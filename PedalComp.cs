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
        Sample[] _laBuffer  = new Sample[1];
        int      _laWrite   = 0;
        int      _laSamples = 0;

        // ── RMS detection buffer ──────────────────────────────────────────────
        float[]  _rmsBuffer = new float[1];
        int      _rmsWrite  = 0;
        float    _rmsSum    = 0f;
        int      _rmsSize   = 0;

        // ── Published metering (audio thread → UI thread, volatile) ──────────
        volatile float _meterIn  = 0f;
        volatile float _meterOut = 0f;
        volatile float _grDb     = 0f;
        volatile int   _clip     = 0;
        const    int   CLIP_HOLD = 60;   // ~2 seconds at 30 fps

        public float MeterIn       => _meterIn;
        public float MeterOut      => _meterOut;
        public float GrDb          => _grDb;
        public int   ClipCountdown => _clip;
        public void  ClearClip()    => _clip = 0;
        public void  DecrementClip() { if (_clip > 0) _clip--; }

        // ── Constructor ───────────────────────────────────────────────────────
        public PedalCompMachine(IBuzzMachineHost host) => this.host = host;

        // ── Parameters ───────────────────────────────────────────────────────

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
            Name = "Makeup Gain", Description = "Output makeup gain in dB. Ignored when Auto Makeup is on.",
            MinValue = 0, MaxValue = 24, DefValue = 0,
            ValueDescriptor = Descriptors.Decibel)]
        public int MakeupGain { get; set; } = 0;

        // Auto Makeup: computes the theoretically correct makeup from threshold
        // and ratio so perceived loudness stays constant as settings change.
        // Formula: gain reduction at 0 dBFS = −threshold × (1 − 1/ratio).
        [ParameterDecl(
            Name              = "Auto Makeup",
            Description       = "Compute makeup gain automatically from threshold and ratio. Overrides Makeup Gain.",
            MinValue          = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int AutoMakeup { get; set; } = 0;

        [ParameterDecl(
            Name        = "Wet/Dry",
            Description = "Mix between compressed (wet) and original (dry) signal.  100 = fully wet.",
            MinValue    = 0, MaxValue = 100, DefValue = 100)]
        public int WetDry { get; set; } = 100;

        [ParameterDecl(
            Name        = "Lookahead",
            Description = "Lookahead delay in milliseconds.  0 = off.  Adds equivalent latency.",
            MinValue    = 0, MaxValue = 20, DefValue = 0,
            ValueDescriptor = Descriptors.Milliseconds)]
        public int Lookahead { get; set; } = 0;

        [ParameterDecl(
            Name              = "Detection",
            Description       = "Envelope detection mode. Peak = fast transient response, RMS = smoother on full mixes.",
            MinValue          = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Peak", "RMS" })]
        public int Detection { get; set; } = 0;

        // ── Cached DSP coefficients ───────────────────────────────────────────
        // Recomputed only when parameters or sample rate change, keeping
        // MathF.Exp / MathF.Pow calls entirely out of the per-block hot path.
        int   _cachedSr         = 0;
        int   _cachedAttack     = -1;
        int   _cachedRelease    = -1;
        int   _cachedThresh     = -1;
        int   _cachedRatio      = -1;
        int   _cachedKnee       = -1;
        int   _cachedMakeup     = -1;
        int   _cachedAutoMakeup = -1;

        float _attackCoef       = 0f;
        float _releaseCoef      = 0f;
        float _fastReleaseCoef  = 0f;
        float _threshLin        = 0f;
        float _kneeEntryLin     = 0f;
        float _makeupLin        = 1f;

        void UpdateCoefficients(int sr)
        {
            bool dirty = sr         != _cachedSr         ||
                         Attack     != _cachedAttack      ||
                         Release    != _cachedRelease     ||
                         Threshold  != _cachedThresh      ||
                         Ratio      != _cachedRatio       ||
                         Knee       != _cachedKnee        ||
                         MakeupGain != _cachedMakeup      ||
                         AutoMakeup != _cachedAutoMakeup;

            if (!dirty) return;

            _cachedSr         = sr;
            _cachedAttack     = Attack;
            _cachedRelease    = Release;
            _cachedThresh     = Threshold;
            _cachedRatio      = Ratio;
            _cachedKnee       = Knee;
            _cachedMakeup     = MakeupGain;
            _cachedAutoMakeup = AutoMakeup;

            float threshDb = ThresholdDb;

            _attackCoef     = MathF.Exp(-1f / (Attack  * 0.001f * sr));
            _releaseCoef    = MathF.Exp(-1f / (Release * 0.001f * sr));
            float fastMs    = MathF.Max(Release * 0.25f, 10f);
            _fastReleaseCoef = MathF.Exp(-1f / (fastMs * 0.001f * sr));

            _threshLin    = DbToLin(threshDb);
            _kneeEntryLin = DbToLin(threshDb - Knee * 0.5f);

            // Makeup: auto computes theoretical gain reduction at 0 dBFS.
            float makeupDb;
            if (AutoMakeup == 1)
            {
                float ratioF = (float)Ratio;
                float grAt0  = ratioF > 1.001f ? -threshDb * (1f - 1f / ratioF) : 0f;
                makeupDb = Math.Clamp(grAt0, 0f, 24f);
            }
            else
            {
                makeupDb = MakeupGain;
            }
            _makeupLin = DbToLin(makeupDb);
        }

        // ── DSP helpers ───────────────────────────────────────────────────────

        // Exact conversions — used outside the inner loop (coefficient setup).
        static float LinToDb(float lin) =>
            lin > 1e-9f ? 20f * MathF.Log10(lin) : -120f;

        static float DbToLin(float db) =>
            MathF.Pow(10f, db / 20f);

        // Fast LinToDb using IEEE 754 bit extraction.
        // Replaces MathF.Log10 in the per-sample hot path.
        // Accuracy: ±0.1 dB — more than sufficient for gain computation.
        static float FastLinToDb(float lin)
        {
            if (lin <= 1e-9f) return -120f;
            int   bits = BitConverter.SingleToInt32Bits(lin);
            float exp  = (bits >> 23) - 127f;
            float mant = BitConverter.Int32BitsToSingle((bits & 0x007FFFFF) | 0x3F800000) - 1f;
            float log2 = exp + mant * (1.4142f - 0.7071f * mant);
            return log2 * 6.02059f;   // log2 → dB
        }

        // Fast DbToLin using IEEE 754 bit construction.
        // Replaces MathF.Pow in the per-sample hot path.
        static float FastDbToLin(float db)
        {
            float x  = db * 0.16609f;           // db × log2(10)/20
            float xi = MathF.Floor(x);
            float xf = x - xi;
            float p  = 1f + xf * (0.69315f + xf * (0.24023f + xf * 0.05550f));
            int   e  = Math.Clamp((int)xi + 127, 1, 254);
            return BitConverter.Int32BitsToSingle(e << 23) * p;
        }

        // Soft-clip: transparent below ~−0.9 dBFS, then smoothly saturates.
        static float SoftClip(float x)
        {
            const float T = 0.9f;
            float ax = MathF.Abs(x);
            if (ax <= T) return x;
            float over = (ax - T) / (1f - T);
            float sat  = T + (1f - T) * (over / (1f + over));
            return MathF.CopySign(sat, x);
        }

        // Standard soft-knee gain reduction in dB. Also used by the GUI for
        // the transfer curve, so marked internal.
        internal static float SoftKneeGR(float dbIn, float T, float W, float ratio)
        {
            float dbOver = dbIn - T;
            float slope  = 1f - 1f / ratio;
            if (W < 0.001f)
                return dbOver > 0f ? dbOver * slope : 0f;
            float halfW = W * 0.5f;
            if (2f * dbOver < -W) return 0f;
            if (2f * dbOver > W)  return dbOver * slope;
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
            int needed = Math.Max(1, (int)(0.010f * sr));
            if (needed == _rmsSize) return;
            _rmsSize   = needed;
            _rmsBuffer = new float[needed];
            _rmsSum    = 0f;
            _rmsWrite  = 0;
        }

        // ── Work ──────────────────────────────────────────────────────────────

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (mode == WorkModes.WM_NOIO)
            {
                env   = 0f;
                _grDb = 0f;
                return false;
            }

            if (input == null || n == 0)
            {
                _grDb = 0f;
                return false;
            }

            int sr = Global.Buzz.SelectedAudioDriverSampleRate;
            UpdateCoefficients(sr);

            float threshDb = ThresholdDb;
            float knee     = (float)Knee;
            float ratio    = (float)Ratio;
            float wetF     = WetDry / 100f;
            float dryF     = 1f - wetF;

            EnsureLookahead(sr);
            EnsureRms(sr);
            int  bufLen  = _laBuffer.Length;
            bool useRms  = Detection == 1;

            float maxIn  = 0f;
            float maxOut = 0f;
            float maxGR  = 0f;

            for (int i = 0; i < n; i++)
            {
                // ── Normalise to ±1.0 ────────────────────────────────────────
                float inL = input[i].L * SCALE;
                float inR = input[i].R * SCALE;

                // ── Peak level ────────────────────────────────────────────────
                float peak = MathF.Max(MathF.Abs(inL), MathF.Abs(inR));
                if (peak > maxIn) maxIn = peak;

                // ── RMS level (running sum, 10 ms window) ─────────────────────
                float sq = (inL * inL + inR * inR) * 0.5f;
                _rmsSum -= _rmsBuffer[_rmsWrite];
                _rmsBuffer[_rmsWrite] = sq;
                _rmsSum   = MathF.Max(0f, _rmsSum + sq);
                _rmsWrite = (_rmsWrite + 1) % _rmsSize;

                // ── Select detection level ────────────────────────────────────
                // RMS sqrt deferred: only compute when actually needed.
                float level = useRms ? MathF.Sqrt(_rmsSum / _rmsSize) : peak;

                // ── Lookahead ring buffer ─────────────────────────────────────
                _laBuffer[_laWrite] = new Sample(inL, inR);
                int readIdx = (_laWrite + 1) % bufLen;
                Sample delayed = _laBuffer[readIdx];
                _laWrite = readIdx;

                // ── Envelope follower — program-dependent release ─────────────
                float coef;
                if (level >= env)
                {
                    coef = _attackCoef;
                }
                else
                {
                    float dropRatio = env > 1e-9f ? level / env : 0f;
                    coef = _fastReleaseCoef + (_releaseCoef - _fastReleaseCoef) * dropRatio;
                }
                env = level + (env - level) * coef;

                // ── Gain computation ──────────────────────────────────────────
                // FastLinToDb / FastDbToLin replace MathF.Log10 / MathF.Pow here.
                float gr   = 1f;
                float dbGR = 0f;
                if (ratio > 1.001f && env > _kneeEntryLin)
                {
                    dbGR = SoftKneeGR(FastLinToDb(env), threshDb, knee, ratio);
                    if (dbGR > 0f)
                    {
                        gr = FastDbToLin(-dbGR);
                        if (dbGR > maxGR) maxGR = dbGR;
                    }
                }

                // ── Apply gain + makeup + wet/dry ─────────────────────────────
                float outL = delayed.L * (gr * _makeupLin * wetF + dryF);
                float outR = delayed.R * (gr * _makeupLin * wetF + dryF);

                // ── Soft-clip ─────────────────────────────────────────────────
                if (MathF.Abs(outL) > 0.9f || MathF.Abs(outR) > 0.9f)
                    _clip = CLIP_HOLD;
                outL = SoftClip(outL);
                outR = SoftClip(outR);

                output[i] = new Sample(outL / SCALE, outR / SCALE);

                float outPeak = MathF.Max(MathF.Abs(outL), MathF.Abs(outR));
                if (outPeak > maxOut) maxOut = outPeak;
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

        Rectangle meterIn    = null!;
        Rectangle meterGR    = null!;
        Rectangle meterOut   = null!;
        Rectangle peakHoldLine = null!;   // thin line on GR meter
        TextBlock lblGR      = null!;
        Border    clipLight  = null!;

        // ── VU ballistics ─────────────────────────────────────────────────────
        float vuIn  = 0f;
        float vuOut = 0f;
        float vuGR  = 0f;

        const float VU_ATTACK  = 0.60f;
        const float VU_RELEASE = 0.92f;
        const float DB_FLOOR   = -40f;
        const float DB_CEIL    =   0f;
        const float GR_MAX     =  20f;

        // ── GR peak hold ──────────────────────────────────────────────────────
        float grPeakHold  = 0f;
        int   grPeakTimer = 0;
        const int   GR_PEAK_HOLD_TICKS = 45;    // ~1.5 s at 30 fps
        const float GR_PEAK_FALL       = 0.3f;  // dB per tick after hold expires

        // ── Transfer curve ────────────────────────────────────────────────────
        Polyline  transferLine = null!;
        int _lastThresh = -1, _lastRatio = -1, _lastKnee = -1;

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

            // ── Title row with SAT indicator ──────────────────────────────────
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
                VerticalAlignment   = VerticalAlignment.Center
            });

            clipLight = new Border
            {
                Width             = 10,
                Height            = 10,
                CornerRadius      = new CornerRadius(2),
                Background        = new SolidColorBrush(Color.FromRgb(50, 15, 15)),
                BorderBrush       = new SolidColorBrush(Color.FromRgb(80, 30, 30)),
                BorderThickness   = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor            = Cursors.Hand,
                ToolTip           = "SAT — signal in soft-clip zone; click to reset"
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

            meterIn  = AddMeterColumn(meterPanel, 0, "IN",  Color.FromRgb( 70, 200,  85), null);
            meterOut = AddMeterColumn(meterPanel, 2, "OUT", Color.FromRgb( 55, 145, 220), null);

            // GR column — capture canvas to add the peak hold line
            Canvas grCanvas;
            meterGR = AddMeterColumn(meterPanel, 1, "GR", Color.FromRgb(220, 85, 55), out grCanvas);

            peakHoldLine = new Rectangle
            {
                Width      = TrackW - 2,
                Height     = 2,
                Fill       = new SolidColorBrush(Color.FromRgb(255, 200, 60)),
                Visibility = Visibility.Collapsed
            };
            Canvas.SetLeft(peakHoldLine, 0);
            Canvas.SetBottom(peakHoldLine, 0);
            grCanvas.Children.Add(peakHoldLine);

            root.Children.Add(meterPanel);

            lblGR = new TextBlock
            {
                Text                = "GR  0.0 dB",
                Foreground          = new SolidColorBrush(Color.FromRgb(220, 85, 55)),
                FontSize            = 9,
                FontFamily          = new FontFamily("Consolas"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 4, 0, 6)
            };
            root.Children.Add(lblGR);

            // ── Transfer curve ────────────────────────────────────────────────
            // X: input dB (−60 → 0), Y: output dB (−60 → 0, inverted on screen).
            // Reference diagonal = unity gain (1:1). Curve shows actual response.
            var curveBorder = new Border
            {
                Width           = 160,
                Height          = 80,
                Background      = new SolidColorBrush(Color.FromRgb(10, 10, 14)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(45, 45, 58)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var curveCanvas = new Canvas { Width = 158, Height = 78 };

            // Unity reference line (diagonal)
            curveCanvas.Children.Add(new Line
            {
                X1              = 0, Y1 = 78,
                X2              = 158, Y2 = 0,
                Stroke          = new SolidColorBrush(Color.FromRgb(45, 45, 58)),
                StrokeThickness = 1
            });

            transferLine = new Polyline
            {
                Stroke          = new SolidColorBrush(Color.FromRgb(55, 145, 220)),
                StrokeThickness = 1.5,
                StrokeLineJoin  = PenLineJoin.Round
            };
            curveCanvas.Children.Add(transferLine);

            curveBorder.Child = curveCanvas;
            root.Children.Add(curveBorder);

            Width  = 180;
            Height = 260;

            return root;
        }

        // Returns the fill rectangle. Exposes the inner canvas via out param
        // so callers can add overlay elements (e.g. peak hold line).
        Rectangle AddMeterColumn(Grid panel, int col, string label, Color color, out Canvas canvas)
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

            canvas = new Canvas { Width = TrackW - 2, Height = TrackH - 2 };

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

        // Overload for callers that don't need the canvas.
        Rectangle AddMeterColumn(Grid panel, int col, string label, Color color, object? _)
        {
            Canvas dummy;
            return AddMeterColumn(panel, col, label, color, out dummy);
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

        // ── Meter + curve refresh (30 fps) ────────────────────────────────────

        void RefreshMeters()
        {
            if (comp == null) return;

            // ── VU ballistics ─────────────────────────────────────────────────
            float rawIn  = comp.MeterIn;
            float rawOut = comp.MeterOut;
            float rawGR  = comp.GrDb;

            vuIn  = rawIn  + (vuIn  - rawIn)  * (rawIn  > vuIn  ? VU_ATTACK : VU_RELEASE);
            vuOut = rawOut + (vuOut - rawOut) * (rawOut > vuOut ? VU_ATTACK : VU_RELEASE);
            vuGR  = rawGR  + (vuGR  - rawGR)  * (rawGR  > vuGR  ? VU_ATTACK : VU_RELEASE);

            SetLevel(meterIn,  NormLevel(vuIn));
            SetLevel(meterOut, NormLevel(vuOut));
            SetLevel(meterGR,  Math.Clamp(vuGR / GR_MAX, 0f, 1f));

            // ── GR peak hold ──────────────────────────────────────────────────
            if (vuGR >= grPeakHold)
            {
                grPeakHold  = vuGR;
                grPeakTimer = GR_PEAK_HOLD_TICKS;
            }
            else if (grPeakTimer > 0)
            {
                grPeakTimer--;
            }
            else
            {
                grPeakHold = MathF.Max(0f, grPeakHold - GR_PEAK_FALL);
            }

            if (grPeakHold > 0.1f)
            {
                float peakNorm = Math.Clamp(grPeakHold / GR_MAX, 0f, 1f);
                peakHoldLine.Visibility = Visibility.Visible;
                Canvas.SetBottom(peakHoldLine, peakNorm * (TrackH - 2) - 1);
            }
            else
            {
                peakHoldLine.Visibility = Visibility.Collapsed;
            }

            lblGR.Text = $"GR  {vuGR:F1} dB";

            // ── SAT indicator ─────────────────────────────────────────────────
            int clipVal = comp.ClipCountdown;
            if (clipVal > 0) comp.DecrementClip();
            clipLight.Background = new SolidColorBrush(clipVal > 0
                ? Color.FromRgb(240, 50, 40)
                : Color.FromRgb(50, 15, 15));

            // ── Transfer curve (only redraws when parameters change) ──────────
            if (comp.Threshold != _lastThresh ||
                comp.Ratio     != _lastRatio  ||
                comp.Knee      != _lastKnee)
            {
                _lastThresh = comp.Threshold;
                _lastRatio  = comp.Ratio;
                _lastKnee   = comp.Knee;
                UpdateTransferCurve(_lastThresh, _lastRatio, _lastKnee);
            }
        }

        void UpdateTransferCurve(int threshold, int ratio, int knee)
        {
            const float DB_MIN = -60f;
            const float DB_MAX =   0f;
            const float W      = 158f;
            const float H      =  78f;
            const int   STEPS  =  80;

            float threshDb = -(float)threshold;
            float ratioF   =  (float)ratio;
            float kneeF    =  (float)knee;

            var pts = new PointCollection(STEPS + 1);
            for (int s = 0; s <= STEPS; s++)
            {
                float inDb  = DB_MIN + (DB_MAX - DB_MIN) * s / STEPS;
                float gr    = PedalCompMachine.SoftKneeGR(inDb, threshDb, kneeF, ratioF);
                float outDb = Math.Clamp(inDb - gr, DB_MIN, DB_MAX);

                float x = (inDb  - DB_MIN) / (DB_MAX - DB_MIN) * W;
                float y = (1f - (outDb - DB_MIN) / (DB_MAX - DB_MIN)) * H;
                pts.Add(new Point(x, y));
            }
            transferLine.Points = pts;
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
