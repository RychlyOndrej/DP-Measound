using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeaSound
{
    internal sealed class WasapiCaptureNative : IDisposable
    {
        private readonly MMDevice _device;
        private readonly AudioClientShareMode _shareMode;
        private readonly WaveFormat _waveFormat;
        private readonly int _bufferMilliseconds;

        private NAudio.CoreAudioApi.AudioClient? _audioClient;
        private NAudio.CoreAudioApi.AudioCaptureClient? _captureClient;
        private IntPtr _eventHandle;
        private Task? _captureTask;
        private CancellationTokenSource? _captureCts;
        private bool _isDisposed;

        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public WaveFormat WaveFormat => _waveFormat;
        public AudioClientShareMode ShareMode => _shareMode;

        public WasapiCaptureNative(MMDevice device, AudioClientShareMode shareMode, WaveFormat waveFormat, int bufferMilliseconds = 100)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _shareMode = shareMode;
            _waveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
            _bufferMilliseconds = bufferMilliseconds;
        }

        public void StartRecording()
        {
            if (_captureTask != null) throw new InvalidOperationException("Already recording");
            InitializeAudioClient();
            _captureCts = new CancellationTokenSource();
            _captureTask = Task.Run(() => CaptureLoop(_captureCts.Token), _captureCts.Token);
        }

        public void StopRecording() => _captureCts?.Cancel();

        private void InitializeAudioClient()
        {
            _audioClient = _device.AudioClient;
            long bufferDuration = _bufferMilliseconds * 10000L;
            long periodicity = _shareMode == AudioClientShareMode.Exclusive ? bufferDuration : 0;

            _eventHandle = CreateEventEx(IntPtr.Zero, IntPtr.Zero, 0, EventAccess.EVENT_ALL_ACCESS);
            if (_eventHandle == IntPtr.Zero) throw new InvalidOperationException("Failed to create event handle");

            try
            {
                _audioClient.Initialize(_shareMode, NAudio.CoreAudioApi.AudioClientStreamFlags.EventCallback,
                    bufferDuration, periodicity, _waveFormat, Guid.Empty);
                Debug.WriteLine($"[WasapiCaptureNative] Initialized: {_shareMode} mode, {_waveFormat.SampleRate}Hz, {_waveFormat.BitsPerSample}bit, {_waveFormat.Channels}ch");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WasapiCaptureNative] Initialize failed: 0x{Marshal.GetHRForException(ex):X8} - {ex.Message}");
                if (_eventHandle != IntPtr.Zero) { CloseHandle(_eventHandle); _eventHandle = IntPtr.Zero; }
                throw;
            }

            _captureClient = _audioClient.AudioCaptureClient;
            try { _audioClient.SetEventHandle(_eventHandle); }
            catch (Exception ex) { Debug.WriteLine($"[WasapiCaptureNative] SetEventHandle failed: {ex.Message}"); throw; }
        }

        private void CaptureLoop(CancellationToken cancellationToken)
        {
            Exception? capturedException = null;
            try
            {
                _audioClient!.Start();
                byte[] buffer = new byte[_waveFormat.AverageBytesPerSecond];

                while (!cancellationToken.IsCancellationRequested)
                {
                    int waitResult = WaitForSingleObject(_eventHandle, 2000);
                    if (waitResult != 0) { if (cancellationToken.IsCancellationRequested) break; continue; }

                    int packetLength = _captureClient!.GetNextPacketSize();
                    while (packetLength > 0)
                    {
                        var dataPointer = _captureClient.GetBuffer(out int numFramesAvailable, out _);
                        int bytesToRead = numFramesAvailable * _waveFormat.BlockAlign;
                        if (buffer.Length < bytesToRead) buffer = new byte[bytesToRead];
                        Marshal.Copy(dataPointer, buffer, 0, bytesToRead);
                        _captureClient.ReleaseBuffer(numFramesAvailable);
                        if (bytesToRead > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytesToRead));
                        packetLength = _captureClient.GetNextPacketSize();
                    }
                }
            }
            catch (Exception ex) { capturedException = ex; Debug.WriteLine($"[WasapiCaptureNative] Capture loop exception: {ex.Message}"); }
            finally
            {
                try { _audioClient?.Stop(); } catch { }
                RecordingStopped?.Invoke(this, new StoppedEventArgs(capturedException));
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try { StopRecording(); _captureTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _captureCts?.Dispose();
            if (_eventHandle != IntPtr.Zero) { CloseHandle(_eventHandle); _eventHandle = IntPtr.Zero; }
            _captureClient = null;
            _audioClient?.Dispose();
            _audioClient = null;
        }

        #region Win32 Interop
        [Flags] private enum EventAccess : uint { EVENT_ALL_ACCESS = 0x1F0003 }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateEventEx(IntPtr lpEventAttributes, IntPtr lpName, uint dwFlags, EventAccess dwDesiredAccess);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern int WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);
        #endregion
    }
}
