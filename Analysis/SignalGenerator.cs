using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NWaves.Signals.Builders;
using NWaves.Signals.Builders.Base;

namespace MeaSound
{
    internal class SignalGenerator
    {
        private readonly int _sampleRate;
        private readonly int _channels;
        private const float SilenceDuration = 0.25f;
        private const double DefaultFadeMs = 10.0;

        public static float GetSilenceDuration() => SilenceDuration;

        public int SampleRate => _sampleRate;
        public int Channels => _channels;

        public SignalGenerator(int sampleRate, int channels = 1)
        {
            _sampleRate = sampleRate;
            _channels = channels;
        }

        public float[] GenerateSamples(
            TestSignalType type,
            int durationSeconds = 2,
            float constantFreq = 1000f,
            int mlsOrder = 10,
            int startFreq = 20,
            int endFreq = 20000,
            WaveformType waveform = WaveformType.Sine,
            SweepType sweepType = SweepType.Linear,
            float[]? multiToneFreqs = null,
            string? customFilePath = null)
        {
            float[] signal = type switch
            {
                TestSignalType.SineSweep => GenerateSineSweep(startFreq, endFreq, durationSeconds, sweepType),
                TestSignalType.MLS => GenerateMLS(mlsOrder),
                TestSignalType.WhiteNoise => GenerateWhiteNoise(durationSeconds),
                TestSignalType.PinkNoise => GeneratePinkNoise(durationSeconds),
                TestSignalType.ConstantTone => GenerateTone(durationSeconds, constantFreq, waveform),
                TestSignalType.MultiTone => GenerateMultiTone(multiToneFreqs ?? [1000f], durationSeconds),
                TestSignalType.SteppedSine => GenerateSteppedSine(multiToneFreqs ?? [100, 1000, 10000], durationSeconds),
                TestSignalType.CustomFile => LoadCustomFile(customFilePath),
                _ => throw new NotSupportedException($"Nepodporovaný typ signálu: {type}")
            };

            Normalize(signal);

            switch (type)
            {
                case TestSignalType.SineSweep:
                case TestSignalType.SteppedSine:
                    break;
                case TestSignalType.MLS:
                    ApplyFade(signal, fadeDurationMs: 1.0);
                    break;
                default:
                    ApplyFade(signal, DefaultFadeMs);
                    break;
            }

            if (type == TestSignalType.SineSweep)
                return signal;

            return AddSilencePadding(signal);
        }

        #region Signal Generators

        public float[] GenerateSineSweep(int startFreq, int endFreq, int durationSeconds, SweepType sweepType)
        {
            int N = _sampleRate * durationSeconds;
            float[] samples = new float[N];

            for (int n = 0; n < N; n++)
            {
                double t = (double)n / _sampleRate;
                double phase;

                switch (sweepType)
                {
                    case SweepType.Linear:
                        phase = 2 * Math.PI * (startFreq * t + 0.5 * (endFreq - startFreq) * t * t / durationSeconds);
                        break;

                    case SweepType.ExponentialSweep:
                    {
                        double K = Math.Log((double)endFreq / startFreq);
                        phase = 2 * Math.PI * startFreq * durationSeconds / K * (Math.Exp(K * t / durationSeconds) - 1);
                        break;
                    }

                    case SweepType.PowerLaw:
                    {
                        double k = 2.0;
                        phase = 2 * Math.PI * (startFreq * t + (endFreq - startFreq) * Math.Pow(t, k + 1) / ((k + 1) * Math.Pow(durationSeconds, k)));
                        break;
                    }

                    default:
                        phase = 0;
                        break;
                }

                samples[n] = (float)Math.Sin(phase);
            }
            return samples;
        }

        public float[] GenerateMLS(int order)
        {
            var builder = new MlsBuilder(order);
            builder.SampledAt(_sampleRate).OfLength((1 << order) - 1);
            return builder.Build().Samples;
        }

        public float[] GenerateWhiteNoise(int durationSeconds)
        {
            return new WhiteNoiseBuilder()
                .OfLength(_sampleRate * durationSeconds)
                .SampledAt(_sampleRate)
                .Build().Samples;
        }

