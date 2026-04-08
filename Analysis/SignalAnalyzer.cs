using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NWaves.Operations;
using NWaves.Signals;
using NWaves.Transforms;
using NWaves.Windows;

namespace MeaSound
{
    internal sealed class DeconvolutionResult
    {
        /// <summary>Logarithmic frequency axis in Hz.</summary>
        public double[] FrequencyAxis { get; init; } = Array.Empty<double>();
        /// <summary>Magnitude in dB before smoothing.</summary>
        public double[] RawMagnitudeDb { get; init; } = Array.Empty<double>();
        /// <summary>Smoothed magnitude in dB, or null when smoothing is disabled.</summary>
        public double[]? SmoothedMagnitudeDb { get; private set; }
        /// <summary>Phase in degrees.</summary>
        public double[] Phasedeg { get; init; } = Array.Empty<double>();
        /// <summary>Group delay in milliseconds.</summary>
        public double[] GroupDelayMs { get; init; } = Array.Empty<double>();

        /// <summary>
        /// Computes smoothing in log-frequency bands using Hann-weighted averaging
        /// in power space.  <paramref name="octaveFraction"/> = 0 → no smoothing.
        /// </summary>
        public void ComputeSmoothed(double octaveFraction)
        {
            if (octaveFraction <= 0 || FrequencyAxis.Length == 0)
            {
                SmoothedMagnitudeDb = null;
                return;
            }

            int n = FrequencyAxis.Length;
            double[] smoothed = new double[n];
            double halfOct = octaveFraction / 2.0;

            for (int i = 0; i < n; i++)
            {
                double fc = FrequencyAxis[i];
                double fLo = fc * Math.Pow(2.0, -halfOct);
                double fHi = fc * Math.Pow(2.0, +halfOct);

                double sumW = 0, sumP = 0;
                for (int j = 0; j < n; j++)
                {
                    double f = FrequencyAxis[j];
                    if (f < fLo || f > fHi) continue;

                    // Hann weight in log-frequency space
                    double t = (Math.Log(f) - Math.Log(fLo)) / (Math.Log(fHi) - Math.Log(fLo));
                    double w = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * t));

                    double power = Math.Pow(10.0, RawMagnitudeDb[j] / 10.0);
                    sumW += w;
                    sumP += w * power;
                }

                smoothed[i] = sumW > 0
                    ? 10.0 * Math.Log10(sumP / sumW + 1e-30)
                    : RawMagnitudeDb[i];
            }

