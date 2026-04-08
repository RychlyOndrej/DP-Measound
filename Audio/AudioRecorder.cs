using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MeaSound
{
    internal sealed class AudioRecorder : IDisposable
    {
        #region Constants
        private const float ClipThreshold = 0.99f;
        #endregion

        #region Fields
        private WasapiCaptureNative? _wasapiCapture;
        private AsioRecorder? _asioRecorder;
        private WaveFileWriter? _writer;
        private WaveFileWriter? _refWriter;
        private readonly object _writerLock = new();
        private MMDevice? _recordingDevice;
        private EventHandler<WaveInEventArgs>? _dataAvailableHandler;
        private EventHandler<StoppedEventArgs>? _recordingStoppedHandler;
        private TaskCompletionSource<bool>? _recordingStoppedTcs;
        private bool _clipDetected;
        private bool _exclusiveSupported = true;
        private readonly HashSet<string> _exclusiveStartBlackList = new(StringComparer.OrdinalIgnoreCase);
        private (int SampleRate, int BitDepth, bool IsFloat)? _lastRequested;
        private (int BitDepth, bool IsFloat)? _lastFileFormatRequested;
        private string? _activeRecordingPath;
        private string? _activeRefPath;
        private readonly List<string> _lastExclusiveProbeResults = new();
        #endregion

        #region Properties
        public InputBackend Backend { get; set; } = InputBackend.Wasapi;
        public string? AsioDriverName { get; set; }
        public int CaptureChannelOverride { get; set; }
        public int InputSignalChannel { get; set; }
        public bool UseReferenceChannel { get; set; }
        public int InputReferenceChannel { get; set; } = 1;
        public int AsioInputChannelIndex { get; set; }
        public int AsioOutputChannelIndex { get; set; }
        public bool IsRecording { get; private set; }
        public bool IsExclusiveActive => _wasapiCapture?.ShareMode == AudioClientShareMode.Exclusive;
        public int? CurrentCaptureChannels
            => Backend == InputBackend.Asio
                ? (_asioRecorder?.IsRecording == true ? GetDesiredInputChannels() : null)
                : _wasapiCapture?.WaveFormat?.Channels;
        public WaveFormat? CurrentCaptureWaveFormat
            => Backend == InputBackend.Asio ? null : _wasapiCapture?.WaveFormat;
        public bool GetShareMode() => !_exclusiveSupported;
        public IReadOnlyList<string> LastExclusiveProbeResults => _lastExclusiveProbeResults;
        #endregion

        #region Events
        public Action<string>? OnError { get; set; }
        public Action<string>? OnRecordingStopped { get; set; }
        public Action? OnClipDetected { get; set; }
        public Func<string, bool>? OnSettingsMismatchConfirmationRequested { get; set; }
        public event Action? OnRecordingStarted;
        public event Action? RecordingStateChanged;
        #endregion

        #region Device and format probing
        public void SetDevice(MMDevice? device) => _recordingDevice = device;

        public static IReadOnlyList<string> GetAsioDriverNames()
        {
            try { return AsioOut.GetDriverNames(); }
            catch { return Array.Empty<string>(); }
        }

        public bool ReprobeExclusiveSupport(int sampleRate, int bitDepth, bool isFloat)
        {
            if (_recordingDevice == null) { _exclusiveSupported = false; return false; }
            int ch = GetDesiredInputChannels();
            var wf = BuildWaveFormat(sampleRate, ch, bitDepth, isFloat);
            bool ok = CanOpenExclusive(wf, out string? details);
            _exclusiveSupported = ok;
            Debug.WriteLine(ok
                ? $"[AudioRecorder] Exclusive probe OK: {sampleRate}Hz {ch}ch {(isFloat ? "float32" : $"pcm{wf.BitsPerSample}")}"
                : $"[AudioRecorder] Exclusive probe NO: {sampleRate}Hz {ch}ch ({details})");
            return ok;
        }

        public IReadOnlyList<string> ProbeExclusiveSupportedFormats(
            IEnumerable<int>? sampleRates = null,
            IEnumerable<(int BitDepth, bool IsFloat)>? formats = null,
            int channels = 1)
        {
            _lastExclusiveProbeResults.Clear();
            if (_recordingDevice == null) { _lastExclusiveProbeResults.Add("Není vybráno žádné nahrávací zaøízení."); return _lastExclusiveProbeResults; }

            var srList = (sampleRates ?? new[] { 8000, 11025, 22050, 32000, 44100, 48000, 96000, 192000 }).Distinct().ToArray();
            var fmtList = (formats ?? new (int BitDepth, bool IsFloat)[] { (32, true), (16, false), (24, false), (32, false) }).Distinct().ToArray();

            foreach (var sr in srList)
                foreach (var (bitDepth, isFloat) in fmtList)
                {
                    var wf = BuildWaveFormat(sr, channels, bitDepth, isFloat);
                    bool ok = CanOpenExclusive(wf, out string? details);
                    string label = isFloat ? $"{sr} Hz / 32-bit Float / {channels}ch" : $"{sr} Hz / {wf.BitsPerSample}-bit PCM / {channels}ch";
                    _lastExclusiveProbeResults.Add(ok ? $"OK: {label}" : $"NO: {label} ({details})");
                }

            return _lastExclusiveProbeResults;
        }

        private bool CanOpenExclusive(WaveFormat waveFormat, out string? details)
        {
            details = null;
            if (_recordingDevice == null) { details = "device=null"; return false; }
            try
            {
                var ac = _recordingDevice.AudioClient;
                bool supported = ac.IsFormatSupported(AudioClientShareMode.Exclusive, waveFormat);
                if (supported) { Debug.WriteLine($"[AudioRecorder] Format check OK: {FormatWaveFormat(waveFormat)}"); return true; }
                details = $"NotSupported; mix={FormatWaveFormat(ac.MixFormat)}";
                Debug.WriteLine($"[AudioRecorder] Format check FAILED: {details}");
                return false;
            }
            catch (Exception ex) { details = $"Exception: {ex.Message}"; Debug.WriteLine($"[AudioRecorder] Format check exception: {details}"); return false; }
        }
        #endregion

        #region Recording public API
        public void StartRecording(string micFilePath, int sampleRate, string? refFilePath = null)
            => StartRecording(micFilePath, sampleRate, bitDepth: 32, isFloat: true, refFilePath: refFilePath);

        public void StartRecording(string micFilePath, int sampleRate, int bitDepth, bool isFloat, string? refFilePath = null)
        {
            _clipDetected = false;
            if (Backend == InputBackend.Asio) { StartRecordingAsio(micFilePath, sampleRate, bitDepth, isFloat, playbackProvider: null, refFilePath: refFilePath); return; }

            ReleaseCapture();
            _lastFileFormatRequested = (bitDepth, isFloat);
            if (!InitializeWasapiCapture(sampleRate, bitDepth, isFloat)) return;
            if (!ConfirmWasapiSettingsMismatch(sampleRate, bitDepth, isFloat)) { ReleaseCapture(); return; }

            SetupWasapiHandlersAndWriter(micFilePath, bitDepth, isFloat, refFilePath);
            IsRecording = true;
            Debug.WriteLine($"[AudioRecorder] Starting capture: WASAPI {(_wasapiCapture!.ShareMode == AudioClientShareMode.Exclusive ? "Exclusive" : "Shared")}, mic={micFilePath}");

            try { _wasapiCapture.StartRecording(); }
            catch (Exception ex)
            {
                Debug.WriteLine("[AudioRecorder] StartRecording failed: " + ex.Message);
                if (_wasapiCapture.ShareMode == AudioClientShareMode.Exclusive) { TryFallbackToSharedOnStartFailure(micFilePath, ex, refFilePath); return; }
                OnError?.Invoke("Nahr?v?n? nelze spustit: " + ex.Message);
                ReleaseCapture(); return;
            }

            OnRecordingStarted?.Invoke();
            RecordingStateChanged?.Invoke();
        }

        public void StopRecording()
        {
            if (Backend == InputBackend.Asio) { try { _asioRecorder?.StopRecording(); } catch { } return; }
            Debug.WriteLine("[AudioRecorder] StopRecording called.");
            if (!IsRecording || _wasapiCapture == null) { _recordingStoppedTcs?.TrySetResult(true); RecordingStateChanged?.Invoke(); return; }
            try { _wasapiCapture.StopRecording(); }
            catch (Exception ex) { Debug.WriteLine("[AudioRecorder] StopRecording error: " + ex.Message); OnError?.Invoke("Chyba pøi zastavení nahrávání."); ReleaseCapture(); }
            RecordingStateChanged?.Invoke();
        }

        public async Task StopRecordingAsync(int timeoutMs = 3000)
        {
            if (Backend == InputBackend.Asio) { try { await (_asioRecorder?.StopRecordingAsync(timeoutMs) ?? Task.CompletedTask); } catch { } return; }
            var tcs = _recordingStoppedTcs;
            string? path = _activeRecordingPath;
            StopRecording();
            if (tcs == null) return;
            bool completed;
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                try { await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token)); completed = tcs.Task.IsCompleted; }
                catch (TaskCanceledException) { completed = false; }
            }
            if (completed) return;
            Debug.WriteLine($"[AudioRecorder] StopRecordingAsync timeout after {timeoutMs}ms - forcing cleanup");
            ForceCleanupAfterTimeout(path);
        }

        public void StopAllAudio() { if (IsRecording) StopRecording(); }
        #endregion

        #region WASAPI initialization
        private bool InitializeWasapiCapture(int sampleRate, int bitDepth, bool isFloat)
        {
            Debug.WriteLine("[AudioRecorder] Initializing capture...");
            if (_recordingDevice == null) { OnError?.Invoke("Není vybráno žádné nahrávací zaøízení."); return false; }
            _lastRequested = (sampleRate, bitDepth, isFloat);
            if (!string.IsNullOrWhiteSpace(_recordingDevice.ID) && _exclusiveStartBlackList.Contains(_recordingDevice.ID)) _exclusiveSupported = false;
            int channels = GetDesiredInputChannels();
            if (_exclusiveSupported && TryInitExclusiveCapture(sampleRate, channels, bitDepth, isFloat)) return true;
            return TryInitSharedCapture(sampleRate, channels, bitDepth, isFloat);
        }

        private bool TryInitExclusiveCapture(int sampleRate, int channels, int bitDepth, bool isFloat)
        {
            var desiredFormat = BuildWaveFormatForDevice(sampleRate, channels, bitDepth, isFloat);
            if (CanOpenExclusive(desiredFormat, out _))
            {
                try { _wasapiCapture = new WasapiCaptureNative(_recordingDevice!, AudioClientShareMode.Exclusive, desiredFormat, bufferMilliseconds: 100); Debug.WriteLine($"Mode: Exclusive (requested format)."); return true; }
                catch (Exception ex) { Debug.WriteLine($"Exclusive with requested format failed: {ex.Message}"); }
            }
            var mixFormat = _recordingDevice!.AudioClient?.MixFormat;
            if (mixFormat != null)
            {
                try { _wasapiCapture = new WasapiCaptureNative(_recordingDevice, AudioClientShareMode.Exclusive, mixFormat, bufferMilliseconds: 100); Debug.WriteLine("Mode: Exclusive (device mix format)."); return true; }
                catch (Exception ex) { Debug.WriteLine($"Exclusive with mix format failed: {ex.Message}"); }
            }
            _exclusiveSupported = false;
            return false;
        }

        private bool TryInitSharedCapture(int sampleRate, int channels, int bitDepth, bool isFloat)
        {
            try
            {
                var mixFormat = _recordingDevice!.AudioClient?.MixFormat ?? BuildWaveFormat(sampleRate, channels, bitDepth, isFloat);
                _wasapiCapture = new WasapiCaptureNative(_recordingDevice, AudioClientShareMode.Shared, mixFormat, bufferMilliseconds: 100);
                Debug.WriteLine($"Mode: Shared ({FormatWaveFormat(mixFormat)}).");
                return true;
            }
            catch (COMException ex) { OnError?.Invoke($"Nelze pøistoupit k mikrofonu – je obsazený jinou aplikací. COMException: {ex.Message}"); return false; }
        }
        #endregion

        #region WASAPI handlers
        private void SetupWasapiHandlersAndWriter(string micFilePath, int bitDepth, bool isFloat, string? refFilePath)
        {
            var src = _wasapiCapture!.WaveFormat;
            var monoFormat = BuildWaveFormat(src.SampleRate, 1, bitDepth, isFloat);
            _activeRecordingPath = micFilePath;
            _activeRefPath = (UseReferenceChannel && refFilePath != null) ? refFilePath : null;
            _recordingStoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_writerLock)
            {
                _writer = new WaveFileWriter(micFilePath, monoFormat);
                if (_activeRefPath != null) _refWriter = new WaveFileWriter(_activeRefPath, monoFormat);
            }

            _dataAvailableHandler = (s, a) => HandleDataAvailable(a, src, bitDepth, isFloat);
            _recordingStoppedHandler = (s, a) =>
            {
                try { DisposeWriters(); }
                finally
                {
                    try { _wasapiCapture?.Dispose(); } catch { }
                    _wasapiCapture = null;
                    IsRecording = false;
                    _recordingStoppedTcs?.TrySetResult(true);
                    try { OnRecordingStopped?.Invoke(micFilePath); } catch { }
                    if (a.Exception != null) OnError?.Invoke($"Chyba pøi nahrávání: {a.Exception.Message}");
                    RecordingStateChanged?.Invoke();
                }
                Debug.WriteLine("[AudioRecorder] RecordingStopped handler executed.");
            };

            _wasapiCapture.DataAvailable += _dataAvailableHandler;
            _wasapiCapture.RecordingStopped += _recordingStoppedHandler;
        }

        private void HandleDataAvailable(WaveInEventArgs a, WaveFormat src, int bitDepth, bool isFloat)
        {
            try
            {
                lock (_writerLock)
                {
                    if (_writer == null) return;
                    float[] samples = DecodeToFloat(a.Buffer, a.BytesRecorded, src);
                    if (samples.Length == 0) return;
                    int frameCount = samples.Length / src.Channels;
                    float[] micSamples = ExtractChannel(samples, src.Channels, InputSignalChannel, frameCount);
                    DetectClipping(micSamples);
                    if (isFloat) _writer.WriteSamples(micSamples, 0, micSamples.Length);
                    else WritePcmToWriter(_writer, micSamples, bitDepth);

                    if (_refWriter != null && UseReferenceChannel)
                    {
                        float[] refSamples = ExtractChannel(samples, src.Channels, InputReferenceChannel, frameCount);
                        if (isFloat) _refWriter.WriteSamples(refSamples, 0, refSamples.Length);
                        else WritePcmToWriter(_refWriter, refSamples, bitDepth);
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine("[AudioRecorder] DataAvailable error: " + ex.Message); }
        }

        private void DetectClipping(float[] samples)
        {
            if (_clipDetected) return;
            for (int i = 0; i < samples.Length; i++)
            {
                if (Math.Abs(samples[i]) > ClipThreshold)
                {
                    _clipDetected = true;
                    Debug.WriteLine($"[AudioRecorder] *** CLIPPING DETECTED *** value={samples[i]:F4}");
                    try { OnClipDetected?.Invoke(); } catch { }
                    break;
                }
            }
        }
        #endregion

        #region WASAPI exclusive fallback
        private void TryFallbackToSharedOnStartFailure(string micFilePath, Exception ex, string? refFilePath)
        {
            try
            {
                if (ex is ArgumentException && _recordingDevice != null && !string.IsNullOrWhiteSpace(_recordingDevice.ID))
                    _exclusiveStartBlackList.Add(_recordingDevice.ID);

                DetachWasapiHandlers();
                try { _wasapiCapture?.Dispose(); } catch { }
                _wasapiCapture = null;
                DisposeWriters();
                _exclusiveSupported = false;

                if (_recordingDevice == null) { IsRecording = false; _recordingStoppedTcs?.TrySetResult(true); OnError?.Invoke("Nen? vybr?no ??dn? nahr?vac? za??zen?."); return; }

                var mixFormat = _recordingDevice.AudioClient?.MixFormat;
                if (mixFormat == null)
                {
                    int sr = _lastRequested?.SampleRate ?? 48000;
                    var req = _lastFileFormatRequested ?? (16, false);
                    mixFormat = BuildWaveFormat(sr, GetDesiredInputChannels(), req.BitDepth, req.IsFloat);
                }

                _wasapiCapture = new WasapiCaptureNative(_recordingDevice, AudioClientShareMode.Shared, mixFormat, bufferMilliseconds: 100);

                var req2 = _lastFileFormatRequested ?? (16, false);
                int requestedSampleRate = _lastRequested?.SampleRate ?? mixFormat.SampleRate;
                string fallbackMessage = BuildWasapiFallbackMessage(requestedSampleRate, req2.BitDepth, req2.IsFloat, mixFormat);
                if (!ConfirmSettingsMismatch(fallbackMessage))
                {
                    OnError?.Invoke("Nahrávání bylo zrušeno, protože skuteèné nastavení neodpovídá vybranému.");
                    ReleaseCapture();
                    return;
                }

                SetupWasapiHandlersAndWriter(micFilePath, req2.BitDepth, req2.IsFloat, refFilePath);
                IsRecording = true;
                _wasapiCapture.StartRecording();
                OnError?.Invoke("Exclusive se nepoda?ilo spustit, pou?il jsem Shared.");
                OnRecordingStarted?.Invoke();
                RecordingStateChanged?.Invoke();
            }
            catch (Exception fallbackEx)
            {
                IsRecording = false; _recordingStoppedTcs?.TrySetResult(true);
                Debug.WriteLine("[AudioRecorder] Shared fallback also failed: " + fallbackEx.Message);
                OnError?.Invoke("Nahr?v?n? nelze spustit: " + fallbackEx.Message);
                ReleaseCapture();
            }
        }
        #endregion

        #region ASIO recording
        private void StartRecordingAsio(string micFilePath, int sampleRate, int bitDepth, bool isFloat,
            ISampleProvider? playbackProvider, string? refFilePath = null)
        {
            ReleaseCapture();
            if (string.IsNullOrWhiteSpace(AsioDriverName))
            {
                var available = string.Join(", ", GetAsioDriverNames());
                OnError?.Invoke(string.IsNullOrWhiteSpace(available) ? "Nen? vybr?n ASIO driver (a ??dn? ASIO drivery nebyly nalezeny)." : $"Nen? vybr?n ASIO driver. Dostupn?: {available}");
                return;
            }

            try
            {
                _asioRecorder = new AsioRecorder
                {
                    DriverName = AsioDriverName,
                    InputChannelIndex = AsioInputChannelIndex,
                    OutputChannelIndex = AsioOutputChannelIndex,
                    SignalChannelIndex = InputSignalChannel,
                    UseReferenceChannel = UseReferenceChannel,
                    ReferenceChannelIndex = InputReferenceChannel,
                    OnSettingsMismatchConfirmationRequested = OnSettingsMismatchConfirmationRequested
                };
                _asioRecorder.OnRecordingStarted += () => { IsRecording = true; OnRecordingStarted?.Invoke(); RecordingStateChanged?.Invoke(); };
                _asioRecorder.OnRecordingStopped += (path) => { IsRecording = false; _recordingStoppedTcs?.TrySetResult(true); OnRecordingStopped?.Invoke(path); RecordingStateChanged?.Invoke(); };
                _asioRecorder.OnError += (msg) => OnError?.Invoke(msg);
                _asioRecorder.OnClipDetected += () => OnClipDetected?.Invoke();
                _recordingStoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                string? activeRef = (UseReferenceChannel && refFilePath != null) ? refFilePath : null;
                _asioRecorder.StartRecording(micFilePath, sampleRate, bitDepth, isFloat, playbackProvider, refFilePath: activeRef);
            }
            catch (Exception ex) { Debug.WriteLine($"[AudioRecorder] ASIO StartRecording failed: {ex.Message}"); OnError?.Invoke($"ASIO nahr?v?n? nelze spustit: {ex.Message}"); ReleaseCapture(); }
        }
        #endregion

        #region Lifecycle
        public void ReleaseCapture()
        {
            try
            {
                Debug.WriteLine("[AudioRecorder] ReleaseCapture called");
                try { _asioRecorder?.StopRecording(); } catch { }
                try { _asioRecorder?.Dispose(); } catch { }
                _asioRecorder = null;
                DetachWasapiHandlers();
                if (IsRecording) { try { _wasapiCapture?.StopRecording(); } catch { } }
                try { _wasapiCapture?.Dispose(); } catch { }
                _wasapiCapture = null;
                DisposeWriters();
                IsRecording = false;
                _recordingStoppedTcs?.TrySetResult(true);
                _recordingStoppedTcs = null;
                _activeRecordingPath = null; _activeRefPath = null; _lastFileFormatRequested = null;
                _dataAvailableHandler = null; _recordingStoppedHandler = null;
            }
            catch (Exception ex) { Debug.WriteLine("ReleaseCapture error: " + ex.Message); }
            finally { RecordingStateChanged?.Invoke(); }
        }

        public void Dispose() => ReleaseCapture();

        private void DetachWasapiHandlers()
        {
            try
            {
                if (_wasapiCapture != null)
                {
                    if (_dataAvailableHandler != null) _wasapiCapture.DataAvailable -= _dataAvailableHandler;
                    if (_recordingStoppedHandler != null) _wasapiCapture.RecordingStopped -= _recordingStoppedHandler;
                }
            }
            catch { }
        }

        private void DisposeWriters()
        {
            lock (_writerLock)
            {
                try { _writer?.Flush(); } catch { } try { _writer?.Dispose(); } catch { } _writer = null;
                try { _refWriter?.Flush(); } catch { } try { _refWriter?.Dispose(); } catch { } _refWriter = null;
            }
        }

        private void ForceCleanupAfterTimeout(string? path)
        {
            try
            {
                var cap = _wasapiCapture; _wasapiCapture = null;
                DetachWasapiHandlers();
                try { cap?.Dispose(); } catch { }
                DisposeWriters();
                IsRecording = false; _recordingStoppedTcs?.TrySetResult(true);
                if (!string.IsNullOrWhiteSpace(path)) { try { OnRecordingStopped?.Invoke(path); } catch { } }
            }
            finally { RecordingStateChanged?.Invoke(); }
        }
        #endregion

        #region Decoding and channel extraction
        private static float[] DecodeToFloat(byte[] buffer, int bytesRecorded, WaveFormat src)
        {
            int bytesPerSample = src.BitsPerSample / 8;
            if (bytesPerSample <= 0) return Array.Empty<float>();
            int frameSize = bytesPerSample * src.Channels;
            if (frameSize <= 0) return Array.Empty<float>();
            int frameCount = bytesRecorded / frameSize;
            if (frameCount <= 0) return Array.Empty<float>();

            float[] samples = new float[frameCount * src.Channels];
            bool isFloat = src.Encoding == WaveFormatEncoding.IeeeFloat;
            bool isPcm = src.Encoding == WaveFormatEncoding.Pcm;

            if (!isFloat && !isPcm)
            {
                if (src.Encoding == WaveFormatEncoding.Extensible) { isFloat = bytesPerSample == 4; isPcm = !isFloat; }
                else { Debug.WriteLine($"[AudioRecorder] DecodeToFloat: unsupported encoding {src.Encoding}"); return Array.Empty<float>(); }
            }

            if (isFloat && bytesPerSample == 4) { Buffer.BlockCopy(buffer, 0, samples, 0, frameCount * src.Channels * sizeof(float)); return samples; }
            if (!isPcm) return Array.Empty<float>();

            int idx = 0;
            if (bytesPerSample == 2)
            {
                for (int i = 0; i < frameCount; i++) { int b = i * frameSize; for (int ch = 0; ch < src.Channels; ch++) { int o = b + ch * 2; samples[idx++] = (short)(buffer[o] | (buffer[o + 1] << 8)) / 32768f; } }
            }
            else if (bytesPerSample == 3)
            {
                for (int i = 0; i < frameCount; i++) { int b = i * frameSize; for (int ch = 0; ch < src.Channels; ch++) { int o = b + ch * 3; int v = buffer[o] | (buffer[o + 1] << 8) | (buffer[o + 2] << 16); if ((v & 0x0080_0000) != 0) v |= unchecked((int)0xFF00_0000); samples[idx++] = v / 8388608f; } }
            }
            else if (bytesPerSample == 4)
            {
                for (int i = 0; i < frameCount; i++) { int b = i * frameSize; for (int ch = 0; ch < src.Channels; ch++) { int o = b + ch * 4; samples[idx++] = (float)(BitConverter.ToInt32(buffer, o) / 2147483648.0); } }
            }
            return samples;
        }

        private static float[] ExtractChannel(float[] interleaved, int totalChannels, int channelIndex, int frameCount)
        {
            if (totalChannels == 1) return interleaved;
            int ch = Math.Clamp(channelIndex, 0, totalChannels - 1);
            var result = new float[frameCount];
            for (int i = 0; i < frameCount; i++) result[i] = interleaved[i * totalChannels + ch];
            return result;
        }
        #endregion

        #region Static file readers
        public static float[] ReadWavAsFloatMono(string path, out int sampleRate)
        {
            using var reader = new AudioFileReader(path);
            sampleRate = reader.WaveFormat.SampleRate;
            int channels = reader.WaveFormat.Channels;
            var samples = new List<float>();
            float[] buffer = new float[reader.WaveFormat.SampleRate];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                for (int i = 0; i < read; i += channels)
                { float sum = 0; int cnt = Math.Min(channels, read - i); for (int c = 0; c < cnt; c++) sum += buffer[i + c]; samples.Add(sum / cnt); }
            return samples.ToArray();
        }

        public static float[] ReadWavChannelAsFloat(string path, int channelIndex, out int sampleRate)
        {
            using var reader = new AudioFileReader(path);
            sampleRate = reader.WaveFormat.SampleRate;
            int channels = reader.WaveFormat.Channels;
            if (channelIndex < 0 || channelIndex >= channels)
                throw new ArgumentOutOfRangeException(nameof(channelIndex), $"Channel index {channelIndex} is out of range. File has {channels} channels.");
            var samples = new List<float>();
            float[] buffer = new float[reader.WaveFormat.SampleRate];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                for (int i = channelIndex; i < read; i += channels) samples.Add(buffer[i]);
            Debug.WriteLine($"[AudioRecorder] Read channel {channelIndex} from {path}: {samples.Count} samples at {sampleRate}Hz");
            return samples.ToArray();
        }
        #endregion

        #region WaveFormat helpers
        private static WaveFormat BuildWaveFormat(int sampleRate, int channels, int bitDepth, bool isFloat)
        {
            if (isFloat) return WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            bitDepth = bitDepth switch { 16 => 16, 24 => 24, 32 => 32, _ => 16 };
            return new WaveFormat(sampleRate, bitDepth, channels);
        }

        private WaveFormat BuildWaveFormatForDevice(int sampleRate, int channels, int bitDepth, bool isFloat)
        {
            var mix = _recordingDevice?.AudioClient?.MixFormat;
            if (!isFloat && mix is WaveFormatExtensible)
            { bitDepth = bitDepth switch { 16 => 16, 24 => 24, 32 => 32, _ => 16 }; return new WaveFormatExtensible(sampleRate, bitDepth, channels); }
            return BuildWaveFormat(sampleRate, channels, bitDepth, isFloat);
        }

        private int GetDesiredInputChannels()
        {
            if (CaptureChannelOverride is 1 or 2) return CaptureChannelOverride;
            if (Backend == InputBackend.Asio) return 2;
            return _recordingDevice?.AudioClient?.MixFormat?.Channels ?? 1;
        }
        #endregion

        #region Utility
        private static void WritePcmToWriter(WaveFileWriter writer, float[] samples, int bitDepth) => PcmWriterHelper.WritePcmSamples(writer, samples, bitDepth);

        private bool ConfirmWasapiSettingsMismatch(int requestedSampleRate, int requestedBitDepth, bool requestedIsFloat)
        {
            if (_wasapiCapture == null)
                return true;

            WaveFormat actual = _wasapiCapture.WaveFormat;
            bool actualIsFloat = IsFloatWaveFormat(actual);
            int normalizedRequestedBits = requestedBitDepth is 16 or 24 or 32 ? requestedBitDepth : 16;
            bool sampleRateMismatch = requestedSampleRate != actual.SampleRate;
            bool formatMismatch = requestedIsFloat != actualIsFloat || (!requestedIsFloat && normalizedRequestedBits != actual.BitsPerSample);

            if (!sampleRateMismatch && !formatMismatch)
                return true;

            string requestedFormat = requestedIsFloat
                ? $"{requestedSampleRate} Hz, Float32"
                : $"{requestedSampleRate} Hz, PCM {normalizedRequestedBits}-bit";

            string actualFormat = actualIsFloat
                ? $"{actual.SampleRate} Hz, Float32"
                : $"{actual.SampleRate} Hz, PCM {actual.BitsPerSample}-bit";

            string mode = _wasapiCapture.ShareMode == AudioClientShareMode.Exclusive ? "WASAPI Exclusive" : "WASAPI Shared";

            string message =
                $"Vybrané WASAPI nastavení se neshoduje se skuteèným vstupním formátem.\n\n" +
                $"Vybráno: {requestedFormat}\n" +
                $"Vstup zaøízení ({mode}): {actualFormat}\n\n" +
                "Chcete i pøesto pokraèovat?";

            return ConfirmSettingsMismatch(message);
        }

        private bool ConfirmSettingsMismatch(string message)
        {
            return OnSettingsMismatchConfirmationRequested?.Invoke(message) ?? true;
        }

        private static bool IsFloatWaveFormat(WaveFormat waveFormat)
        {
            if (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                return true;

            if (waveFormat.Encoding == WaveFormatEncoding.Extensible)
                return waveFormat.BitsPerSample == 32;

            return false;
        }

        private static string BuildWasapiFallbackMessage(int requestedSampleRate, int requestedBitDepth, bool requestedIsFloat, WaveFormat fallbackWaveFormat)
        {
            int normalizedRequestedBits = requestedBitDepth is 16 or 24 or 32 ? requestedBitDepth : 16;
            bool fallbackIsFloat = IsFloatWaveFormat(fallbackWaveFormat);

            string requestedFormat = requestedIsFloat
                ? $"{requestedSampleRate} Hz, Float32"
                : $"{requestedSampleRate} Hz, PCM {normalizedRequestedBits}-bit";

            string fallbackFormat = fallbackIsFloat
                ? $"{fallbackWaveFormat.SampleRate} Hz, Float32"
                : $"{fallbackWaveFormat.SampleRate} Hz, PCM {fallbackWaveFormat.BitsPerSample}-bit";

            return
                "Vybraný režim nebylo možné spustit v WASAPI Exclusive.\n\n" +
                $"Vybráno: {requestedFormat}\n" +
                $"Aplikace se pøepne na WASAPI Shared ({fallbackFormat}).\n\n" +
                "Chcete i pøesto pokraèovat?";
        }

        private static string FormatWaveFormat(WaveFormat? wf)
        {
            if (wf == null) return "<null>";
            return wf.Encoding == WaveFormatEncoding.IeeeFloat
                ? $"{wf.SampleRate}Hz {wf.Channels}ch Float32"
                : $"{wf.SampleRate}Hz {wf.Channels}ch PCM{wf.BitsPerSample}";
        }
        #endregion
    }
}