        public float[] GeneratePinkNoise(int durationSeconds)
        {
            return new PinkNoiseBuilder()
                .OfLength(_sampleRate * durationSeconds)
                .SampledAt(_sampleRate)
                .Build().Samples;
        }

        public float[] GenerateTone(int durationSeconds, float frequency, WaveformType waveform)
        {
            SignalBuilder builder = waveform switch
            {
                WaveformType.Square => new SquareWaveBuilder().SetParameter("freq", frequency),
                WaveformType.Sawtooth => new SawtoothBuilder().SetParameter("freq", frequency),
                WaveformType.Triangle => new TriangleWaveBuilder().SetParameter("freq", frequency),
                _ => new SineBuilder().SetParameter("freq", frequency)
            };
            return builder.OfLength(_sampleRate * durationSeconds).SampledAt(_sampleRate).Build().Samples;
        }

        public float[] GenerateMultiTone(float[] frequencies, int durationSeconds)
        {
            int totalSamples = durationSeconds * _sampleRate;
            float[] samples = new float[totalSamples];

            foreach (var freq in frequencies)
            {
                double phase = 0;
                double increment = 2.0 * Math.PI * freq / _sampleRate;
                for (int i = 0; i < totalSamples; i++)
                {
                    samples[i] += (float)Math.Sin(phase);
                    phase += increment;
                }
            }
            return samples;
        }

        public float[] GenerateSteppedSine(float[] frequencies, int totalDurationSeconds)
        {
            if (frequencies == null || frequencies.Length == 0)
                return new float[_sampleRate * totalDurationSeconds];

            Debug.WriteLine($"Generating Stepped Sine with {frequencies.Length} steps.");

            int samplesPerStep = (_sampleRate * totalDurationSeconds) / frequencies.Length;
            float[] totalSamples = new float[samplesPerStep * frequencies.Length];
            int crossFadeSamples = (int)(0.005 * _sampleRate);

            for (int i = 0; i < frequencies.Length; i++)
            {
                float freq = frequencies[i];
                int startOffset = i * samplesPerStep;

                for (int n = 0; n < samplesPerStep; n++)
                {
                    double t = (double)n / _sampleRate;
                    float sample = (float)Math.Sin(2 * Math.PI * freq * t);

                    if (n < crossFadeSamples)
                        sample *= (float)(0.5 * (1.0 - Math.Cos(Math.PI * n / crossFadeSamples)));
                    else if (n > samplesPerStep - crossFadeSamples)
                        sample *= (float)(0.5 * (1.0 - Math.Cos(Math.PI * (samplesPerStep - n) / crossFadeSamples)));

                    totalSamples[startOffset + n] = sample;
                }
            }

            return totalSamples;
        }

        public float[] LoadCustomFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Cesta k souboru je prázdná.", nameof(filePath));

            using var reader = new AudioFileReader(filePath);
            if (reader.WaveFormat.SampleRate != _sampleRate)
            {
                var resampler = new WdlResamplingSampleProvider(reader, _sampleRate);
                List<float> resampled = new();
                float[] buffer = new float[_sampleRate];
                int read;
                while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                    resampled.AddRange(buffer.Take(read));
                return resampled.ToArray();
            }

            var samples = new float[reader.Length / sizeof(float)];
            int readCount = reader.Read(samples, 0, samples.Length);
            Array.Resize(ref samples, readCount);
            return samples;
        }

        #endregion

        #region Signal Processing

        private static void Normalize(float[] signal)
        {
            if (signal == null || signal.Length == 0) return;

            float max = 0;
            for (int i = 0; i < signal.Length; i++)
            {
                float abs = Math.Abs(signal[i]);
                if (abs > max) max = abs;
            }

            if (max > 0)
            {
                float multiplier = 1.0f / max;
                for (int i = 0; i < signal.Length; i++)
                    signal[i] *= multiplier;

                Debug.WriteLine($"Signal normalized by factor {multiplier:F2} (Peak was {max:F4})");
            }
        }

        private void ApplyFade(float[] signal, double fadeDurationMs)
        {
            int fadeSamples = (int)(fadeDurationMs / 1000.0 * _sampleRate);
            fadeSamples = Math.Min(fadeSamples, signal.Length / 2);
            if (fadeSamples <= 0) return;

            for (int i = 0; i < fadeSamples; i++)
            {
                float multiplier = (float)(0.5 * (1.0 - Math.Cos(Math.PI * i / fadeSamples)));
                signal[i] *= multiplier;
                signal[signal.Length - 1 - i] *= multiplier;
            }
        }