            SmoothedMagnitudeDb = smoothed;
        }

        /// <summary>Returns smoothed data if available, otherwise raw data.</summary>
        public double[] MagnitudeDb => SmoothedMagnitudeDb ?? RawMagnitudeDb;
    }

    // SignalAnalyzer  –  main analysis class

    internal class SignalAnalyzer
    {
        public SweepType CurrentSweepType { get; set; } = SweepType.ExponentialSweep;
        public int OriginalSignalSampleRate { get; set; } = 48000;
        public int SweepStartFreq { get; set; } = 100;
        public int SweepEndFreq { get; set; } = 8000;
        public int SweepDurationSeconds { get; set; } = 10;
        public bool UseFeedbackChannel { get; set; } = false;

        /// <summary>Window length around the IR peak before the peak [ms].</summary>
        public double LeftWindowMs { get; set; } = 125.0;
        /// <summary>Window length around the IR peak after the peak [ms].</summary>
        public double IrWindowMs { get; set; } = 150.0;
        /// <summary>Regularization parameter for Wiener deconvolution.</summary>
        public double WienerLambda { get; set; } = 1e-5;
        /// <summary>Lower frequency of the resulting spectrum [Hz].</summary>
        public double PlotF1 { get; set; } = 20.0;
        /// <summary>Upper frequency of the resulting spectrum [Hz].</summary>
        public double PlotF2 { get; set; } = 20000.0;
        /// <summary>Number of log-uniform output points.</summary>
        public int LogBins { get; set; } = 2000;

        private float[] _originalSignal = null;
        private float[] _feedbackSignal = null;
        private List<int> _selectedFrequencies;

        public bool IsFarinaPath =>
            CurrentSweepType == SweepType.ExponentialSweep && !UseFeedbackChannel;

        public void SetOriginalSignalFromFile(string wavPath)
        {
            if (!File.Exists(wavPath))
                throw new FileNotFoundException("The original signal file does not exist.", wavPath);
            _originalSignal = AudioRecorder.ReadWavAsFloatMono(wavPath, out int sr);
            OriginalSignalSampleRate = sr > 0 ? sr : OriginalSignalSampleRate;
            Debug.WriteLine($"[SignalAnalyzer] Original signal loaded: {_originalSignal.Length} samples @ {sr} Hz");
        }

        public float[] GetOriginalSignal()
        {
            if (_originalSignal == null)
                throw new InvalidOperationException("The original signal is not set.");
            return _originalSignal;
        }

        public void SetSelectedFrequencies(List<int> freqs) => _selectedFrequencies = freqs;

        public void SetFeedbackSignal(float[] feedback) => _feedbackSignal = feedback;

        public void LoadFeedbackFromRecording(string wavPath, int feedbackChannel = 0)
        {
            if (!File.Exists(wavPath)) { Debug.WriteLine($"[SignalAnalyzer] WARNING: Cannot load feedback: {wavPath}"); return; }
            try
            {
                _feedbackSignal = AudioRecorder.ReadWavAsFloatMono(wavPath, out _);
                Debug.WriteLine($"[SignalAnalyzer] Feedback loaded: {_feedbackSignal.Length} samples");
            }
            catch (Exception ex) { Debug.WriteLine($"[SignalAnalyzer] ERROR loading feedback: {ex.Message}"); _feedbackSignal = null; }
        }

        // Remove silence from beginning and end of the signal
        private static (float[] mic, float[]? reference) ApplyTrimAuto(
            float[] mic, float[]? reference, int sampleRate)
        {
            int onset = FindOnsetAuto(mic, sampleRate);
            float[] trimmedMic = onset > 0 ? mic[onset..] : mic;
            float[]? trimmedRef = null;
            if (reference != null)
            {
                int refOnset = 0;
                if (reference.Length > onset * 2)
                {
                    refOnset = Math.Min(onset, reference.Length);
                }
                trimmedRef = refOnset > 0 ? reference[refOnset..] : reference;
            }
            Debug.WriteLine($"[ApplyTrimAuto] onset={onset} ({onset * 1000.0 / sampleRate:F1} ms)");
            return (trimmedMic, trimmedRef);
        }

        private static int FindOnsetAuto(float[] signal, int sampleRate)
        {
            int block = Math.Max(1, (int)(0.010 * sampleRate)); // 10 ms
            // Global RMS
            double globalRms = 0;
            for (int i = 0; i < signal.Length; i++) globalRms += (double)signal[i] * signal[i];
            globalRms = Math.Sqrt(globalRms / signal.Length);
            double threshold = 0.10 * globalRms;

            for (int i = 0; i + block <= signal.Length; i += block)
            {
                double rms = 0;
                for (int j = 0; j < block; j++) rms += (double)signal[i + j] * signal[i + j];
                rms = Math.Sqrt(rms / block);
                if (rms >= threshold)
                    return Math.Max(0, i - block);
            }
            return 0;
        }

        // FFT and window utility functions
        private static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        /// <summary>
        /// Creates and normalizes a Hann window.
        /// </summary>
        private static double[] BuildWindowCoeffs(int n)
        {
            double[] w = new double[n];
            double norm = 0;
            for (int i = 0; i < n; i++)
            {
                w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1)));
                norm += w[i] * w[i];
            }
            // Normalization: norm = 1 / sqrt(Σw²) → amplitude independent of length or shape
            double scale = norm > 0 ? 1.0 / Math.Sqrt(norm) : 1.0;
            for (int i = 0; i < n; i++) w[i] *= scale;
            return w;
        }

        private static void ForwardFft(float[] data, out double[] real, out double[] imag)
        {
            int n = data.Length;
            real = new double[n];
            imag = new double[n];
            // Use NWaves RealFft
            var poolF = ArrayPool<float>.Shared;
            float[] r = poolF.Rent(n), im = poolF.Rent(n);
            try
            {
                var fft = new RealFft(n);
                fft.Direct(data, r, im);
                for (int i = 0; i < n; i++) { real[i] = r[i]; imag[i] = im[i]; }
            }
            finally { poolF.Return(r, clearArray: true); poolF.Return(im, clearArray: true); }
        }

        private static float[] InverseFft(double[] real, double[] imag)
        {
            int n = real.Length;
            var poolF = ArrayPool<float>.Shared;
            float[] r = poolF.Rent(n), im = poolF.Rent(n), outF = poolF.Rent(n);
            try
            {
                for (int i = 0; i < n; i++) { r[i] = (float)real[i]; im[i] = (float)imag[i]; }
                var fft = new RealFft(n);
                fft.Inverse(r, im, outF);
                float[] result = new float[n];
                Array.Copy(outF, result, n);
                return result;
            }
            finally { poolF.Return(r, clearArray: true); poolF.Return(im, clearArray: true); poolF.Return(outF, clearArray: true); }
        }

        // Direct FFT
        /// <summary>
        /// Performs direct FFT without deconvolution.
        /// </summary>
        public DeconvolutionResult ComputeDirectFft(float[] mic, int sampleRate, float[]? refSignal = null)
        {
            (mic, refSignal) = ApplyTrimAuto(mic, refSignal, sampleRate);

            int L = mic.Length;
            int fftSize = Math.Max(65536, NextPow2(L));

            double[] window = BuildWindowCoeffs(L);
            double windowSum = 0;
            for (int i = 0; i < L; i++) windowSum += window[i];

            float[] padded = new float[fftSize];
            for (int i = 0; i < L; i++) padded[i] = (float)(mic[i] * window[i]);

            ForwardFft(padded, out double[] real, out double[] imag);

            int bins = fftSize / 2 + 1;
            double[] magDb = new double[bins];
            double[] freqAxis = new double[bins];
            double freqRes = (double)sampleRate / fftSize;

            for (int k = 0; k < bins; k++)
            {
                freqAxis[k] = k * freqRes;

                // Amplitude calculation with normalization (2.0 / windowSum ensures 0dBFS sine = 0dB in the graph)
                double mag = Math.Sqrt(real[k] * real[k] + imag[k] * imag[k]) * 2.0 / windowSum;
                magDb[k] = 20.0 * Math.Log10(mag + 1e-15);
            }

            return ResampleToMaxLogBins(freqAxis, magDb, 20, 20000, LogBins);
        }

        // Wiener deconvolution
        public float[] WienerDeconvolve(float[] mic, float[] reference, int sampleRate)
        {
            int fftSize = NextPow2(mic.Length + reference.Length);
            float[] yPad = new float[fftSize], xPad = new float[fftSize];
            Array.Copy(mic, yPad, mic.Length);
            Array.Copy(reference, xPad, reference.Length);

            ForwardFft(yPad, out double[] yReal, out double[] yImag);
            ForwardFft(xPad, out double[] xReal, out double[] xImag);

            // Find maximum energy for relative lambda calculation
            double maxXsq = 0;
            double freqRes = (double)sampleRate / fftSize;
            int startBin = Math.Max(1, (int)(SweepStartFreq / freqRes));
            int endBin = Math.Min(fftSize / 2, (int)(SweepEndFreq / freqRes));

            for (int i = startBin; i <= endBin; i++)
            {
                double magSq = xReal[i] * xReal[i] + xImag[i] * xImag[i];
                if (magSq > maxXsq) maxXsq = magSq;
            }

            // Your lambda from UI (if 0, use a safe default value)
            double baseLambda = (WienerLambda > 0 ? WienerLambda : 1e-4) * maxXsq;

            double[] hReal = new double[fftSize], hImag = new double[fftSize];

            for (int i = 0; i <= fftSize / 2; i++)
            {
                double hz = i * freqRes;

                double taper = 1.0;
                double fadeStart = SweepStartFreq * 0.15;
                double fadeEnd = SweepEndFreq * 0.15;

                if (hz < SweepStartFreq)
                {
                    double edge = SweepStartFreq - fadeStart;
                    taper = hz < edge ? 0.0 : 0.5 * (1.0 - Math.Cos(Math.PI * (hz - edge) / fadeStart));
                }
                else if (hz > SweepEndFreq)
                {
                    double edge = SweepEndFreq + fadeEnd;
                    taper = hz > edge ? 0.0 : 0.5 * (1.0 + Math.Cos(Math.PI * (hz - SweepEndFreq) / fadeEnd));
                }

                // DYNAMIC LAMBDA DERIVED FROM UI:
                // In the middle of the band (taper = 1) it is exactly your value from UI.
                // At the edges (taper decreases to 0), your value from UI smoothly increases (e.g. 100x).
                // This way you control the shape and smoothing, but the algorithm never fails.
                double dynamicLambda = baseLambda * (1.0 + (1.0 - taper) * 100.0);

                double den = xReal[i] * xReal[i] + xImag[i] * xImag[i] + dynamicLambda;

                double resR = (xReal[i] * yReal[i] + xImag[i] * yImag[i]) / den;
                double resI = (xReal[i] * yImag[i] - xImag[i] * yReal[i]) / den;

                hReal[i] = resR * taper;
                hImag[i] = resI * taper;

                if (i > 0 && i < fftSize / 2)
                {
                    hReal[fftSize - i] = hReal[i];
                    hImag[fftSize - i] = -hImag[i];
                }
            }

            float[] irRaw = InverseFft(hReal, hImag);

            int shift = reference.Length;
            float[] irShifted = new float[fftSize];
            for (int i = 0; i < fftSize; i++)
            {
                irShifted[(i + shift) % fftSize] = irRaw[i];
            }

            Array.Resize(ref irShifted, mic.Length + reference.Length - 1);
            return irShifted;
        }

        /// <param name="sampleRate">Sampling rate of the mic and reference signals.</param>
        public float[] FarinaDeconvolve(float[] mic, int sampleRate, double startFreq, double endFreq, double durationSeconds)
        {
            int n = (int)(sampleRate * durationSeconds);
            float[] inv = new float[n];

            double logRatio = Math.Log(endFreq / startFreq);

            // 1. GENERATE A PURE INVERSE FILTER (without any Fade-in/out)
            for (int i = 0; i < n; i++)
            {
                // Time flowing backwards (because we are building an already reversed filter)
                double tReverse = (double)(n - 1 - i) / sampleRate;

                // Exact logarithmic phase at given time
                double phase = 2.0 * Math.PI * startFreq * durationSeconds / logRatio
                             * (Math.Exp(logRatio * tReverse / durationSeconds) - 1.0);

                // Time for amplitude compensation (from 0 to duration)
                double tForward = (double)i / sampleRate;

                // Result: Pure sine * compensation -3dB/octave
                inv[i] = (float)(Math.Sin(phase) * Math.Exp(-tForward * logRatio / durationSeconds));
            }

            int fftSize = NextPow2(mic.Length + n);
            float[] yPad = new float[fftSize], iP = new float[fftSize];
            Array.Copy(mic, yPad, mic.Length);
            Array.Copy(inv, iP, n);

            ForwardFft(yPad, out double[] yR, out double[] yI);
            ForwardFft(iP, out double[] iR, out double[] iI);

            double[] hR = new double[fftSize], hI = new double[fftSize];
            double freqRes = (double)sampleRate / fftSize;

            // Apply a soft taper to the final result to prevent Gibbs phenomenon (ringing)
            double fadeStart = startFreq * 0.15;
            double fadeEnd = endFreq * 0.15;

            for (int i = 0; i <= fftSize / 2; i++)
            {
                double hz = i * freqRes;

                double resR = yR[i] * iR[i] - yI[i] * iI[i];
                double resI = yR[i] * iI[i] + yI[i] * iR[i];

                double taper = 1.0;
                if (hz < startFreq)
                {
                    double edge = Math.Max(0.1, startFreq - fadeStart);
                    if (hz <= edge) taper = 0.0;
                    else taper = 0.5 * (1.0 - Math.Cos(Math.PI * (hz - edge) / fadeStart));
                }
                else if (hz > endFreq)
                {
                    double edge = Math.Min(sampleRate / 2.0, endFreq + fadeEnd);
                    if (hz >= edge) taper = 0.0;
                    else taper = 0.5 * (1.0 + Math.Cos(Math.PI * (hz - endFreq) / fadeEnd));
                }

                hR[i] = resR * taper;
                hI[i] = resI * taper;

                if (i > 0 && i < fftSize / 2)
                {
                    hR[fftSize - i] = hR[i];
                    hI[fftSize - i] = -hI[i];
                }
            }
            return InverseFft(hR, hI);
        }

        // ComputeResult  –  Convert IR to frequency response
        public DeconvolutionResult ComputeResult(float[] ir, int sampleRate)
        {
            int pIdx = FindPeakIndex(ir);

            int preS = (int)(20.0 * sampleRate / 1000.0);
            int postS = (int)(IrWindowMs * sampleRate / 1000.0);
            int start = Math.Max(0, pIdx - preS);
            int end = Math.Min(ir.Length - 1, pIdx + postS);

            double[] preWin = RisingHalfCoeff(pIdx - start);
            double[] postWin = FallingHalfCoeff(end - pIdx + 1, 0.2);

            int fftSize = Math.Max(65536, NextPow2((end - start) * 2));
            float[] padded = new float[fftSize];

            for (int i = 0; i < (end - start + 1); i++)
            {
                int srcIdx = start + i;
                double w = (srcIdx < pIdx) ? preWin[srcIdx - start] : postWin[srcIdx - pIdx];

                // FFT shift to zero
                int destIdx = (srcIdx < pIdx) ? fftSize - (pIdx - srcIdx) : (srcIdx - pIdx);
                padded[destIdx] = (float)(ir[srcIdx] * w);
            }

            ForwardFft(padded, out double[] real, out double[] imag);

            int bins = fftSize / 2 + 1;
            double freqRes = (double)sampleRate / fftSize;
            double[] fAx = new double[bins], mDb = new double[bins];

            // Find the maximum value in the measured band for normalization
            double bandMax = -150;
            for (int k = 0; k < bins; k++)
            {
                fAx[k] = k * freqRes;
                double mag = Math.Sqrt(real[k] * real[k] + imag[k] * imag[k]);
                mDb[k] = 20.0 * Math.Log10(mag + 1e-18);
                if (fAx[k] >= SweepStartFreq && fAx[k] <= SweepEndFreq) bandMax = Math.Max(bandMax, mDb[k]);
            }

            // Normalization: peak of the measured band will be at 0 dB
            for (int k = 0; k < bins; k++) mDb[k] -= bandMax;

            return ResampleToLogBins(fAx, mDb, new double[bins], new double[bins], PlotF1, PlotF2, LogBins);
        }

        /// <summary>Returns coefficients for the rising half of a Hann window.</summary>
        private double[] RisingHalfCoeff(int len)
        {
            double[] win = new double[len];
            for (int i = 0; i < len; i++)
            {
                // Smooth ramp from 0 (full noise reduction) to 1 just before the peak
                win[i] = 0.5 * (1.0 - Math.Cos(Math.PI * i / (double)len));
            }
            return win;
        }

        /// <summary>Returns post-peak window coefficients with an optional flat section.</summary>
        private double[] FallingHalfCoeff(int len, double flatFraction = 0.05)
        {
            double[] win = new double[len];

            int flatLen = (int)(len * flatFraction);
            int taperLen = len - flatLen;

            for (int i = 0; i < len; i++)
            {
                if (i < flatLen)
                {
                    // Pass the main energy (direct sound from the speaker) unchanged
                    win[i] = 1.0;
                }
                else
                {
                    int taperIndex = i - flatLen;
                    win[i] = 0.5 * (1.0 + Math.Cos(Math.PI * taperIndex / (double)taperLen));
                }
            }
            return win;
        }

        /// <summary>
        /// Analyzes frequency response and returns points for selected frequencies.
        /// </summary>
        public List<(double frequency, double amplitudeDb)> AnalyzeFrequencyResponse(
            float[] recordedSignal,
            int sampleRate,
            TestSignalType signalType,
            AnalysisMethod analysisMethod = AnalysisMethod.Farina,
            float[]? signalSlice = null)
        {
            if (signalType == TestSignalType.SteppedSine)
            {
                var refSig = GetActiveReferenceSignal();
                return AnalyzeSteppedSineSegments(signalSlice ?? recordedSignal,
                                                   refSig ?? Array.Empty<float>(), sampleRate);
            }

            var result = RunDeconvolution(recordedSignal, sampleRate, signalType, analysisMethod, signalSlice);
            return ExtractSelectedFrequencies(result, sampleRate);
        }

        /// <summary>
        /// Computes full frequency response with optional octave smoothing.
        /// </summary>
        public DeconvolutionResult ComputeFrequencyResponse(
            float[] recordedSignal,
            int sampleRate,
            TestSignalType signalType,
            AnalysisMethod analysisMethod = AnalysisMethod.Farina,
            float[]? signalSlice = null,
            double smoothingOctaveFraction = 1.0/12.0)
        {
            if (signalType == TestSignalType.SteppedSine)
                return new DeconvolutionResult();

            var result = RunDeconvolution(recordedSignal, sampleRate, signalType, analysisMethod, signalSlice);
            result.ComputeSmoothed(smoothingOctaveFraction);
            return result;
        }

        /// <summary>Shared deconvolution path used by frequency-response methods.</summary>
        private DeconvolutionResult RunDeconvolution(
            float[] recordedSignal,
            int sampleRate,
            TestSignalType signalType,
            AnalysisMethod analysisMethod,
            float[]? signalSlice)
        {
            if (analysisMethod == AnalysisMethod.DirectFft)
            {
                return ComputeDirectFft(signalSlice ?? recordedSignal, sampleRate, UseFeedbackChannel ? _feedbackSignal : null);
                Debug.WriteLine($"[RunDeconvolution] Using Direct FFT with{(UseFeedbackChannel ? "" : "out")} feedback reference.");
            }

            float[] mic = signalSlice ?? recordedSignal;
            float[] ir;

            if (analysisMethod == AnalysisMethod.Farina || !UseFeedbackChannel)
            {
                ir = FarinaDeconvolve(mic, sampleRate, SweepStartFreq, SweepEndFreq, SweepDurationSeconds);
                Debug.WriteLine($"[RunDeconvolution] Using Farina deconvolution with original signal as reference.");
            }
            else
            {
                float[] refForTrim = _feedbackSignal ?? _originalSignal;
                ir = WienerDeconvolve(mic, refForTrim, sampleRate);
                Debug.WriteLine($"[RunDeconvolution] Using Wiener deconvolution with{(UseFeedbackChannel ? " feedback" : " original signal")} as reference.");
            }

            return ComputeResult(ir, sampleRate);
        }

        private List<(double frequency, double amplitudeDb)> ExtractSelectedFrequencies(
    DeconvolutionResult result, int sampleRate)
        {
            var freqsToUse = _selectedFrequencies == null
                ? Enumerable.Empty<int>()
                : _selectedFrequencies.Where(f => f >= 20 && f <= 20000);

            var out_ = new List<(double, double)>();
            foreach (int targetFreq in freqsToUse)
            {
                double searchRange = 1.0145;
                double fLo = targetFreq / searchRange;
                double fHi = targetFreq * searchRange;

                double peakDb = GetMaxInFreqRange(result, fLo, fHi);
                out_.Add((targetFreq, peakDb));
            }
            return out_;
        }

        private static double GetMaxInFreqRange(DeconvolutionResult result, double fLo, double fHi)
        {
            double[] freqs = result.FrequencyAxis;
            double[] mags = result.MagnitudeDb;
            double maxVal = double.NegativeInfinity;
            bool found = false;

            int startIdx = BinarySearchLo(freqs, fLo);
            int endIdx = BinarySearchLo(freqs, fHi);

            for (int i = startIdx; i <= Math.Min(endIdx + 1, freqs.Length - 1); i++)
            {
                if (freqs[i] >= fLo && freqs[i] <= fHi)
                {
                    if (mags[i] > maxVal) maxVal = mags[i];
                    found = true;
                }
            }
            return found ? maxVal : InterpolateLogResult(result, (fLo + fHi) / 2.0);
        }

        private static double InterpolateLogResult(DeconvolutionResult result, double hz)
        {
            double[] freqs = result.FrequencyAxis;
            double[] mags  = result.MagnitudeDb;
            if (freqs.Length == 0) return -120.0;

            int lo = 0, hi = freqs.Length - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (freqs[mid] < hz) lo = mid; else hi = mid;
            }
            if (lo == hi) return mags[lo];

            double t = (hz - freqs[lo]) / (freqs[hi] - freqs[lo]);
            return mags[lo] + t * (mags[hi] - mags[lo]);
        }


        // Spectrogram (STFT)
        public static (float[,] data, double[] times, double[] freqs) ComputeSpectrogram(
            float[] signal, int sampleRate, int fftSize = 8192, int hopSize = 1024,
            NWaves.Windows.WindowType window = NWaves.Windows.WindowType.Rectangular)
        {
            int origLength = signal.Length;
            int paddedLength = origLength;
            if (origLength % hopSize != 0)
                paddedLength = ((origLength / hopSize) + 1) * hopSize;
            if (paddedLength < fftSize)
                paddedLength = fftSize;

            float[] paddedSignal = signal;
            if (paddedLength != origLength)
            {
                paddedSignal = new float[paddedLength];
                Array.Copy(signal, paddedSignal, origLength);
            }

            var stft = new NWaves.Transforms.Stft(fftSize, hopSize, window);
            var spec = stft.Spectrogram(paddedSignal);
            int frames = spec.Count, bins = spec[0].Length;

            int startBin = Math.Max(0, Math.Min((int)Math.Ceiling(20.0  * fftSize / sampleRate), bins - 1));
            int endBin   = Math.Max(0, Math.Min((int)Math.Floor(20000.0 * fftSize / sampleRate), bins - 1));
            if (endBin < startBin) endBin = startBin;
            int newBins = endBin - startBin + 1;

            var data  = new float[frames, newBins];
            double maxTimeSec = origLength / (double)sampleRate;
            var times = Enumerable.Range(0, frames)
                .Select(i => Math.Min(i * hopSize / (double)sampleRate, maxTimeSec))
                .ToArray();
            var freqs = Enumerable.Range(startBin, newBins)
                .Select(i => i * sampleRate / (double)fftSize)
                .ToArray();

            const double eps = 1e-10;
            double globalMax = double.MinValue;
            for (int t = 0; t < frames; t++)
            {
                var row = spec[t];
                for (int f = startBin; f <= endBin; f++)
                {
                    double db = 20 * Math.Log10(Math.Max(Math.Abs(row[f]), eps));
                    if (db > globalMax) globalMax = db;
                    data[t, f - startBin] = (float)db;
                }
            }
            spec = null;

            float maxF = (float)globalMax;
            for (int t = 0; t < frames; t++)
                for (int f = 0; f < newBins; f++)
                {
                    float v = data[t, f] - maxF;
                    data[t, f] = v < -80 ? -80 : v;
                }

            return (data, times, freqs);
        }

        // Impulse response for Excel export
        public (float[] impulse, double[] time) GetImpulseResponse(
            float[] signal, float[] referenceImpulse, int sampleRate)
        {
            var sig    = new NWaves.Signals.DiscreteSignal(sampleRate, signal);
            var refSig = new NWaves.Signals.DiscreteSignal(sampleRate, referenceImpulse);
            var result = NWaves.Operations.Operation.CrossCorrelate(sig, refSig);
            float[] corr = result.Samples;
            double maxVal = corr.Max(Math.Abs);
            if (maxVal > 1e-12) for (int i = 0; i < corr.Length; i++) corr[i] /= (float)maxVal;
            int maxSamples = Math.Min((int)(0.5 * sampleRate), corr.Length);
            float[] trimmed = new float[maxSamples];
            double[] time   = new double[maxSamples];
            for (int i = 0; i < maxSamples; i++) { trimmed[i] = corr[i]; time[i] = i / (double)sampleRate; }
            return (trimmed, time);
        }

        // SteppedSine segment analysis
        private List<(double frequency, double amplitudeDb)> AnalyzeSteppedSineSegments(
            float[] recordedSignal, float[] referenceSignal, int sampleRate)
        {
            var results = new List<(double frequency, double amplitudeDb)>();
            float[] trimmedRecorded = TrimSilenceEnds(recordedSignal, sampleRate, -50);
            float[] trimmedFeedback = null;

            if (UseFeedbackChannel && _feedbackSignal != null && _feedbackSignal.Length > 0)
            {
                int micTrimStart = FindOnsetAuto(recordedSignal, sampleRate);
                int feedbackLen = Math.Min(trimmedRecorded.Length, _feedbackSignal.Length - micTrimStart);
                if (feedbackLen > 0 && micTrimStart < _feedbackSignal.Length)
                {
                    trimmedFeedback = new float[feedbackLen];
                    Array.Copy(_feedbackSignal, micTrimStart, trimmedFeedback, 0, feedbackLen);
                }
                else trimmedFeedback = TrimSilenceEnds(_feedbackSignal, sampleRate, -50);
            }

            if (_selectedFrequencies == null || _selectedFrequencies.Count == 0) return results;
            int numSteps = _selectedFrequencies.Count;
            int samplesPerStep = trimmedRecorded.Length / numSteps;
            const double skipStartRatio = 0.25, skipEndRatio = 0.10;

            for (int stepIdx = 0; stepIdx < numSteps; stepIdx++)
            {
                int targetFreq   = _selectedFrequencies[stepIdx];
                int segmentStart = stepIdx * samplesPerStep;
                int segmentEnd   = Math.Min((stepIdx + 1) * samplesPerStep, trimmedRecorded.Length);
                int segmentLength = segmentEnd - segmentStart;
                int stableStart  = segmentStart + (int)(segmentLength * skipStartRatio);
                int stableEnd    = segmentEnd   - (int)(segmentLength * skipEndRatio);
                int stableLength = stableEnd - stableStart;
                if (stableLength <= 0) continue;

                float[] stableRecorded = new float[stableLength];
                Array.Copy(trimmedRecorded, stableStart, stableRecorded, 0, stableLength);
                double peakMic = GetFrequencyPeakMagnitude(stableRecorded, sampleRate, targetFreq);
                double finalMag = peakMic;

                if (UseFeedbackChannel && trimmedFeedback != null)
                {
                    int fbStart = Math.Min(stableStart, trimmedFeedback.Length);
                    int fbLen   = Math.Min(stableLength, trimmedFeedback.Length - fbStart);
                    if (fbLen > 0)
                    {
                        float[] stableFeedback = new float[fbLen];
                        Array.Copy(trimmedFeedback, fbStart, stableFeedback, 0, fbLen);
                        double peakRef = GetFrequencyPeakMagnitude(stableFeedback, sampleRate, targetFreq);
                        if (peakRef > 1e-12) finalMag = peakMic / peakRef;
                    }
                }
                results.Add((targetFreq, 20 * Math.Log10(finalMag + 1e-12)));
            }
            return results;
        }

        // Private utility functions
        private static int FindPeakIndex(float[] ir)
        {
            float maxVal = 0; int peakIndex = 0;
            for (int i = 0; i < ir.Length; i++)
            {
                float a = Math.Abs(ir[i]);
                if (a > maxVal) { maxVal = a; peakIndex = i; }
            }
            return peakIndex;
        }

        private float[] GetActiveReferenceSignal()
        {
            if (UseFeedbackChannel && _feedbackSignal != null && _feedbackSignal.Length > 0)
                return _feedbackSignal;
            return _originalSignal;
        }

        /// <summary>
        /// Resamples spectrum values to logarithmically spaced bins in the given frequency range.
        /// </summary>
        private static DeconvolutionResult ResampleToLogBins(
    double[] freqAxis, double[] magDb, double[] phaseDeg, double[] groupDelayMs,
    double f1, double f2, int logBins)
        {
            double[] outFreq = new double[logBins], outMag = new double[logBins];
            double[] outPhas = new double[logBins], outGd = new double[logBins];

            double logF1 = Math.Log(f1), logF2 = Math.Log(f2);

            for (int i = 0; i < logBins; i++)
            {
                double hz = Math.Exp(logF1 + (double)i / (logBins - 1) * (logF2 - logF1));
                outFreq[i] = hz;

                int lo = BinarySearchLo(freqAxis, hz);
                int hi = Math.Min(lo + 1, freqAxis.Length - 1);

                double t = (hz - freqAxis[lo]) / (freqAxis[hi] - freqAxis[lo]);
                t = Math.Clamp(t, 0.0, 1.0);

                // Cubic interpolation (Smoothstep) for a smooth REW appearance
                double smoothT = t * t * (3 - 2 * t);

                outMag[i] = magDb[lo] + (magDb[hi] - magDb[lo]) * smoothT;
                outPhas[i] = phaseDeg[lo] + (phaseDeg[hi] - phaseDeg[lo]) * smoothT;
                outGd[i] = groupDelayMs[lo] + (groupDelayMs[hi] - groupDelayMs[lo]) * smoothT;
            }

            return new DeconvolutionResult
            {
                FrequencyAxis = outFreq,
                RawMagnitudeDb = outMag,
                Phasedeg = outPhas,
                GroupDelayMs = outGd
            };
        }

        private static DeconvolutionResult ResampleToMaxLogBins(
    double[] freqAxis, double[] magDb, double f1, double f2, int logBins)
        {
            double[] outFreq = new double[logBins];
            double[] outMag = new double[logBins];
            double logF1 = Math.Log(f1);
            double logF2 = Math.Log(f2);

            for (int i = 0; i < logBins; i++)
            {
                double t = (double)i / (logBins - 1);
                double hzCenter = Math.Exp(logF1 + t * (logF2 - logF1));

                // Compute the boundaries of this logarithmic "bin"
                double hzPrev = Math.Exp(logF1 + (double)(i - 0.5) / (logBins - 1) * (logF2 - logF1));
                double hzNext = Math.Exp(logF1 + (double)(i + 0.5) / (logBins - 1) * (logF2 - logF1));

                outFreq[i] = hzCenter;

                // Find all FFT bins that fall into this range
                int idxStart = BinarySearchLo(freqAxis, hzPrev);
                int idxEnd = BinarySearchLo(freqAxis, hzNext);

                if (idxStart == idxEnd)
                {
                    int hi = Math.Min(idxStart + 1, freqAxis.Length - 1);
                    double frac = (hzCenter - freqAxis[idxStart]) / (freqAxis[hi] - freqAxis[idxStart] + 1e-9);
                    outMag[i] = magDb[idxStart] + frac * (magDb[hi] - magDb[idxStart]);
                }
                else
                {
                    double maxVal = double.NegativeInfinity;
                    for (int k = idxStart; k <= idxEnd; k++)
                    {
                        if (magDb[k] > maxVal) maxVal = magDb[k];
                    }
                    outMag[i] = maxVal;
                }
            }

            return new DeconvolutionResult
            {
                FrequencyAxis = outFreq,
                RawMagnitudeDb = outMag
            };
        }

        private static int BinarySearchLo(double[] arr, double val)
        {
            int lo = 0, hi = arr.Length - 1;
            if (val <= arr[0]) return 0;
            if (val >= arr[hi]) return hi;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (arr[mid] <= val) lo = mid; else hi = mid;
            }
            return lo;
        }

        private static float[] TrimSilenceEnds(float[] signal, int sampleRate, double thresholdDb, double marginSec = 0.05)
        {
            int windowSize = (int)(0.005 * sampleRate);
            double threshold = Math.Pow(10, thresholdDb / 20.0);
            int start = 0, end = signal.Length - 1;
            for (int i = 0; i < signal.Length - windowSize; i += windowSize)
            {
                double rms = 0;
                for (int j = 0; j < windowSize; j++) rms += signal[i + j] * signal[i + j];
                rms = Math.Sqrt(rms / windowSize);
                if (rms > threshold) { start = i; break; }
            }
            for (int i = signal.Length - windowSize; i >= windowSize; i -= windowSize)
            {
                double rms = 0;
                for (int j = 0; j < windowSize; j++) rms += signal[i - j] * signal[i - j];
                rms = Math.Sqrt(rms / windowSize);
                if (rms > threshold) { end = i; break; }
            }
            int margin = (int)(marginSec * sampleRate);
            start = Math.Max(0, start - margin);
            end = Math.Min(signal.Length - 1, end + margin);
            int length = end - start + 1;
            if (length <= 0) return signal;
            float[] trimmed = new float[length];
            Array.Copy(signal, start, trimmed, 0, length);
            return trimmed;
        }

        private static double GetFrequencyPeakMagnitude(float[] signal, int sampleRate, double targetFreq)
        {
            int n = signal.Length;
            if (n < 2) return 0.0;

            int fftSize = NextPow2(n);
            float[] padded = new float[fftSize];
            Array.Copy(signal, padded, n);

            ForwardFft(padded, out double[] real, out double[] imag);
            double freqRes = (double)sampleRate / fftSize;
            int bin = Math.Clamp((int)Math.Round(targetFreq / freqRes), 1, fftSize / 2 - 1);
            double re = real[bin], im = imag[bin];
            return Math.Sqrt(re * re + im * im);
        }

    }
}
