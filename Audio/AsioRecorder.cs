using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeaSound
{
    internal sealed class AsioRecorder : IDisposable
    {
        private AsioOut? _asio;
        private WaveFileWriter? _writer;
        private WaveFileWriter? _refWriter;
        private readonly object _writerLock = new();
        private string? _recordingPath;
        private string? _refPath;
        private TaskCompletionSource<bool>? _recordingStoppedTcs;
        private int _actualSampleRate;
        private bool _writePcm;
        private int _pcmBits;
        private bool _clipDetected = false;
        private const float ClipThreshold = 0.99f;

        public string? DriverName { get; set; }
        public int InputChannelIndex = 0;
        public int OutputChannelIndex = 0;
        public bool IsRecording { get; private set; }
        public int SignalChannelIndex = 0;
        public bool UseReferenceChannel = false;
        public int ReferenceChannelIndex = 1;

        public event Action<string>? OnRecordingStopped;
        public event Action<string>? OnError;
        public event Action? OnRecordingStarted;
        public event Action? OnClipDetected;
        public Func<string, bool>? OnSettingsMismatchConfirmationRequested { get; set; }

        public void StartRecording(string micFilePath, int sampleRate, int bitDepth = 32, bool isFloat = true,
            ISampleProvider? playbackProvider = null, string? refFilePath = null)
        {
            if (IsRecording) throw new InvalidOperationException("Already recording");
            if (string.IsNullOrWhiteSpace(DriverName)) throw new InvalidOperationException("ASIO driver name is not set");

            StopAndCleanup();
            _clipDetected = false;
            _recordingPath = micFilePath;
            _refPath = (UseReferenceChannel && refFilePath != null) ? refFilePath : null;
            _writePcm = !isFloat;
            _pcmBits = _writePcm ? (bitDepth is 16 or 24 or 32 ? bitDepth : 16) : 32;

            try
            {
                _asio = new AsioOut(DriverName);
                int driverInputChannels = _asio.DriverInputChannelCount;
                int driverOutputChannels = _asio.DriverOutputChannelCount;
                Debug.WriteLine($"[AsioRecorder] Driver: {driverInputChannels} inputs, {driverOutputChannels} outputs");

                ISampleProvider monoSource = playbackProvider ?? new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
                if (monoSource.WaveFormat.SampleRate != sampleRate) monoSource = new WdlResamplingSampleProvider(monoSource, sampleRate);
                if (monoSource.WaveFormat.Channels != 1) monoSource = monoSource.ToMono();

                // Apply calibration gain
                float calGain = Preferences.Load().GetCalibrationGainLinear();
                monoSource = new VolumeSampleProvider(monoSource) { Volume = calGain };

                int outChannels = Math.Max(1, driverOutputChannels);
                ISampleProvider playbackFinal = outChannels == 1 ? monoSource : new MonoToMultiChannelProvider(monoSource, outChannels);
                var playback = new SampleProviderToWaveProvider(playbackFinal);

                _asio.InitRecordAndPlayback(playback, driverInputChannels, sampleRate);
                _asio.ChannelOffset = 0;
                _asio.InputChannelOffset = 0;

                _actualSampleRate = GetActualSampleRate(sampleRate);
                var monoFormat = isFloat ? WaveFormat.CreateIeeeFloatWaveFormat(_actualSampleRate, 1) : new WaveFormat(_actualSampleRate, _pcmBits, 1);

                string? mismatchMessage = BuildAsioMismatchMessage(sampleRate, bitDepth, isFloat, _actualSampleRate, _pcmBits);
                if (!string.IsNullOrWhiteSpace(mismatchMessage) && !ConfirmSettingsMismatch(mismatchMessage))
                    throw new InvalidOperationException("Spuštìní ASIO nahrávání bylo zrušeno kvùli neshodì nastavení.");

                lock (_writerLock)
                {
                    _writer = new WaveFileWriter(micFilePath, monoFormat);
                    if (_refPath != null) _refWriter = new WaveFileWriter(_refPath, monoFormat);
                }

                _asio.AudioAvailable += OnAsioAudioAvailable;
                _asio.PlaybackStopped += OnAsioPlaybackStopped;
                _recordingStoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                IsRecording = true;
                _asio.Play();
                OnRecordingStarted?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AsioRecorder] Start failed: {ex.Message}");
                OnError?.Invoke($"ASIO nahr?v?n? selhalo: {ex.Message}");
                StopAndCleanup();
                throw;
            }
        }

        private bool ConfirmSettingsMismatch(string message)
        {
            return OnSettingsMismatchConfirmationRequested?.Invoke(message) ?? true;
        }

        private static string? BuildAsioMismatchMessage(int requestedSampleRate, int requestedBitDepth, bool requestedIsFloat, int actualSampleRate, int actualPcmBits)
        {
            int normalizedRequestedPcmBits = requestedBitDepth is 16 or 24 or 32 ? requestedBitDepth : 16;
            bool sampleRateMismatch = requestedSampleRate != actualSampleRate;
            bool bitDepthMismatch = !requestedIsFloat && normalizedRequestedPcmBits != actualPcmBits;

            if (!sampleRateMismatch && !bitDepthMismatch)
                return null;

            string requestedFormat = requestedIsFloat
                ? $"{requestedSampleRate} Hz, Float32"
                : $"{requestedSampleRate} Hz, PCM {normalizedRequestedPcmBits}-bit";

            string actualFormat = requestedIsFloat
                ? $"{actualSampleRate} Hz, Float32"
                : $"{actualSampleRate} Hz, PCM {actualPcmBits}-bit";

            return
                $"Vybrané ASIO nastavení se neshoduje se skuteèným spuštìním.\n\n" +
                $"Vybráno: {requestedFormat}\n" +
                $"Spustí se: {actualFormat}\n\n" +
                "Chcete i pøesto pokraèovat?";
        }

        public void StopRecording()
        {
            if (!IsRecording) return;
            try { _asio?.Stop(); } catch (Exception ex) { Debug.WriteLine($"[AsioRecorder] Stop error: {ex.Message}"); }
        }

        public async Task StopRecordingAsync(int timeoutMs = 3000)
        {
            var tcs = _recordingStoppedTcs;
            StopRecording();
            if (tcs == null) return;
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));
                if (!tcs.Task.IsCompleted) StopAndCleanup();
            }
            catch (TaskCanceledException) { }
        }

        private void OnAsioAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
        {
            try
            {
                int totalInputChannels = e.InputBuffers.Length;
                if (totalInputChannels <= 0) return;
                float[] interleaved = e.GetAsInterleavedSamples();
                if (interleaved == null || interleaved.Length == 0) return;
                int frameCount = e.SamplesPerBuffer;
                float[] micSamples = ExtractMonoChannel(interleaved, totalInputChannels, SignalChannelIndex, frameCount);

                if (!_clipDetected)
                    for (int i = 0; i < micSamples.Length; i++)
                        if (Math.Abs(micSamples[i]) > ClipThreshold)
                        { _clipDetected = true; try { OnClipDetected?.Invoke(); } catch { } break; }

                lock (_writerLock)
                {
                    if (_writer == null) return;
                    if (_writePcm) PcmWriterHelper.WritePcmSamples(_writer, micSamples, _pcmBits);
                    else _writer.WriteSamples(micSamples, 0, micSamples.Length);

                    if (_refWriter != null && UseReferenceChannel)
                    {
                        float[] refSamples = ExtractMonoChannel(interleaved, totalInputChannels, ReferenceChannelIndex, frameCount);
                        if (_writePcm) PcmWriterHelper.WritePcmSamples(_refWriter, refSamples, _pcmBits);
                        else _refWriter.WriteSamples(refSamples, 0, refSamples.Length);
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[AsioRecorder] AudioAvailable error: {ex.Message}"); OnError?.Invoke($"Chyba pøi zpracování audio: {ex.Message}"); }
        }

        private static float[] ExtractMonoChannel(float[] interleaved, int totalChannels, int channelIndex, int frameCount)
        {
            if (totalChannels == 1) return interleaved;
            int ch = Math.Clamp(channelIndex, 0, totalChannels - 1);
            var result = new float[frameCount];
            for (int i = 0; i < frameCount; i++) result[i] = interleaved[i * totalChannels + ch];
            return result;
        }

        private void OnAsioPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e?.Exception != null) Debug.WriteLine($"[AsioRecorder] Stopped with exception: {e.Exception.Message}");
            var path = _recordingPath;
            StopAndCleanup();
            if (!string.IsNullOrWhiteSpace(path)) OnRecordingStopped?.Invoke(path);
        }

        private int GetActualSampleRate(int requested)
        {
            try
            {
                if (_asio == null) return requested;
                var driverProp = typeof(AsioOut).GetProperty("Driver", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var driver = driverProp?.GetValue(_asio);
                if (driver == null) return requested;
                var srProp = driver.GetType().GetProperty("SampleRate");
                if (srProp?.GetValue(driver) is double sr && sr > 0) return (int)Math.Round(sr);
            }
            catch (Exception ex) { Debug.WriteLine($"[AsioRecorder] Failed to get actual sample rate: {ex.Message}"); }
            return requested;
        }

        private void StopAndCleanup()
        {
            IsRecording = false;
            if (_asio != null)
            {
                try { _asio.AudioAvailable -= OnAsioAudioAvailable; } catch { }
                try { _asio.PlaybackStopped -= OnAsioPlaybackStopped; } catch { }
            }
            try { _asio?.Stop(); } catch { }
            try { _asio?.Dispose(); } catch { }
            _asio = null;
            lock (_writerLock)
            {
                try { _writer?.Flush(); } catch { } try { _writer?.Dispose(); } catch { } _writer = null;
                try { _refWriter?.Flush(); } catch { } try { _refWriter?.Dispose(); } catch { } _refWriter = null;
            }
            _recordingStoppedTcs?.TrySetResult(true);
            _recordingStoppedTcs = null;
            _recordingPath = null; _refPath = null;
        }

        public void Dispose() { StopAndCleanup(); GC.SuppressFinalize(this); }
    }
}