        private float[] AddSilencePadding(float[] signal)
        {
            int silenceSamples = (int)(_sampleRate * SilenceDuration);
            float[] paddedSignal = new float[signal.Length + 2 * silenceSamples];
            Array.Copy(signal, 0, paddedSignal, silenceSamples, signal.Length);
            return paddedSignal;
        }

        #endregion

        #region WAV Export

        public static void SaveToWavFile(float[] samples, int sampleRate, int channels, string filePath, int bitDepth = 32, bool isFloat = true)
        {
            WaveFormat format = isFloat
                ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
                : new WaveFormat(sampleRate, bitDepth, channels);

            using var writer = new WaveFileWriter(filePath, format);

            if (isFloat)
            {
                writer.WriteSamples(samples, 0, samples.Length);
            }
            else
            {
                foreach (var sample in samples)
                {
                    switch (bitDepth)
                    {
                        case 16:
                            short sampleInt16 = (short)(sample * short.MaxValue);
                            writer.WriteSample(sampleInt16 / (float)short.MaxValue);
                            break;
                        case 24:
                        case 32:
                            writer.WriteSample(sample);
                            break;
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// Builds a playback buffer for operation without a hardware reference channel.
        /// </summary>
        /// <param name="signal">Original signal from UI (float[], untouched).</param>
        /// <param name="startFreq">Starting frequency of the signal in Hz (for pre-roll extrapolation in sweeps).</param>
        /// <param name="endFreq">Ending frequency of the signal in Hz (for post-roll extrapolation in sweeps).</param>
        /// <param name="sweepType">Sweep type of the original signal.</param>
        /// <param name="signalDurationSeconds">Duration of the original signal without edge silence.</param>
        /// <param name="isSweepSignal">
        /// <c>true</c> = sweep (SineSweep/MLS) → pre/post-roll extrapolates phase.<br/>
        /// <c>false</c> = others (ConstantTone, MultiTone, …) → pre/post-roll is silence with fade.
        /// </param>
        /// <param name="layout">Output: offsets of individual sections in samples.</param>
        public float[] BuildSyncBuffer(
            float[] signal,
            int startFreq,
            int endFreq,
            SweepType sweepType,
            double signalDurationSeconds,
            bool isSweepSignal,
            out SyncBufferLayout layout)
            => BuildBuffer(signal, startFreq, endFreq, sweepType, signalDurationSeconds,
                           isSweepSignal, "BuildSyncBuffer", out layout);

        /// <summary>
        /// Builds a playback buffer for operation with a hardware loopback reference.
        /// </summary>
        /// <param name="signal">Input signal from the UI.</param>
        /// <param name="startFreq">Signal start frequency in Hz.</param>
        /// <param name="endFreq">Signal end frequency in Hz.</param>
        /// <param name="sweepType">Sweep type used by the source signal.</param>
        /// <param name="signalDurationSeconds">Source signal duration without edge silence.</param>
        /// <param name="isSweepSignal">True for sweep-style signals that need phase extrapolation.</param>
        /// <param name="layout">Outputs section offsets and lengths in samples.</param>
        public float[] BuildReferenceBuffer(
            float[] signal,
            int startFreq,
            int endFreq,
            SweepType sweepType,
            double signalDurationSeconds,
            bool isSweepSignal,
            out SyncBufferLayout layout)
            => BuildBuffer(signal, startFreq, endFreq, sweepType, signalDurationSeconds,
                           isSweepSignal, "BuildReferenceBuffer", out layout);

        private float[] BuildBuffer(
            float[] signal,
            int startFreq,
            int endFreq,
            SweepType sweepType,
            double signalDurationSeconds,
            bool isSweepSignal,
            string debugLabel,
            out SyncBufferLayout layout)
        {
            ArgumentNullException.ThrowIfNull(signal);

            int preRollSamples  = isSweepSignal ? ComputeRollLength(startFreq) : (int)(_sampleRate * 0.150);
            int postRollSamples = isSweepSignal ? ComputeRollLength(endFreq)   : (int)(_sampleRate * 0.150);

            float signalPeak = ComputePeak(signal);
            if (signalPeak < 1e-6f) signalPeak = 1.0f;

            float[] preRoll = isSweepSignal
                ? BuildSweepPreRoll(preRollSamples,  startFreq, endFreq, signalDurationSeconds, sweepType, signalPeak)
                : BuildSilentRoll(preRollSamples,  fadeIn: true);

            float[] postRoll = isSweepSignal
                ? BuildSweepPostRoll(postRollSamples, startFreq, endFreq, signalDurationSeconds, sweepType, signalPeak)
                : BuildSilentRoll(postRollSamples, fadeIn: false);

            int preRollOffset  = 0;
            int signalOffset   = preRollSamples;
            int postRollOffset = signalOffset + signal.Length;
            int totalLength    = postRollOffset + postRollSamples;

            float[] output = new float[totalLength];
            Array.Copy(preRoll,  0, output, preRollOffset,  preRollSamples);
            Array.Copy(signal,   0, output, signalOffset,   signal.Length);
            Array.Copy(postRoll, 0, output, postRollOffset, postRollSamples);

            layout = new SyncBufferLayout(
                PreRollOffset:  preRollOffset,
                PreRollLength:  preRollSamples,
                SignalOffset:   signalOffset,
                SignalLength:   signal.Length,
                PostRollOffset: postRollOffset,
                PostRollLength: postRollSamples,
                SampleRate:     _sampleRate);

            Debug.WriteLine($"[{debugLabel}] total={totalLength} ({(double)totalLength/_sampleRate:F3}s) | " +
                            $"preRoll={preRollSamples} ({preRollSamples*1000/_sampleRate}ms) " +
                            $"signal={signal.Length} postRoll={postRollSamples} ({postRollSamples*1000/_sampleRate}ms)");
            return output;
        }

        // Shared private helpers

        private static float ComputePeak(float[] signal)
        {
            float max = 0f;
            foreach (float s in signal)
            {
                float abs = MathF.Abs(s);
                if (abs > max) max = abs;
            }
            return max;
        }

        /// <summary>Builds pre-roll sweep continuation with phase continuity.</summary>
        private float[] BuildSweepPreRoll(int length, int startFreq, int endFreq,
                                          double durationSec, SweepType sweepType, float amplitude)
        {
            float[] pr = new float[length];
            for (int i = 0; i < length; i++)
            {
                double t = -(double)(length - i) / _sampleRate;
                double phase = ComputeSweepPhase(t, startFreq, endFreq, durationSec, sweepType);
                pr[i] = (float)(Math.Sin(phase) * amplitude);
            }
            ApplyHanningFade(pr, fadeIn: true);
            return pr;
        }

        /// <summary>Builds post-roll sweep continuation with smooth fade-out.</summary>
        private float[] BuildSweepPostRoll(int length, int startFreq, int endFreq,
                                            double durationSec, SweepType sweepType, float amplitude)
        {
            float[] po = new float[length];
            for (int i = 0; i < length; i++)
            {
                double t = durationSec + (double)i / _sampleRate;
                double phase = ComputeSweepPhase(t, startFreq, endFreq, durationSec, sweepType);
                po[i] = (float)(Math.Sin(phase) * amplitude);
            }
            ApplyHanningFade(po, fadeIn: false);
            return po;
        }

        /// <summary>Computes pre/post-roll length from minimum duration and period count.</summary>
        private int ComputeRollLength(int edgeFreq, int minPeriods = 6, double minSec = 0.150)
        {
            double perPeriod = edgeFreq > 0 ? (double)_sampleRate / edgeFreq : 0;
            int bySec    = (int)(_sampleRate * minSec);
            int byPeriod = (int)(perPeriod * minPeriods);
            return Math.Max(bySec, byPeriod);
        }

        /// <summary>Computes instantaneous sweep phase at time <paramref name="t"/>.</summary>
        private static double ComputeSweepPhase(double t, int startFreq, int endFreq,
                                                double durationSeconds, SweepType sweepType)
        {
            return sweepType switch
            {
                SweepType.Linear =>
                    2.0 * Math.PI * (startFreq * t
                        + 0.5 * (endFreq - startFreq) * t * t / durationSeconds),

                SweepType.ExponentialSweep =>
                    2.0 * Math.PI * startFreq * durationSeconds
                        / Math.Log((double)endFreq / startFreq)
                        * (Math.Exp(Math.Log((double)endFreq / startFreq) * t / durationSeconds) - 1.0),

                SweepType.PowerLaw =>
                    2.0 * Math.PI * (startFreq * t
                        + (endFreq - startFreq) * Math.Pow(Math.Abs(t), 3.0) * Math.Sign(t)
                          / (3.0 * Math.Pow(durationSeconds, 2.0))),

                _ => 2.0 * Math.PI * startFreq * t
            };
        }

        private static float[] BuildSilentRoll(int length, bool fadeIn)
            => new float[length];

        private static void ApplyHanningFade(float[] buffer, bool fadeIn)
        {
            int n = buffer.Length;
            for (int i = 0; i < n; i++)
            {
                double w = fadeIn
                    ? 0.5 * (1.0 - Math.Cos(Math.PI * i / n))
                    : 0.5 * (1.0 - Math.Cos(Math.PI * (n - i) / n));
                buffer[i] *= (float)w;
            }
        }
    }

    /// <summary>
    /// Describes offsets and lengths of sections in a generated playback buffer.
    /// </summary>
    internal record SyncBufferLayout(
        /// <summary>Pre-roll start offset in samples.</summary>
        int PreRollOffset,
        /// <summary>Pre-roll length in samples.</summary>
        int PreRollLength,
        /// <summary>Main signal start offset in samples.</summary>
        int SignalOffset,
        /// <summary>Main signal length in samples.</summary>
        int SignalLength,
        /// <summary>Post-roll start offset in samples.</summary>
        int PostRollOffset,
        /// <summary>Post-roll length in samples.</summary>
        int PostRollLength,
        /// <summary>Playback sample rate.</summary>
        int SampleRate)
    {
        /// <summary>Main signal start time in seconds.</summary>
        public double SignalOffsetSeconds => (double)SignalOffset / SampleRate;

        /// <summary>Main signal start time in seconds.</summary>
        public double SignalStartSeconds => SignalOffsetSeconds;

        /// <summary>Total buffer length in samples.</summary>
        public int TotalLength => PostRollOffset + PostRollLength;

        /// <summary>
        /// Returns the recording slice that corresponds to the main signal section.
        /// </summary>
        public float[] SliceSignal(float[] recorded, int recordedSr)
        {
            if (recorded == null || recorded.Length == 0)
                return recorded ?? Array.Empty<float>();

            double ratio = recordedSr > 0 && SampleRate > 0
                ? (double)recordedSr / SampleRate
                : 1.0;

            int offset = (int)Math.Round(SignalOffset * ratio);
            int length = (int)Math.Round(SignalLength * ratio);

            if (length <= 0)
                return recorded;

            var slice = new float[length];
            int srcStart = Math.Max(0, offset);
            int srcEnd   = Math.Min(recorded.Length, offset + length);
            int dstStart = Math.Max(0, -offset);
            int copyLen  = Math.Max(0, srcEnd - srcStart);
            if (copyLen > 0)
                Array.Copy(recorded, srcStart, slice, dstStart, copyLen);
            return slice;
        }

        /// <summary>
        /// Returns the recording slice used for spectrogram rendering.
        /// </summary>
        public float[] SliceSpectrogram(float[] recorded, int recordedSr)
        {
            if (recorded == null || recorded.Length == 0)
                return recorded ?? Array.Empty<float>();

            double ratio = recordedSr > 0 && SampleRate > 0
                ? (double)recordedSr / SampleRate
                : 1.0;

            int offset = (int)Math.Round(SignalOffset * ratio);
            int length = (int)Math.Round(SignalLength * ratio);

            if (length <= 0)
                return recorded;

            var slice = new float[length];
            int srcStart = Math.Max(0, offset);
            int srcEnd   = Math.Min(recorded.Length, offset + length);
            int dstStart = Math.Max(0, -offset);
            int copyLen  = Math.Max(0, srcEnd - srcStart);
            if (copyLen > 0)
                Array.Copy(recorded, srcStart, slice, dstStart, copyLen);
            return slice;
        }
    }
}
#nullable disable