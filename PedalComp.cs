using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WDE.PedalComp
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Machine declaration
    // ─────────────────────────────────────────────────────────────────────────
    [MachineDecl(Name = "Pedal Comp", ShortName = "PComp", Author = "WDE", MaxTracks = 0)]
    public class PedalCompMachine : IBuzzMachine, INotifyPropertyChanged
    {
        readonly IBuzzMachineHost host;

        // ── Sample scale ──────────────────────────────────────────────────────
        // ReBuzz passes samples as ±32768 floats. Divide to get ±1.0.
        const float SCALE = 1f / 32768f;

        // ── Envelope follower state (normalised linear) ───────────────────────
        float env = 0f;

        // ── Lookahead circular buffer ─────────────────────────────────────────
        // Stores normalised stereo samples so the envelope detector can "see"
        // peaks before the delayed audio arrives at the output.
        Sample[] _laBuffer  = new Sample[1];
        int      _laWrite   = 0;
        int      _laSamples = 0;

        // ── RMS detection buffer ──────────────────────────────────────────────
        // Running sum of squared samples over a ~10 ms window.
        float[]  _rmsBuffer = new float[1];
        int      _rmsWrite  = 0;
        float    _rmsSum    = 0f;
        int      _rmsSize   = 0;

        // ── Published metering (audio thread → UI thread, volatile) ──────────
        volatile float _meterIn  = 0f;
        volatile float _meterOut = 0f;
        volatile float _grDb     = 0f;
        // Clip countdown: audio thread sets to CLIP_HOLD when output clips.
        // UI thread decrements each tick (~33 ms); light is on while > 0.
        volatile int   _clip     = 0;
        const    int   CLIP_HOLD = 60;   // ~2 seconds at 30 fps

        public float MeterIn  => _meterIn;
        public float MeterOut => _meterOut;
        public float GrDb     => _grDb;
        public int   ClipCountdown  => _clip;
        public void  ClearClip()    => _clip = 0;
        public void  DecrementClip() { if (_clip > 0) _clip--; }

        // ── Constructor ───────────────────────────────────────────────────────
        public PedalCompMachine(IBuzzMachineHost host) => this.host = host;

        // ── Parameters ───────────────────────────────────────────────────────

        // Threshold: stored value = dB below 0 dBFS (higher → lower → more compression).
        int _threshStored = 18;
        [ParameterDecl(
            Name        = "Threshold",
            Description = "Compression threshold.  0 = 0 dBFS (no effect), 60 = −60 dBFS (maximum).",
            MinValue    = 0, MaxValue = 60, DefValue = 18)]
        public int Threshold { get => _threshStored; set => _threshStored = value; }
        float ThresholdDb => -(float)_threshStored;

        [ParameterDecl(
            Name        = "Ratio",
            Description = "Compression ratio N:1.  1 = no compression, 30 = near-limiting.",
            MinValue    = 1, MaxValue = 30, DefValue = 4)]
        public int Ratio { get; set; } = 4;

        // Knee: soft-knee width in dB centred on the threshold.
        //   0 = hard knee (original behaviour)
        //   1–12 = progressively softer transition zone
        [ParameterDecl(
            Name        = "Knee",
            Description = "Soft-knee width in dB.  0 = hard knee, 12 = widest soft knee.",
            MinValue    = 0, MaxValue = 12, DefValue = 0)]
        public int Knee { get; set; } = 0;

        [ParameterDecl(
            Name = "Attack", Description = "Attack time in milliseconds.",
            MinValue = 1, MaxValue = 200, DefValue = 10,
            ValueDescriptor = Descriptors.Milliseconds)]
        public int Attack { get; set; } = 10;

        [ParameterDecl(
            Name = "Release", Description = "Release time in milliseconds.",
            MinValue = 10, MaxValue = 2000, DefValue = 100,
            ValueDescriptor = Descriptors.Milliseconds)]
        public int Release { get; set; } = 100;

        [ParameterDecl(
            Name = "Makeup Gain", Description = "Output makeup gain in dB.",
            MinValue = 0, MaxValue = 24, DefValue = 0,
            ValueDescriptor = Descriptors.Decibel)]
        public int MakeupGain { get; set; } = 0;

        // Wet/Dry: 100 = fully compressed, 0 = dry (original signal) only.
        // Both paths are time-aligned via the lookahead buffer.
        [ParameterDecl(
            Name        = "Wet/Dry",
            Description = "Mix between compressed (wet) and original (dry) signal.  100 = fully wet.",
            MinValue    = 0, MaxValue = 100, DefValue = 100)]
        public int WetDry { get; set; } = 100;

        // Lookahead: delays the audio path so the envelope follower sees peaks
        // before they arrive at the gain stage, allowing perfect transient catch.
        [ParameterDecl(
            Name        = "Lookahead",
            Description = "Lookahead delay in milliseconds.  0 = off.  Adds equivalent latency.",
            MinValue    = 0, MaxValue = 20, DefValue = 0,
            ValueDescriptor = Descriptors.Milliseconds)]
        public int Lookahead { get; set; } = 0;

        // Detection mode: 0 = Peak, 1 = RMS.
        // Peak catches transients precisely; RMS averages energy and sounds
        // smoother on full mixes and bus material.
        [ParameterDecl(
            Name        = "Detection",
            Description = "Envelope detection mode.  0 = Peak,  1 = RMS (10 ms window).",
            MinValue    = 0, MaxValue = 1, DefValue = 0)]
        public int Detection { get; set; } = 0;

        // ── DSP helpers ───────────────────────────────────────────────────────

        static float LinToDb(float lin) =>
            lin > 1e-9f ? 20f * MathF.Log10(lin) : -120f;

        static float DbToLin(float db) =>
            MathF.Pow(10f, db / 20f);

        // Standard soft-knee gain reduction in dB.
        // dbIn  = signal level in dB
        // T     = threshold in dB
        // W     = knee width in dB (total, centred on T)
        // ratio = compression ratio
        static float SoftKneeGR(float dbIn, float T, float W, float ratio)
        {
            float dbOver = dbIn - T;
            float slope  = 1f - 1f / ratio;

            if (W < 0.001f)
                // Hard knee
                return dbOver > 0f ? dbOver * slope : 0f;

            float halfW = W * 0.5f;
            if (2f * dbOver < -W)
                return 0f;                          // below knee zone
            if (2f * dbOver > W)
                return dbOver * slope;              // above knee zone

            // Inside knee zone: quadratic interpolation
            float t = dbOver + halfW;
            return t * t * slope / W;
        }

        // ── Lookahead buffer management ───────────────────────────────────────

        void EnsureLookahead(int sr)
        {
            int needed = Math.Max(0, (int)(Lookahead * 0.001f * sr));
            if (needed == _laSamples && _laBuffer.Length == needed + 1) return;

            _laSamples = needed;
            _laBuffer  = new Sample[needed + 1];
            _laWrite   = 0;
        }

        // ── RMS buffer management ─────────────────────────────────────────────

        void EnsureRms(int sr)
        {
            int needed = Math.Max(1, (int)(0.010f * sr));  // 10 ms window
            if (needed == _rmsSize) return;

            _rmsSize   = needed;
            _rmsBuffer = new float[needed];
            _rmsSum    = 0f;
            _rmsWrite  = 0;
        }

        // ── Work ──────────────────────────────────────────────────────────────

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // No input signal — tell ReBuzz we produced nothing so it can
            // skip calling us entirely and drop CPU usage to zero.
            if (mode == WorkModes.WM_NOIO)
            {
                env    = 0f;
                _grDb  = 0f;
                return false;
            }

            if (input == null || n == 0)
            {
                _grDb = 0f;
                return false;
            }

            int   sr          = Global.Buzz.SelectedAudioDriverSampleRate;
            float attackCoef  = MathF.Exp(-1f / (Attack  * 0.001f * sr));
            float releaseCoef = MathF.Exp(-1f / (Release * 0.001f * sr));
            float threshDb    = ThresholdDb;
            float threshLin   = DbToLin(threshDb);
            float knee        = (float)Knee;
            float ratio       = (float)Ratio;
            float makeup      = DbToLin(MakeupGain);
            float wetF        = WetDry  / 100f;
            float dryF        = 1f - wetF;

            EnsureLookahead(sr);
            EnsureRms(sr);
            int bufLen  = _laBuffer.Length;
            bool useRms = Detection == 1;

            float maxIn  = 0f;
            float maxOut = 0f;
            float maxGR  = 0f;

            for (int i = 0; i < n; i++)
            {
                // ── Normalise current input ───────────────────────────────────
                float inL = input[i].L * SCALE;
                float inR = input[i].R * SCALE;

                // ── Peak level (always computed for IN meter) ─────────────────
                float peak = MathF.Max(MathF.Abs(inL), MathF.Abs(inR));
                if (peak > maxIn) maxIn = peak;

                // ── RMS level (running sum over 10 ms window) ─────────────────
                float sq = (inL * inL + inR * inR) * 0.5f;
                _rmsSum -= _rmsBuffer[_rmsWrite];
                _rmsBuffer[_rmsWrite] = sq;
                _rmsSum  = MathF.Max(0f, _rmsSum + sq);   // clamp to avoid -ve from float drift
                _rmsWrite = (_rmsWrite + 1) % _rmsSize;
                float rmsLevel = MathF.Sqrt(_rmsSum / _rmsSize);

                // ── Select detection level for envelope follower ──────────────
                float level = useRms ? rmsLevel : peak;

                // ── Write current input into lookahead ring ───────────────────
                _laBuffer[_laWrite] = new Sample(inL, inR);

                // ── Read delayed sample (laSamples behind write) ──────────────
                // When laSamples = 0 the read pointer equals the write pointer,
                // returning the sample we just stored → zero latency path.
                int readIdx = (_laWrite + 1) % bufLen;
                Sample delayed = _laBuffer[readIdx];

                _laWrite = readIdx;   // advance write to old read position

                // ── Envelope follower on selected detection level ─────────────
                float coef = level > env ? attackCoef : releaseCoef;
                env = level + (env - level) * coef;

                // ── Gain computation (soft or hard knee) ──────────────────────
                float gr   = 1f;
                float dbGR = 0f;
                if (ratio > 1.001f && env > threshLin * DbToLin(-knee * 0.5f))
                {
                    dbGR = SoftKneeGR(LinToDb(env), threshDb, knee, ratio);
                    if (dbGR > 0f)
                    {
                        gr = DbToLin(-dbGR);
                        if (dbGR > maxGR) maxGR = dbGR;
                    }
                }

                // ── Apply gain + makeup + wet/dry to the delayed signal ───────
                // dry = delayed unchanged; wet = delayed * gr * makeup
                // Both paths use the same delayed sample → always time-aligned.
                float outL = delayed.L * (gr * makeup * wetF + dryF);
                float outR = delayed.R * (gr * makeup * wetF + dryF);
                output[i]  = new Sample(outL / SCALE, outR / SCALE);

                float outPeak = MathF.Max(MathF.Abs(outL), MathF.Abs(outR));
                if (outPeak > maxOut) maxOut = outPeak;
                if (outPeak > 1f) _clip = CLIP_HOLD;
            }

            _meterIn  = maxIn;
            _meterOut = maxOut;
            _grDb     = maxGR;

            return true;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  GUI
    // ─────────────────────────────────────────────────────────────────────────

    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new PedalCompGui();
    }

    public class PedalCompGui : UserControl, IMachineGUI
    {
        IMachine?         machine;
        PedalCompMachine? comp;
        DispatcherTimer?  timer;

        Rectangle meterIn   = null!;
        Rectangle meterGR   = null!;
        Rectangle meterOut  = null!;
        TextBlock lblGR     = null!;
        Border    clipLight = null!;   // clip indicator

        // ── VU ballistics (UI thread) ─────────────────────────────────────────
        float vuIn  = 0f;
        float vuOut = 0f;
        float vuGR  = 0f;

        const float VU_ATTACK  = 0.60f;
        const float VU_RELEASE = 0.92f;

        const float DB_FLOOR = -40f;
        const float DB_CEIL  =   0f;
        const float GR_MAX   = 20f;

        const double TrackH = 80;
        const double TrackW = 22;

        // ── IMachineGUI ───────────────────────────────────────────────────────

        public IMachine? Machine
        {
            get => machine;
            set
            {
                machine = value;
                if (machine != null)
                {
                    comp = (PedalCompMachine)machine.ManagedMachine;
                    timer ??= CreateTimer();
                    timer.Start();
                }
                else
                {
                    timer?.Stop();
                }
            }
        }

        // ── Construction ─────────────────────────────────────────────────────

        public PedalCompGui() => Content = BuildUI();

        UIElement BuildUI()
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 24));

            var root = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin      = new Thickness(10, 8, 10, 8)
            };

            // ── Title row with clip indicator ─────────────────────────────────
            var titleRow = new Grid();
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            titleRow.Children.Add(new TextBlock
            {
                Text                = "P E D A L  C O M P",
                Foreground          = new SolidColorBrush(Color.FromRgb(170, 170, 185)),
                FontSize            = 8.5,
                FontFamily          = new FontFamily("Consolas"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 0)
            });

            // Clip indicator — small rectangle, click to reset
            clipLight = new Border
            {
                Width           = 10,
                Height          = 10,
                CornerRadius    = new CornerRadius(2),
                Background      = new SolidColorBrush(Color.FromRgb(50, 15, 15)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(80, 30, 30)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin          = new Thickness(0, 0, 0, 0),
                Cursor          = Cursors.Hand,
                ToolTip         = "CLIP — click to reset"
            };
            clipLight.MouseLeftButtonDown += (_, _) => comp?.ClearClip();
            Grid.SetColumn(clipLight, 1);
            titleRow.Children.Add(clipLight);

            titleRow.Margin = new Thickness(0, 0, 0, 6);
            root.Children.Add(titleRow);

            // ── Meter columns ─────────────────────────────────────────────────
            var meterPanel = new Grid { Height = TrackH + 16 };
            meterPanel.ColumnDefinitions.Add(new ColumnDefinition());
            meterPanel.ColumnDefinitions.Add(new ColumnDefinition());
            meterPanel.ColumnDefinitions.Add(new ColumnDefinition());

            meterIn  = AddMeterColumn(meterPanel, 0, "IN",  Color.FromRgb( 70, 200,  85));
            meterGR  = AddMeterColumn(meterPanel, 1, "GR",  Color.FromRgb(220,  85,  55));
            meterOut = AddMeterColumn(meterPanel, 2, "OUT", Color.FromRgb( 55, 145, 220));

            root.Children.Add(meterPanel);

            lblGR = new TextBlock
            {
                Text                = "GR  0.0 dB",
                Foreground          = new SolidColorBrush(Color.FromRgb(220, 85, 55)),
                FontSize            = 9,
                FontFamily          = new FontFamily("Consolas"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 4, 0, 0)
            };
            root.Children.Add(lblGR);

            Width  = 180;
            Height = 155;

            return root;
        }

        Rectangle AddMeterColumn(Grid panel, int col, string label, Color color)
        {
            var column = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            column.Children.Add(new TextBlock
            {
                Text                = label,
                Foreground          = new SolidColorBrush(Color.FromRgb(120, 120, 135)),
                FontSize            = 7.5,
                FontFamily          = new FontFamily("Consolas"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 2)
            });

            var track = new Border
            {
                Width           = TrackW,
                Height          = TrackH,
                Background      = new SolidColorBrush(Color.FromRgb(10, 10, 14)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(45, 45, 58)),
                BorderThickness = new Thickness(1),
                ClipToBounds    = true
            };

            var canvas = new Canvas { Width = TrackW - 2, Height = TrackH - 2 };

            var fill = new Rectangle
            {
                Width  = TrackW - 2,
                Height = 0,
                Fill   = new LinearGradientBrush
                {
                    StartPoint    = new Point(0, 1),
                    EndPoint      = new Point(0, 0),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop(DimColor(color, 0.28f), 0.00),
                        new GradientStop(color,                  0.72),
                        new GradientStop(Colors.White,           1.00)
                    }
                }
            };
            Canvas.SetLeft(fill, 0);
            Canvas.SetBottom(fill, 0);
            canvas.Children.Add(fill);
            track.Child = canvas;
            column.Children.Add(track);

            Grid.SetColumn(column, col);
            panel.Children.Add(column);
            return fill;
        }

        static Color DimColor(Color c, float f) =>
            Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

        // ── Timer ─────────────────────────────────────────────────────────────

        DispatcherTimer CreateTimer()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            t.Tick += (_, _) => RefreshMeters();
            return t;
        }

        // ── VU refresh ────────────────────────────────────────────────────────

        void RefreshMeters()
        {
            if (comp == null) return;

            float rawIn  = comp.MeterIn;
            float rawOut = comp.MeterOut;
            float rawGR  = comp.GrDb;

            vuIn  = rawIn  + (vuIn  - rawIn)  * (rawIn  > vuIn  ? VU_ATTACK : VU_RELEASE);
            vuOut = rawOut + (vuOut - rawOut) * (rawOut > vuOut ? VU_ATTACK : VU_RELEASE);
            vuGR  = rawGR  + (vuGR  - rawGR)  * (rawGR  > vuGR  ? VU_ATTACK : VU_RELEASE);

            SetLevel(meterIn,  NormLevel(vuIn));
            SetLevel(meterOut, NormLevel(vuOut));
            SetLevel(meterGR,  Math.Clamp(vuGR / GR_MAX, 0f, 1f));

            lblGR.Text = $"GR  {vuGR:F1} dB";

            // Clip indicator: decrement countdown each tick; light on while > 0.
            // Click resets immediately (handled by MouseLeftButtonDown on clipLight).
            int clipVal = comp.ClipCountdown;
            if (clipVal > 0) comp.DecrementClip();
            clipLight.Background = new SolidColorBrush(clipVal > 0
                ? Color.FromRgb(240, 50, 40)
                : Color.FromRgb(50, 15, 15));
        }

        static float NormLevel(float lin)
        {
            if (lin < 1e-9f) return 0f;
            float db = 20f * MathF.Log10(lin);
            return Math.Clamp((db - DB_FLOOR) / (DB_CEIL - DB_FLOOR), 0f, 1f);
        }

        void SetLevel(Rectangle rect, float norm) =>
            rect.Height = norm * (TrackH - 2);
    }
}
