using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeaSound
{
    public partial class MicCalibrationWindow : Window
    {
        private const float CalibrationFrequency = 1000f;
        private const int UpdateIntervalMs = 80;
        private const double FadeInSeconds = 1.5;
        private const double FadeOutSeconds = 1.5;
        private const double LevelStep = 0.5;
        private const double LevelMin = -40.0;
        private const double LevelMax = 0.0;
        private const int PreSilenceMs = 80;

        private readonly bool _isAsio;
        private readonly MMDevice? _outputDevice;
        private readonly MMDevice? _inputDevice;
        private readonly string? _asioDriverName;
        private readonly int _inputSignalChannel;
        private readonly bool _useReferenceChannel;
        private readonly int _referenceChannel;
        private readonly int _sampleRate;

        private WasapiOut? _wasapiOut;
        private WasapiCapture? _wasapiCapture;
        private AsioOut? _asioOut;
        private CapturingSineProvider? _sineProvider;

        private volatile float _inputRms = float.NaN;
        private volatile float _referenceRms = float.NaN;

        private DispatcherTimer _uiTimer;
        private bool _isRunning;

        private double _levelDb = Preferences.Load().CalibrationGainDb;

        public MicCalibrationWindow(MMDevice? outputDevice, MMDevice? inputDevice, int inputSignalChannel, bool useReferenceChannel, int referenceChannel, int sampleRate)
        {
            InitializeComponent();
            _isAsio = false; _outputDevice = outputDevice; _inputDevice = inputDevice;
            _inputSignalChannel = inputSignalChannel; _useReferenceChannel = useReferenceChannel; _referenceChannel = referenceChannel;
            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            TxtOutputDevice.Text = outputDevice?.FriendlyName ?? "(zadne)";
            TxtInputDevice.Text = inputDevice?.FriendlyName ?? "(zadne)";
            InitReferenceVisibility(); InitUiTimer();
            ApplyLevelDb(_levelDb);
        }

        public MicCalibrationWindow(string asioDriverName, int inputSignalChannel, bool useReferenceChannel, int referenceChannel, int sampleRate)
        {
            InitializeComponent();
            _isAsio = true; _asioDriverName = asioDriverName;
            _inputSignalChannel = inputSignalChannel; _useReferenceChannel = useReferenceChannel; _referenceChannel = referenceChannel;
            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            TxtOutputDevice.Text = $"ASIO: {asioDriverName}";
            TxtInputDevice.Text = $"ASIO: {asioDriverName} (In {inputSignalChannel + 1})";
            InitReferenceVisibility(); InitUiTimer();
            ApplyLevelDb(_levelDb);
        }

        private void InitReferenceVisibility()
        {
            if (GroupBoxReference != null) GroupBoxReference.Visibility = _useReferenceChannel ? Visibility.Visible : Visibility.Collapsed;
            if (_useReferenceChannel && TxtReferenceDevice != null)
                TxtReferenceDevice.Text = _isAsio ? $"ASIO: {_asioDriverName} (In {_referenceChannel + 1})" : (_inputDevice?.FriendlyName ?? "(zadne)") + $" – Input {_referenceChannel + 1}";
        }

        private void InitUiTimer() { _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(UpdateIntervalMs) }; _uiTimer.Tick += UiTimer_Tick; }

        /// <summary>Applies <paramref name="db"/> (clamped), updates UI label and provider gain.</summary>
        private void ApplyLevelDb(double db)
        {
            _levelDb = Math.Clamp(db, LevelMin, LevelMax);
            if (TxtLevelDb != null) TxtLevelDb.Text = $"{_levelDb:F1} dB";
            if (_sineProvider != null)
                _sineProvider.TargetGainLinear = (float)Math.Pow(10.0, _levelDb / 20.0);
        }

        private void BtnLevelUp_Click(object sender, RoutedEventArgs e) => ApplyLevelDb(_levelDb + LevelStep);
        private void BtnLevelDown_Click(object sender, RoutedEventArgs e) => ApplyLevelDb(_levelDb - LevelStep);

        private void TxtLevelDb_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up)   { ApplyLevelDb(_levelDb + LevelStep); e.Handled = true; }
            if (e.Key == Key.Down) { ApplyLevelDb(_levelDb - LevelStep); e.Handled = true; }
            if (e.Key == Key.Enter || e.Key == Key.Return) CommitLevelTextBox();
        }

        private void TxtLevelDb_LostFocus(object sender, RoutedEventArgs e) => CommitLevelTextBox();

        private void TxtLevelDb_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ApplyLevelDb(_levelDb + (e.Delta > 0 ? LevelStep : -LevelStep));
            e.Handled = true;
        }

        private void CommitLevelTextBox()
        {
            if (TxtLevelDb == null) return;
            // Strip trailing " dB" if present, try to parse the number
            string raw = TxtLevelDb.Text.Replace("dB", "").Replace("DB", "").Trim();
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                || double.TryParse(raw, out parsed))
            {
                ApplyLevelDb(parsed);
            }
            else
            {
                // Restore last valid value
                TxtLevelDb.Text = $"{_levelDb:F1} dB";
            }
        }

        private void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) StopCalibration(); else _ = StartCalibrationAsync();
        }

        private async Task StartCalibrationAsync()
        {
            BtnStartStop.IsEnabled = false;
            try
            {
                if (_isAsio) StartAsio(); else StartWasapi();

                await Task.Delay(400 + PreSilenceMs);

                _sineProvider?.StartFadeIn(FadeInSeconds);
                _isRunning = true;
                _uiTimer.Start();
                BtnStartStop.Content = "\u23F9  Zastavit";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MicCalibration] Start failed: {ex.Message}");
                FinishStop();
                MessageBox.Show($"Nelze spustit kalibraci: {ex.Message}", "Chyba", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnStartStop.IsEnabled = true;
            }
        }

        private void StopCalibration()
        {
            if (_isRunning && _sineProvider != null)
            {
                _uiTimer.Stop();
                _isRunning = false;
                BtnStartStop.IsEnabled = false;

                var provider = _sineProvider;
                Task.Run(async () =>
                {
                    // Pre-silence before fade-out so the engine outputs true zeros first
                    await Task.Delay(PreSilenceMs);
                    await provider.FadeOutAsync(FadeOutSeconds);
                    Dispatcher.Invoke(FinishStop);
                });
            }
            else
            {
                FinishStop();
            }
        }

        private void FinishStop()
        {
            // Persist the calibrated level so all playback paths pick it up
            var prefs = Preferences.Load();
            prefs.CalibrationGainDb = _levelDb;
            prefs.Save();

            _inputRms = float.NaN; _referenceRms = float.NaN;
            try { _wasapiOut?.Stop(); } catch { } try { _wasapiOut?.Dispose(); } catch { } _wasapiOut = null;
            try { _wasapiCapture?.StopRecording(); } catch { } try { _wasapiCapture?.Dispose(); } catch { } _wasapiCapture = null;
            if (_asioOut != null) { try { _asioOut.AudioAvailable -= OnAsioAudioAvailable; } catch { } try { _asioOut.Stop(); } catch { } try { _asioOut.Dispose(); } catch { } _asioOut = null; }
            _sineProvider = null;
            BtnStartStop.Content = "\u25BA  Spustit 1 kHz";
            BtnStartStop.IsEnabled = true;
        }

        private void StartWasapi()
        {
            if (_outputDevice == null) throw new InvalidOperationException("Vystupni zarizeni neni pripojene.");
            if (_inputDevice == null) throw new InvalidOperationException("Vstupni zarizeni (mikrofon) neni pripojene.");
            int deviceSr = _outputDevice.AudioClient?.MixFormat?.SampleRate ?? _sampleRate;
            int deviceCh = _outputDevice.AudioClient?.MixFormat?.Channels ?? 1;
            float initialGain = (float)Math.Pow(10.0, _levelDb / 20.0);
            _sineProvider = new CapturingSineProvider(CalibrationFrequency, deviceSr, deviceCh, initialGain);
            _wasapiOut = new WasapiOut(_outputDevice, AudioClientShareMode.Shared, false, 100);
            _wasapiOut.Init(_sineProvider);
            _wasapiOut.Play();
            _wasapiCapture = new WasapiCapture(_inputDevice, false, 50);
            _wasapiCapture.DataAvailable += (_, args) =>
            {
                var wf = _wasapiCapture.WaveFormat;
                _inputRms = ComputeRmsFromBuffer(args.Buffer, args.BytesRecorded, wf, _inputSignalChannel);
                if (_useReferenceChannel) _referenceRms = ComputeRmsFromBuffer(args.Buffer, args.BytesRecorded, wf, _referenceChannel);
            };
            _wasapiCapture.RecordingStopped += (_, args) => { if (args.Exception != null) Debug.WriteLine($"[MicCalibration] WASAPI capture stopped: {args.Exception.Message}"); };
            _wasapiCapture.StartRecording();
        }

        private void StartAsio()
        {
            if (string.IsNullOrWhiteSpace(_asioDriverName)) throw new InvalidOperationException("ASIO driver neni vybran.");
            _asioOut = new AsioOut(_asioDriverName);
            float initialGain = (float)Math.Pow(10.0, _levelDb / 20.0);
            _sineProvider = new CapturingSineProvider(CalibrationFrequency, _sampleRate, 1, initialGain);
            ISampleProvider playbackFinal = _asioOut.DriverOutputChannelCount <= 1 ? (ISampleProvider)_sineProvider : new MonoToMultiChannelProvider(_sineProvider, _asioOut.DriverOutputChannelCount);
            _asioOut.InitRecordAndPlayback(new SampleProviderToWaveProvider(playbackFinal), _asioOut.DriverInputChannelCount, _sampleRate);
            _asioOut.ChannelOffset = 0; _asioOut.InputChannelOffset = 0;
            _asioOut.AudioAvailable += OnAsioAudioAvailable;
            _asioOut.Play();
        }

        private void OnAsioAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
        {
            try
            {
                int total = e.InputBuffers.Length;
                if (total <= 0) return;
                float[] interleaved = e.GetAsInterleavedSamples();
                if (interleaved == null || interleaved.Length == 0) return;
                int fc = e.SamplesPerBuffer;
                _inputRms = ComputeRmsFromInterleaved(interleaved, total, _inputSignalChannel, fc);
                if (_useReferenceChannel) _referenceRms = ComputeRmsFromInterleaved(interleaved, total, _referenceChannel, fc);
            }
            catch (Exception ex) { Debug.WriteLine($"[MicCalibration] ASIO AudioAvailable error: {ex.Message}"); }
        }

        private static float ComputeRmsFromInterleaved(float[] interleaved, int totalChannels, int channelIndex, int frameCount)
        {
            int ch = Math.Clamp(channelIndex, 0, totalChannels - 1);
            double sumSq = 0.0;
            for (int i = 0; i < frameCount; i++) sumSq += interleaved[i * totalChannels + ch] * (double)interleaved[i * totalChannels + ch];
            return frameCount > 0 ? (float)Math.Sqrt(sumSq / frameCount) : 0f;
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            float outRms = _sineProvider?.LastRms ?? float.NaN;
            float inRms = _inputRms, refRms = _referenceRms;
            UpdateLevelDisplay(TxtOutputLevel, PbOutput, outRms, "#FF2196F3");
            UpdateLevelDisplay(TxtInputLevel, PbInput, inRms, "#FF4CAF50");
            if (_useReferenceChannel) UpdateLevelDisplay(TxtReferenceLevel, PbReference, refRms, "#FFFF9800");

            bool hasOut = !float.IsNaN(outRms) && outRms > 0;
            bool hasIn  = !float.IsNaN(inRms)  && inRms  > 0;
            bool hasRef = _useReferenceChannel && !float.IsNaN(refRms) && refRms > 0;

            if (hasOut && (hasIn || hasRef))
            {
                double outDb = 20.0 * Math.Log10(outRms);
                double? deltaIn  = hasIn  ? outDb - 20.0 * Math.Log10(inRms)  : (double?)null;
                double? deltaRef = hasRef ? outDb - 20.0 * Math.Log10(refRms) : (double?)null;

                double delta;
                string deltaSource;
                if (deltaIn.HasValue && deltaRef.HasValue)
                {
                    if (Math.Abs(deltaIn.Value) >= Math.Abs(deltaRef.Value))
                    { delta = deltaIn.Value;  deltaSource = "Output \u2013 Mikrofon"; }
                    else
                    { delta = deltaRef.Value; deltaSource = "Output \u2013 Reference"; }
                }
                else if (deltaRef.HasValue)
                { delta = deltaRef.Value; deltaSource = "Output \u2013 Reference"; }
                else
                { delta = deltaIn!.Value;  deltaSource = "Output \u2013 Mikrofon"; }

                if (TxtDeltaHeader != null) TxtDeltaHeader.Text = deltaSource;
                TxtDelta.Text = $"{delta:+0.0;-0.0;0.0} dB";
                TxtDelta.Foreground = Math.Abs(delta) <= 1.0
                    ? new SolidColorBrush(Colors.Green)
                    : Math.Abs(delta) <= 3.0
                        ? new SolidColorBrush(Colors.DarkOrange)
                        : new SolidColorBrush(Colors.Red);
                TxtHint.Text = BuildHint(delta);
            }
            else
            {
                if (TxtDeltaHeader != null) TxtDeltaHeader.Text = hasRef ? "Output \u2013 Reference" : "Output \u2013 Mikrofon";
                TxtDelta.Text = "\u2013  dB";
                TxtHint.Text = "Cekam na signal...";
            }
        }

        private static void UpdateLevelDisplay(TextBlock? label, System.Windows.Controls.ProgressBar? bar, float rms, string hexColor)
        {
            if (label == null || bar == null) return;
            if (float.IsNaN(rms) || rms <= 0) { label.Text = "–  dBFS"; bar.Value = -60; return; }
            double db = Math.Max(-60.0, 20.0 * Math.Log10(rms));
            label.Text = $"{db:F1} dBFS"; bar.Value = db;
            var color = db >= -3 ? Colors.Red : db >= -12 ? Colors.YellowGreen : (Color)ColorConverter.ConvertFromString(hexColor);
            bar.Foreground = new SolidColorBrush(color);
        }

        private static string BuildHint(double delta)
        {
            double abs = Math.Abs(delta);
            if (abs <= 1.0) return "OK - Urovne jsou vyrovnany (rozdil <= 1 dB).";
            return delta > 0 ? $"Output je o {abs:F1} dB hlasitejsi – snizujte uroven nebo zesilen\u00ED mikrofonu." : $"Vstup je o {abs:F1} dB hlasitejsi – zvysujte uroven nebo snizujte zesilen\u00ED mikrofonu.";
        }

        private static float ComputeRmsFromBuffer(byte[] buffer, int bytesRecorded, WaveFormat wf, int channelIndex)
        {
            int bytesPerSample = wf.BitsPerSample / 8;
            if (bytesPerSample <= 0) return 0f;
            int frameSize = bytesPerSample * wf.Channels, frameCount = bytesRecorded / frameSize;
            if (frameCount <= 0) return 0f;
            int ch = Math.Clamp(channelIndex, 0, wf.Channels - 1);
            bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat || (wf.Encoding == WaveFormatEncoding.Extensible && bytesPerSample == 4);
            double sumSq = 0.0;
            for (int i = 0; i < frameCount; i++)
            {
                int baseOffset = i * frameSize + ch * bytesPerSample;
                float s = isFloat ? BitConverter.ToSingle(buffer, baseOffset) : ReadPcmSample(buffer, baseOffset, bytesPerSample);
                sumSq += s * (double)s;
            }
            return (float)Math.Sqrt(sumSq / frameCount);
        }

        private static float ReadPcmSample(byte[] buffer, int offset, int bytesPerSample) =>
            bytesPerSample switch
            {
                2 => (short)(buffer[offset] | (buffer[offset + 1] << 8)) / 32768f,
                3 => ((int)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16)) << 8 >> 8) / 8388608f,
                4 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
                _ => 0f
            };

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) => StopCalibration();
    }

    internal sealed class CapturingSineProvider : ISampleProvider
    {
        private readonly int _channels;
        private double _phase;
        private readonly double _phaseIncrement;
        private volatile float _lastRms;

        private enum FadeState { Idle, WaitZeroCrossFadeIn, FadeIn, Steady, WaitZeroCrossFadeOut, FadeOut }
        private FadeState _fadeState = FadeState.Idle;
        private double _fadeSamples;
        private double _fadeCurrent;
        private TaskCompletionSource<bool>? _fadeOutTcs;
        private readonly object _fadeLock = new();

        private float _currentGain;
        private float _targetGain;
        private float _gainCoeff;   // per-sample decay coefficient

        public float TargetGainLinear
        {
            get => _targetGain;
            set => _targetGain = Math.Max(0f, value);
        }

        // Keep legacy volatile field so callers that set GainLinear directly still work
        public float GainLinear
        {
            get => _targetGain;
            set => _targetGain = Math.Max(0f, value);
        }

        public WaveFormat WaveFormat { get; }
        public float LastRms => _lastRms;

        public CapturingSineProvider(float frequency, int sampleRate, int channels, float gainLinear = 1.0f)
        {
            _channels = channels;
            _phaseIncrement = 2.0 * Math.PI * frequency / sampleRate;
            _targetGain = _currentGain = Math.Max(0f, gainLinear);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

            // Time constant ~50 ms: coeff = exp(-1 / (sr * ?))
            const double tauSeconds = 0.05;
            _gainCoeff = (float)Math.Exp(-1.0 / (sampleRate * tauSeconds));
        }

        private static float RaisedCosineEnvelope(double t) => (float)(0.5 * (1.0 - Math.Cos(t * Math.PI)));

        public void StartFadeIn(double seconds)
        {
            lock (_fadeLock)
            {
                _fadeSamples = Math.Max(1, seconds * WaveFormat.SampleRate);
                _fadeCurrent = 0;
                _fadeState = FadeState.WaitZeroCrossFadeIn;
            }
        }

        public Task FadeOutAsync(double seconds)
        {
            lock (_fadeLock)
            {
                _fadeOutTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _fadeSamples = Math.Max(1, seconds * WaveFormat.SampleRate);
                _fadeCurrent = 0;
                _fadeState = FadeState.WaitZeroCrossFadeOut;
            }
            return _fadeOutTcs.Task;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / _channels;
            double sumSq = 0.0;

            for (int i = 0; i < frames; i++)
            {
                // Smooth gain: single-pole LP filter per sample
                _currentGain = _gainCoeff * _currentGain + (1f - _gainCoeff) * _targetGain;

                float envelope;
                lock (_fadeLock)
                {
                    double nextPhase = _phase + _phaseIncrement;
                    bool zeroCross = nextPhase >= 2.0 * Math.PI;

                    switch (_fadeState)
                    {
                        case FadeState.WaitZeroCrossFadeIn:
                            envelope = 0f;
                            if (zeroCross) _fadeState = FadeState.FadeIn;
                            break;

                        case FadeState.FadeIn:
                        {
                            double t = _fadeCurrent / _fadeSamples;
                            envelope = RaisedCosineEnvelope(t);
                            _fadeCurrent++;
                            if (_fadeCurrent >= _fadeSamples) _fadeState = FadeState.Steady;
                            break;
                        }

                        case FadeState.WaitZeroCrossFadeOut:
                            envelope = 1f;
                            if (zeroCross) _fadeState = FadeState.FadeOut;
                            break;

                        case FadeState.FadeOut:
                        {
                            double t = _fadeCurrent / _fadeSamples;
                            envelope = RaisedCosineEnvelope(1.0 - t);
                            _fadeCurrent++;
                            if (_fadeCurrent >= _fadeSamples)
                            {
                                _fadeState = FadeState.Idle;
                                _fadeOutTcs?.TrySetResult(true);
                                _fadeOutTcs = null;
                            }
                            break;
                        }

                        case FadeState.Idle:
                            envelope = 0f;
                            break;

                        default: // Steady
                            envelope = 1f;
                            break;
                    }
                }

                float sample = (float)Math.Sin(_phase) * _currentGain * envelope;
                sumSq += sample * (double)sample;
                _phase += _phaseIncrement;
                if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;
                for (int ch = 0; ch < _channels; ch++)
                    buffer[offset + i * _channels + ch] = sample;
            }

            _lastRms = frames > 0 ? (float)Math.Sqrt(sumSq / frames) : 0f;
            return count;
        }
    }
}
