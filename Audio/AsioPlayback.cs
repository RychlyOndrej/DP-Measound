using System;
using System.Diagnostics;
using System.Threading;
using NAudio.Wave;

namespace MeaSound
{
    internal sealed class AsioPlayback : IDisposable
    {
        private sealed class SampleProviderWaveProvider : IWaveProvider
        {
            private readonly ISampleProvider _source;
            private readonly float[] _buffer;

            public SampleProviderWaveProvider(ISampleProvider source, int bufferSamples = 4096)
            {
                _source = source;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, source.WaveFormat.Channels);
                _buffer = new float[Math.Max(256, bufferSamples)];
            }

            public WaveFormat WaveFormat { get; }

            public int Read(byte[] buffer, int offset, int count)
            {
                int samplesRequested = count / sizeof(float);
                if (samplesRequested <= 0) return 0;
                int totalSamplesRead = 0;
                while (totalSamplesRead < samplesRequested)
                {
                    int toRead = Math.Min(_buffer.Length, samplesRequested - totalSamplesRead);
                    int read = _source.Read(_buffer, 0, toRead);
                    if (read <= 0) break;
                    Buffer.BlockCopy(_buffer, 0, buffer, offset + totalSamplesRead * sizeof(float), read * sizeof(float));
                    totalSamplesRead += read;
                }
                return totalSamplesRead * sizeof(float);
            }
        }

        private AsioOut? _asio;
        private AudioFileReader? _reader;

        public string? DriverName { get; set; }
        public int OutputChannelIndex { get; set; } = 0;

        public void PlayFileBlocking(string filePath, int sampleRate, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(DriverName))
                throw new InvalidOperationException("ASIO driver is not selected.");

            Stop();
            _reader = new AudioFileReader(filePath);
            ISampleProvider sp = _reader.ToSampleProvider();

            if (sp.WaveFormat.SampleRate != sampleRate)
                sp = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(sp, sampleRate);
            if (sp.WaveFormat.Channels != 1)
                sp = sp.ToMono();

            // Apply calibration gain
            float calGain = Preferences.Load().GetCalibrationGainLinear();
            sp = new NAudio.Wave.SampleProviders.VolumeSampleProvider(sp) { Volume = calGain };

            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            _asio = new AsioOut(DriverName);
            try { _asio.ChannelOffset = Math.Max(0, OutputChannelIndex); } catch { }

            _asio.Init(new SampleProviderWaveProvider(sp));
            _asio.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null) { Debug.WriteLine("[AsioPlayback] PlaybackStopped exception: " + e.Exception.Message); tcs.TrySetException(e.Exception); }
                else tcs.TrySetResult(true);
            };
            _asio.Play();

            using (token.Register(() => { try { _asio?.Stop(); } catch { } tcs.TrySetCanceled(token); }))
                tcs.Task.GetAwaiter().GetResult();
        }

        public void Stop()
        {
            try { _asio?.Stop(); } catch { }
            try { _asio?.Dispose(); } catch { }
            _asio = null;
            try { _reader?.Dispose(); } catch { }
            _reader = null;
        }

        public void Dispose() => Stop();
    }
}
