// Pedal Comp - stereo soft-knee compressor for ReBuzz.
//
// Copyright (C) 2026 thepedal
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, version 3. See the LICENSE file for the full text.
//
// Additional permission under GNU GPL version 3 section 7:
//
// As a special exception, the copyright holders of this program give you
// permission to combine this program with the ReBuzz and Jeskola Buzz host
// applications and their interface libraries (including, without limitation,
// BuzzGUI.Interfaces, BuzzGUI.Common, and Buzz.MachineInterface), and to
// convey the resulting combined work. This permission applies regardless of
// the licence terms (or absence thereof) under which those host components
// are made available. You must still comply with the GNU GPL version 3 in
// all respects for all of the program's own code.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace WDE.PedalComp
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Machine declaration
    // ─────────────────────────────────────────────────────────────────────────
    [MachineDecl(Name = "Pedal Comp", ShortName = "PComp", Author = "thepedal", MaxTracks = 0)]
    public class PedalCompMachine : IBuzzMachine, INotifyPropertyChanged
    {
        internal const string Version = "1.5";

        readonly IBuzzMachineHost host;

        // ── Sample scale ──────────────────────────────────────────────────────
        // ReBuzz passes samples as ±32768 floats. Divide to get ±1.0.
        const float SCALE = 1f / 32768f;

        // ── Envelope follower state (normalised linear) ───────────────────────
        float env = 0f;

        // ── Lookahead circular buffer ─────────────────────────────────────────
        Sample[] _laBuffer  = new Sample[1];
        int      _laWrite   = 0;
        volatile int _laSamples = 0;   // read by the GUI readout

        // ── RMS detection buffer ──────────────────────────────────────────────
        float[]  _rmsBuffer = new float[1];
        int      _rmsWrite  = 0;
        float    _rmsSum    = 0f;
        int      _rmsSize   = 0;

        // True after Work() has seen no valid input; the next valid block
        // starts from cleared state instead of replaying stale audio.
        bool _idle = true;

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

        // Detector level in dBFS at the end of the last block (transfer-curve dot).
        volatile float _detDb = -120f;
        public float DetectorDb => _detDb;
        public int   LookaheadSamples => _laSamples;

        // ── Level history (audio thread writes, GUI reads) ────────────────────
        // One column per 10 ms: max input peak, max output peak, max gain
        // reduction. Kept on the machine so it survives closing the window.
        // _histCount is free-running; column k lives at k & (HistSize - 1).
        // The GUI reads at most HistSize/2 columns back, so the writer never
        // reaches a column being read.
        internal const int HistSize = 1024;
        internal const int HistColsPerSec = 100;
        internal readonly float[] HistIn  = new float[HistSize];
        internal readonly float[] HistOut = new float[HistSize];
        internal readonly float[] HistGr  = new float[HistSize];
        int   _histCount;
        int   _colLeft;
        float _colIn, _colOut, _colGr;

        internal int HistoryCount => Volatile.Read(ref _histCount);

        void PushColumn()
        {
            int k = _histCount & (HistSize - 1);
            HistIn[k]  = _colIn;
            HistOut[k] = _colOut;
            HistGr[k]  = _colGr;
            _colIn = _colOut = _colGr = 0f;
            Volatile.Write(ref _histCount, _histCount + 1);
        }

        int ColumnLength(int sr) => Math.Max(1, sr / HistColsPerSec);

        // Advance the history by n silent samples (input muted or absent).
        void AdvanceSilent(int n, int sr)
        {
            while (n > 0)
            {
                if (_colLeft <= 0) _colLeft = ColumnLength(sr);
                int take = Math.Min(n, _colLeft);
                _colLeft -= take;
                n        -= take;
                if (_colLeft == 0) PushColumn();
            }
        }

        // ── Constructor ───────────────────────────────────────────────────────
        // Store the host only; host state is not ready yet.
        public PedalCompMachine(IBuzzMachineHost host) => this.host = host;

        // ── Parameters ───────────────────────────────────────────────────────
        // Order, ranges and names unchanged since 1.0, so songs and presets
        // carry over. ReBuzz matches saved parameters by name on song load,
        // so Wet/Dry keeps its name (the Buzz 1503 build calls it Wet Dry).
        // Labels: ValueDescriptions, the same text as the Buzz 1503 build.
        // Attack and Release use Descriptors.Milliseconds, which ReBuzz
        // formats correctly ("10 ms"). Makeup no longer uses
        // Descriptors.Decibel: ReBuzz reads that value as an amplitude, so
        // 6 showed as 15.6 dB and 0 as -inf.

        int _threshStored = 18;
        [ParameterDecl(
            Name        = "Threshold",
            Description = "Compression threshold in dB below full scale. 0 = 0 dBFS (no effect), 60 = -60 dBFS.",
            MinValue    = 0, MaxValue = 60, DefValue = 18,
            ValueDescriptions = new[] {
            "0 dB", "-1 dB", "-2 dB", "-3 dB", "-4 dB", "-5 dB", "-6 dB", "-7 dB", "-8 dB",
            "-9 dB", "-10 dB", "-11 dB", "-12 dB", "-13 dB", "-14 dB", "-15 dB", "-16 dB",
            "-17 dB", "-18 dB", "-19 dB", "-20 dB", "-21 dB", "-22 dB", "-23 dB", "-24 dB",
            "-25 dB", "-26 dB", "-27 dB", "-28 dB", "-29 dB", "-30 dB", "-31 dB", "-32 dB",
            "-33 dB", "-34 dB", "-35 dB", "-36 dB", "-37 dB", "-38 dB", "-39 dB", "-40 dB",
            "-41 dB", "-42 dB", "-43 dB", "-44 dB", "-45 dB", "-46 dB", "-47 dB", "-48 dB",
            "-49 dB", "-50 dB", "-51 dB", "-52 dB", "-53 dB", "-54 dB", "-55 dB", "-56 dB",
            "-57 dB", "-58 dB", "-59 dB", "-60 dB" })]
        public int Threshold { get => _threshStored; set => _threshStored = value; }
        float ThresholdDb => -(float)_threshStored;

        [ParameterDecl(
            Name        = "Ratio",
            Description = "Compression ratio N to 1. 1 = no compression, 30 = near-limiting.",
            MinValue    = 1, MaxValue = 30, DefValue = 4)]
        public int Ratio { get; set; } = 4;

        [ParameterDecl(
            Name        = "Knee",
            Description = "Soft-knee width in dB. 0 = hard knee, 12 = widest soft knee.",
            MinValue    = 0, MaxValue = 12, DefValue = 0,
            ValueDescriptions = new[] {
            "Hard", "1 dB", "2 dB", "3 dB", "4 dB", "5 dB", "6 dB", "7 dB", "8 dB", "9 dB",
            "10 dB", "11 dB", "12 dB" })]
        public int Knee { get; set; } = 0;

        [ParameterDecl(
            Name = "Attack", Description = "Attack time in ms.",
            MinValue = 1, MaxValue = 200, DefValue = 10,
            ValueDescriptor = Descriptors.Milliseconds)]
        public int Attack { get; set; } = 10;

        [ParameterDecl(
            Name = "Release", Description = "Release time in ms. Shortens automatically on sudden drops.",
            MinValue = 10, MaxValue = 2000, DefValue = 100,
            ValueDescriptor = Descriptors.Milliseconds)]
        public int Release { get; set; } = 100;

        [ParameterDecl(
            Name = "Makeup Gain", Description = "Output makeup gain in dB. Ignored when Auto Makeup is on.",
            MinValue = 0, MaxValue = 24, DefValue = 0,
            ValueDescriptions = new[] {
            "0 dB", "+1 dB", "+2 dB", "+3 dB", "+4 dB", "+5 dB", "+6 dB", "+7 dB", "+8 dB",
            "+9 dB", "+10 dB", "+11 dB", "+12 dB", "+13 dB", "+14 dB", "+15 dB", "+16 dB",
            "+17 dB", "+18 dB", "+19 dB", "+20 dB", "+21 dB", "+22 dB", "+23 dB", "+24 dB" })]
        public int MakeupGain { get; set; } = 0;

        // Auto Makeup: computes the theoretically correct makeup from threshold
        // and ratio so perceived loudness stays constant as settings change.
        // Formula: gain reduction at 0 dBFS = -threshold * (1 - 1/ratio).
        [ParameterDecl(
            Name              = "Auto Makeup",
            Description       = "Compute makeup gain automatically from threshold and ratio. Overrides Makeup Gain.",
            MinValue          = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Off", "On" })]
        public int AutoMakeup { get; set; } = 0;

        [ParameterDecl(
            Name        = "Wet/Dry",
            Description = "Mix between compressed (wet) and original (dry) signal. 100 = fully wet.",
            MinValue    = 0, MaxValue = 100, DefValue = 100,
            ValueDescriptions = new[] {
            "0%", "1%", "2%", "3%", "4%", "5%", "6%", "7%", "8%", "9%", "10%", "11%", "12%", "13%",
            "14%", "15%", "16%", "17%", "18%", "19%", "20%", "21%", "22%", "23%", "24%", "25%",
            "26%", "27%", "28%", "29%", "30%", "31%", "32%", "33%", "34%", "35%", "36%", "37%",
            "38%", "39%", "40%", "41%", "42%", "43%", "44%", "45%", "46%", "47%", "48%", "49%",
            "50%", "51%", "52%", "53%", "54%", "55%", "56%", "57%", "58%", "59%", "60%", "61%",
            "62%", "63%", "64%", "65%", "66%", "67%", "68%", "69%", "70%", "71%", "72%", "73%",
            "74%", "75%", "76%", "77%", "78%", "79%", "80%", "81%", "82%", "83%", "84%", "85%",
            "86%", "87%", "88%", "89%", "90%", "91%", "92%", "93%", "94%", "95%", "96%", "97%",
            "98%", "99%", "100%" })]
        public int WetDry { get; set; } = 100;

        [ParameterDecl(
            Name        = "Lookahead",
            Description = "Lookahead delay in milliseconds. 0 = off. Adds equivalent latency.",
            MinValue    = 0, MaxValue = 20, DefValue = 0,
            ValueDescriptions = new[] {
            "Off", "1 ms", "2 ms", "3 ms", "4 ms", "5 ms", "6 ms", "7 ms", "8 ms", "9 ms", "10 ms",
            "11 ms", "12 ms", "13 ms", "14 ms", "15 ms", "16 ms", "17 ms", "18 ms", "19 ms",
            "20 ms" })]
        public int Lookahead { get; set; } = 0;

        [ParameterDecl(
            Name              = "Detection",
            Description       = "Envelope detection mode. Peak = fast transient response, RMS = smoother on full mixes.",
            MinValue          = 0, MaxValue = 1, DefValue = 0,
            ValueDescriptions = new[] { "Peak", "RMS" })]
        public int Detection { get; set; } = 0;

        // ── Right-click menu ──────────────────────────────────────────────────
        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                yield return new MenuItemVM()
                {
                    Text = "About...",
                    Command = new SimpleCommand()
                    {
                        CanExecuteDelegate = p => true,
                        ExecuteDelegate = p => MessageBox.Show(
                            "Pedal Comp   v" + Version + "\n\n" +
                            "Stereo soft-knee compressor with peak or RMS detection,\n" +
                            "lookahead, auto makeup, wet-dry mix and output soft-clip.\n\n" +
                            "Author: thepedal\n" +
                            "github.com/thepedal/pedal-comp\n" +
                            "GNU General Public License v3.0",
                            "About Pedal Comp")
                    }
                };
            }
        }

        // ── Cached DSP coefficients ───────────────────────────────────────────
        // Recomputed only when parameters or sample rate change, keeping
        // Exp / Pow calls entirely out of the per-block hot path.
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

            _makeupLin = DbToLin(EffectiveMakeupDb(Threshold, Ratio, MakeupGain, AutoMakeup));
        }

        // Makeup in dB as applied. Auto computes the theoretical gain reduction
        // at 0 dBFS. Shared with the GUI readout.
        internal static float EffectiveMakeupDb(int threshold, int ratio, int makeup, int auto)
        {
            if (auto != 1) return makeup;
            float ratioF = (float)ratio;
            float grAt0  = ratioF > 1.001f ? threshold * (1f - 1f / ratioF) : 0f;
            return Math.Clamp(grAt0, 0f, 24f);
        }

        // ── DSP helpers ───────────────────────────────────────────────────────

        // Exact conversions — used outside the inner loop (coefficient setup).
        static float LinToDb(float lin) =>
            lin > 1e-9f ? 20f * MathF.Log10(lin) : -120f;

        static float DbToLin(float db) =>
            MathF.Pow(10f, db / 20f);

        // Fast LinToDb using IEEE 754 bit extraction.
        // Replaces Log10 in the per-sample hot path. Accuracy: ±0.01 dB.
        // The mantissa polynomial approximates log2(1+m) on [0,1) and is exact
        // at both ends (p(0)=0, p(1)=1), so the result is continuous across
        // every power-of-two boundary. The previous quadratic,
        // m*(1.4142 - 0.7071*m), reached only 0.7071 at m→1: a 1.76 dB jump
        // every 6.02 dB, which turned into audible gain steps (clicks).
        static float FastLinToDb(float lin)
        {
            if (lin <= 1e-9f) return -120f;
            int   bits = BitConverter.SingleToInt32Bits(lin);
            float exp  = (bits >> 23) - 127f;
            float mant = BitConverter.Int32BitsToSingle((bits & 0x007FFFFF) | 0x3F800000) - 1f;
            float log2 = exp + mant * (1.420865f + mant * (-0.577251f + mant * 0.156386f));
            return log2 * 6.02059f;   // log2 → dB
        }

        // Fast DbToLin using IEEE 754 bit construction.
        // Replaces Pow in the per-sample hot path.
        static float FastDbToLin(float db)
        {
            float x  = db * 0.16609f;           // db × log2(10)/20
            float xi = MathF.Floor(x);
            float xf = x - xi;
            // Cubic for 2^xf on [0,1), exact at both ends (p(1)=2) so there is
            // no step at integer boundaries. Accuracy: ±0.002 dB.
            float p  = 1f + xf * (0.695928f + xf * (0.224946f + xf * 0.079125f));
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
            // Quadratic knee: (x + W/2)^2 / (2W) * slope. Meets the hard-knee
            // line with matching value and slope at both edges of the knee.
            // (Up to 1.3 this divided by W, not 2W: double the gain reduction
            // inside the knee and a step of W/2 * slope dB at its top edge.)
            float t = dbOver + halfW;
            return t * t * slope / (2f * W);
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

        static int SampleRate()
        {
            int sr = Global.Buzz.SelectedAudioDriverSampleRate;
            return sr > 0 ? sr : 44100;
        }

        // Called on every block without valid input. Clears the detector and
        // meters, and on the first idle block empties the delay lines so the
        // next audio does not start with stale samples from before the gap.
        void GoIdle(int n)
        {
            env       = 0f;
            _grDb     = 0f;
            _meterIn  = 0f;
            _meterOut = 0f;
            _detDb    = -120f;
            if (n > 0)
            {
                AdvanceSilent(n, SampleRate());
            }
            if (_idle) return;
            _idle = true;
            Array.Clear(_laBuffer, 0, _laBuffer.Length);
            Array.Clear(_rmsBuffer, 0, _rmsBuffer.Length);
            _rmsSum   = 0f;
        }

        // ── Work ──────────────────────────────────────────────────────────────

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // Input is valid only when the READ flag is set. WM_NOIO is 0, so
            // this also covers ReBuzz's idle calls (and a muted input in
            // Jeskola Buzz, which keeps the previous block in the buffer).
            if ((mode & WorkModes.WM_READ) == 0 || input == null || n == 0)
            {
                GoIdle(n);
                return false;
            }

            int sr = SampleRate();
            UpdateCoefficients(sr);

            float threshDb = ThresholdDb;
            float knee     = (float)Knee;
            float ratio    = (float)Ratio;
            float wetF     = WetDry / 100f;
            float dryF     = 1f - wetF;

            EnsureLookahead(sr);
            EnsureRms(sr);
            _idle = false;
            int  bufLen  = _laBuffer.Length;
            bool useRms  = Detection == 1;

            // READ without WRITE: keep the detector running, produce no output.
            bool write = (mode & WorkModes.WM_WRITE) != 0;

            float maxIn  = 0f;
            float maxOut = 0f;
            float maxGR  = 0f;
            int   colLen = ColumnLength(sr);
            if (_colLeft <= 0 || _colLeft > colLen) _colLeft = colLen;

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

                if (write)
                    output[i] = new Sample(outL / SCALE, outR / SCALE);

                float outPeak = MathF.Max(MathF.Abs(outL), MathF.Abs(outR));
                if (outPeak > maxOut) maxOut = outPeak;

                // ── History column ────────────────────────────────────────────
                if (peak    > _colIn)  _colIn  = peak;
                if (outPeak > _colOut) _colOut = outPeak;
                if (dbGR    > _colGr)  _colGr  = dbGR;
                if (--_colLeft == 0) { PushColumn(); _colLeft = colLen; }
            }

            _meterIn  = maxIn;
            _meterOut = maxOut;
            _grDb     = maxGR;
            _detDb    = FastLinToDb(env);

            return write;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  GUI — embedded at the top of the parameter window
    //
    //  Left: SAT light, IN / GR / OUT meters, transfer curve with a dot at the
    //  detector level. Right: scrolling level and gain-reduction history and
    //  a readout of the effective settings. Fixed size: ReBuzz sizes the
    //  parameter window to the GUI, so the panel must not follow the width
    //  it is offered (that opens a screen-wide window).
    // ─────────────────────────────────────────────────────────────────────────

    [MachineGUIFactoryDecl(PreferWindowedGUI = false, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new PedalCompGui();
    }

    public class PedalCompGui : UserControl, IMachineGUI
    {
        IMachine?         machine;
        PedalCompMachine? comp;
        DispatcherTimer   timer;

        Rectangle   meterIn      = null!;
        Rectangle   meterGR      = null!;
        Rectangle   meterOut     = null!;
        Rectangle   peakHoldLine = null!;   // thin line on GR meter
        TextBlock   lblGR        = null!;
        Border      clipLight    = null!;
        Ellipse     curveDot     = null!;
        HistoryView history      = null!;
        TextBlock   readout      = null!;

        static readonly SolidColorBrush ClipOn  = Frozen(Color.FromRgb(240, 50, 40));
        static readonly SolidColorBrush ClipOff = Frozen(Color.FromRgb(50, 15, 15));
        bool _clipShown;

        // ── Panel size ────────────────────────────────────────────────────────
        const double PanelW   = 560;
        const double PanelH   = 260;
        const double LeftW    = 180;

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
        const float CURVE_DB_MIN = -60f;
        const float CURVE_W      = 158f;
        const float CURVE_H      =  78f;

        const double TrackH = 80;
        const double TrackW = 22;

        string _lastReadout = "";

        // ── IMachineGUI ───────────────────────────────────────────────────────

        public IMachine? Machine
        {
            get => machine;
            set
            {
                machine = value;
                comp = machine?.ManagedMachine as PedalCompMachine;
                history.Machine = comp;
                UpdateTimer();
            }
        }

        // ── Construction ─────────────────────────────────────────────────────

        public PedalCompGui()
        {
            timer = CreateTimer();
            Content = BuildUI();
            Width  = PanelW;
            Height = PanelH;
            // Run the display only while the GUI is on screen. ReBuzz can
            // hide a window rather than unload it, so follow visibility too.
            Loaded            += (_, _) => UpdateTimer();
            Unloaded          += (_, _) => timer.Stop();
            IsVisibleChanged  += (_, _) => UpdateTimer();
        }

        void UpdateTimer()
        {
            if (comp != null && IsLoaded && IsVisible) timer.Start();
            else timer.Stop();
        }

        static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        UIElement BuildUI()
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 24));

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LeftW) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = BuildLeft();
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var right = BuildRight();
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            return grid;
        }

        UIElement BuildLeft()
        {
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
                Background        = ClipOff,
                BorderBrush       = new SolidColorBrush(Color.FromRgb(80, 30, 30)),
                BorderThickness   = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor            = Cursors.Hand,
                ToolTip           = "SAT - signal in soft-clip zone; click to reset"
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

            meterIn  = AddMeterColumn(meterPanel, 0, "IN",  Color.FromRgb( 70, 200,  85), out _);
            meterOut = AddMeterColumn(meterPanel, 2, "OUT", Color.FromRgb( 55, 145, 220), out _);

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
            // The dot sits at the detector level on the curve.
            var curveBorder = new Border
            {
                Width           = 160,
                Height          = 80,
                Background      = new SolidColorBrush(Color.FromRgb(10, 10, 14)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(45, 45, 58)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var curveCanvas = new Canvas { Width = CURVE_W, Height = CURVE_H, ClipToBounds = true };

            curveCanvas.Children.Add(new Line
            {
                X1              = 0, Y1 = CURVE_H,
                X2              = CURVE_W, Y2 = 0,
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

            curveDot = new Ellipse
            {
                Width      = 6,
                Height     = 6,
                Fill       = new SolidColorBrush(Color.FromRgb(255, 200, 60)),
                Visibility = Visibility.Collapsed
            };
            curveCanvas.Children.Add(curveDot);

            curveBorder.Child = curveCanvas;
            root.Children.Add(curveBorder);

            return root;
        }

        UIElement BuildRight()
        {
            var root = new Grid { Margin = new Thickness(0, 8, 10, 8) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            history = new HistoryView();
            Grid.SetRow(history, 0);
            root.Children.Add(history);

            readout = new TextBlock
            {
                Foreground            = new SolidColorBrush(Color.FromRgb(150, 150, 165)),
                FontSize              = 9,
                FontFamily            = new FontFamily("Consolas"),
                LineHeight            = 11,
                LineStackingStrategy  = LineStackingStrategy.BlockLineHeight,
                Margin                = new Thickness(2, 6, 0, 0),
                TextTrimming          = TextTrimming.CharacterEllipsis
            };
            TextOptions.SetTextFormattingMode(readout, TextFormattingMode.Display);
            Grid.SetRow(readout, 1);
            root.Children.Add(readout);

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

        static Color DimColor(Color c, float f) =>
            Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

        // ── Timer ─────────────────────────────────────────────────────────────

        DispatcherTimer CreateTimer()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            t.Tick += (_, _) => Refresh();
            return t;
        }

        // ── Refresh (30 fps) ──────────────────────────────────────────────────

        void Refresh()
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

            lblGR.Text = "GR  " + vuGR.ToString("F1", CultureInfo.InvariantCulture) + " dB";

            // ── SAT indicator ─────────────────────────────────────────────────
            int clipVal = comp.ClipCountdown;
            if (clipVal > 0) comp.DecrementClip();
            bool lit = clipVal > 0;
            if (lit != _clipShown)
            {
                _clipShown = lit;
                clipLight.Background = lit ? ClipOn : ClipOff;
            }

            // ── Transfer curve (only redraws when parameters change) ──────────
            int thr = comp.Threshold, ratio = comp.Ratio, knee = comp.Knee;
            if (thr != _lastThresh || ratio != _lastRatio || knee != _lastKnee)
            {
                _lastThresh = thr;
                _lastRatio  = ratio;
                _lastKnee   = knee;
                UpdateTransferCurve(thr, ratio, knee);
            }

            // ── Dot at the detector level ─────────────────────────────────────
            float det = comp.DetectorDb;
            if (det > CURVE_DB_MIN + 0.5f)
            {
                float inDb  = Math.Clamp(det, CURVE_DB_MIN, 0f);
                float gr    = PedalCompMachine.SoftKneeGR(inDb, -(float)thr, (float)knee, (float)ratio);
                float outDb = Math.Clamp(inDb - gr, CURVE_DB_MIN, 0f);
                Canvas.SetLeft(curveDot, CurveX(inDb) - 3);
                Canvas.SetTop(curveDot,  CurveY(outDb) - 3);
                curveDot.Visibility = Visibility.Visible;
            }
            else
            {
                curveDot.Visibility = Visibility.Collapsed;
            }

            // ── History and readout ───────────────────────────────────────────
            history.Refresh();
            UpdateReadout();
        }

        static float CurveX(float inDb)  => (inDb - CURVE_DB_MIN) / -CURVE_DB_MIN * CURVE_W;
        static float CurveY(float outDb) => (1f - (outDb - CURVE_DB_MIN) / -CURVE_DB_MIN) * CURVE_H;

        void UpdateTransferCurve(int threshold, int ratio, int knee)
        {
            const int STEPS = 120;

            float threshDb = -(float)threshold;
            float ratioF   =  (float)ratio;
            float kneeF    =  (float)knee;

            var pts = new PointCollection(STEPS + 1);
            for (int s = 0; s <= STEPS; s++)
            {
                float inDb  = CURVE_DB_MIN - CURVE_DB_MIN * s / STEPS;
                float gr    = PedalCompMachine.SoftKneeGR(inDb, threshDb, kneeF, ratioF);
                float outDb = Math.Clamp(inDb - gr, CURVE_DB_MIN, 0f);
                pts.Add(new Point(CurveX(inDb), CurveY(outDb)));
            }
            transferLine.Points = pts;
        }

        // Settings as they act on the audio (the sliders cannot show Auto
        // Makeup's result or the lookahead in samples), plus peak levels over
        // the last 3 seconds of history.
        void UpdateReadout()
        {
            var c   = comp!;
            var inv = CultureInfo.InvariantCulture;

            string knee = c.Knee == 0 ? "hard" : c.Knee + " dB";
            float  mk   = PedalCompMachine.EffectiveMakeupDb(c.Threshold, c.Ratio, c.MakeupGain, c.AutoMakeup);
            string la   = c.Lookahead == 0 ? "lookahead off"
                        : "lookahead " + c.Lookahead + " ms = " + c.LookaheadSamples + " smp";

            float pkIn, pkOut;
            history.PeakOver(3 * PedalCompMachine.HistColsPerSec, out pkIn, out pkOut);

            string text =
                "thr " + (c.Threshold == 0 ? "0" : "-" + c.Threshold) + " dB  " + c.Ratio + ":1  knee " + knee + "\n" +
                "att " + c.Attack + " ms  rel " + c.Release + " ms  " + (c.Detection == 1 ? "RMS" : "peak") + "\n" +
                "makeup " + (mk >= 0f ? "+" : "") + mk.ToString("F1", inv) + " dB" + (c.AutoMakeup == 1 ? " auto" : "") +
                "  mix " + c.WetDry + "%\n" +
                la + "\n" +
                "pk 3s  in " + DbText(pkIn) + "  out " + DbText(pkOut);

            if (text != _lastReadout)
            {
                _lastReadout = text;
                readout.Text = text;
            }
        }

        static string DbText(float lin)
        {
            if (lin < 1e-6f) return "-inf";
            return (20.0 * Math.Log10(lin)).ToString("F1", CultureInfo.InvariantCulture);
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

    // ─────────────────────────────────────────────────────────────────────────
    //  History view: the last 4 seconds, newest on the right.
    //  Input peak (green fill) and output peak (blue line) on a -60..0 dBFS
    //  scale from the bottom; gain reduction (red) hanging from the top on a
    //  0..20 dB scale; threshold as an amber dashed line; a faint mark every
    //  second, scrolling with the audio.
    // ─────────────────────────────────────────────────────────────────────────
    public class HistoryView : FrameworkElement
    {
        internal PedalCompMachine? Machine;

        const int   Cols    = 4 * PedalCompMachine.HistColsPerSec;   // 4 s
        const float LvlMin  = -60f;
        const float GrMax   =  20f;

        static readonly Brush BgBrush     = FrozenBrush(Color.FromRgb(10, 10, 14));
        static readonly Pen   BorderPen   = FrozenPen(Color.FromRgb(45, 45, 58), 1);
        static readonly Pen   GridPen     = FrozenPen(Color.FromRgb(30, 30, 40), 1);
        static readonly Brush InFill      = FrozenBrush(Color.FromArgb(70, 70, 170, 85));
        static readonly Pen   InPen       = FrozenPen(Color.FromArgb(150, 70, 170, 85), 1);
        static readonly Pen   OutPen      = FrozenPen(Color.FromRgb(55, 145, 220), 1.2);
        static readonly Brush GrFill      = FrozenBrush(Color.FromArgb(110, 200, 70, 45));
        static readonly Pen   GrPen       = FrozenPen(Color.FromRgb(230, 95, 60), 1.2);
        static readonly Pen   ThreshPen   = FrozenDashedPen(Color.FromRgb(200, 160, 50));
        static readonly Brush LabelBrush  = FrozenBrush(Color.FromRgb(140, 140, 155));
        static readonly Brush LabelBack   = FrozenBrush(Color.FromArgb(200, 10, 10, 14));
        static readonly Typeface Face     = new Typeface("Consolas");

        readonly float[] _in  = new float[Cols];
        readonly float[] _out = new float[Cols];
        readonly float[] _gr  = new float[Cols];
        int _count = -1;        // history count at the last snapshot
        int _thresh = -1;

        static Brush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }
        static Pen FrozenDashedPen(Color c)
        {
            var p = new Pen(new SolidColorBrush(c), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            p.Freeze();
            return p;
        }

        public HistoryView()
        {
            ClipToBounds = true;
            SnapsToDevicePixels = true;
        }

        // Copy the newest columns from the machine; redraw only on change.
        internal void Refresh()
        {
            var m = Machine;
            if (m == null) return;
            int count = m.HistoryCount;
            int thr   = m.Threshold;
            if (count == _count && thr == _thresh) return;
            _count  = count;
            _thresh = thr;

            const int mask = PedalCompMachine.HistSize - 1;
            for (int j = 0; j < Cols; j++)
            {
                int k = count - Cols + j;          // oldest first
                if (k < 0) { _in[j] = _out[j] = _gr[j] = 0f; continue; }
                int idx = k & mask;
                _in[j]  = m.HistIn[idx];
                _out[j] = m.HistOut[idx];
                _gr[j]  = m.HistGr[idx];
            }
            InvalidateVisual();
        }

        // Max input and output peak (linear) over the newest n columns.
        internal void PeakOver(int n, out float pkIn, out float pkOut)
        {
            pkIn = pkOut = 0f;
            if (_count < 0) return;
            for (int j = Math.Max(0, Cols - n); j < Cols; j++)
            {
                if (_in[j]  > pkIn)  pkIn  = _in[j];
                if (_out[j] > pkOut) pkOut = _out[j];
            }
        }

        static double LevelY(float lin, double h)
        {
            if (lin < 1e-9f) return h;
            double db = 20.0 * Math.Log10(lin);
            if (db < LvlMin) return h;
            if (db > 0) db = 0;
            return h * (db / LvlMin);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return;

            dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // Level grid at -20 and -40 dBFS, with labels on the right edge.
            for (int db = -20; db > LvlMin; db -= 20)
            {
                double y = Math.Round(h * (db / LvlMin)) + 0.5;
                dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
            }

            // One mark per second, fixed to the audio so it scrolls.
            double colW = w / Cols;
            if (_count >= 0)
            {
                int perSec = PedalCompMachine.HistColsPerSec;
                for (int j = 0; j < Cols; j++)
                {
                    int k = _count - Cols + j;
                    if (k > 0 && k % perSec == 0)
                    {
                        double x = Math.Round(j * colW) + 0.5;
                        dc.DrawLine(GridPen, new Point(x, 0), new Point(x, h));
                    }
                }
            }

            // Input: filled from the bottom.
            var inGeo = new StreamGeometry();
            using (var g = inGeo.Open())
            {
                g.BeginFigure(new Point(0, h), true, true);
                for (int j = 0; j < Cols; j++)
                {
                    double y = LevelY(_in[j], h);
                    g.LineTo(new Point(j * colW, y), j > 0, false);   // stroked: outline on top
                    g.LineTo(new Point((j + 1) * colW, y), true, false);
                }
                g.LineTo(new Point(w, h), false, false);
            }
            inGeo.Freeze();
            dc.DrawGeometry(InFill, InPen, inGeo);

            // Gain reduction: filled down from the top.
            var grGeo = new StreamGeometry();
            using (var g = grGeo.Open())
            {
                g.BeginFigure(new Point(0, 0), true, true);
                for (int j = 0; j < Cols; j++)
                {
                    double y = h * Math.Min(_gr[j], GrMax) / GrMax;
                    g.LineTo(new Point(j * colW, y), false, false);
                    g.LineTo(new Point((j + 1) * colW, y), false, false);
                }
                g.LineTo(new Point(w, 0), false, false);
            }
            grGeo.Freeze();
            dc.DrawGeometry(GrFill, null, grGeo);

            // Output: line.
            var outGeo = new StreamGeometry();
            using (var g = outGeo.Open())
            {
                g.BeginFigure(new Point(0.5 * colW, LevelY(_out[0], h)), false, false);
                for (int j = 1; j < Cols; j++)
                    g.LineTo(new Point((j + 0.5) * colW, LevelY(_out[j], h)), true, true);
            }
            outGeo.Freeze();
            dc.DrawGeometry(null, OutPen, outGeo);

            // Threshold.
            if (_thresh > 0 && -_thresh > LvlMin)
            {
                double ty = Math.Round(h * (-_thresh / LvlMin)) + 0.5;
                dc.DrawLine(ThreshPen, new Point(0, ty), new Point(w, ty));
            }

            // Labels last, on a dark backing so the traces never hide them.
            for (int db = -20; db > LvlMin; db -= 20)
            {
                double y = Math.Round(h * (db / LvlMin));
                DrawLabel(dc, db.ToString(CultureInfo.InvariantCulture), w - 3, y - 10, true, dpi);
            }
            DrawLabel(dc, "GR + LEVEL  4 s", 3, 2, false, dpi);

            dc.DrawRectangle(null, BorderPen, new Rect(0.5, 0.5, w - 1, h - 1));
        }

        static void DrawLabel(DrawingContext dc, string text, double x, double y, bool alignRight, double dpi)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                       Face, 8, LabelBrush, dpi);
            double lx = alignRight ? x - ft.Width : x;
            dc.DrawRectangle(LabelBack, null, new Rect(lx - 2, y, ft.Width + 4, ft.Height));
            dc.DrawText(ft, new Point(lx, y));
        }
    }
}
