using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace MeaSound
{
    public partial class MainWindow
    {
        #region Input device button states

        private void RefreshButtonStatesInput()
        {
            bool micSelected = selectedMicrophoneInfo != null;
            bool isRecording = recorder?.IsRecording ?? false;

            BtnConnectAudio.IsEnabled = micSelected && !isRecording && !isMicConnected;
            BtnDisconnectAudio.IsEnabled = isMicConnected && !isRecording;
            BtnRefreshAudio.IsEnabled = !isRecording && !isMicConnected;
            ComboBoxSoundCard.IsEnabled = !isRecording && !isMicConnected;
        }

        #endregion

        #region Input device selection and connection

        private void ComboBoxSoundCard_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedMicrophoneInfo = ComboBoxSoundCard.SelectedItem as AudioDeviceInfo;
            RefreshButtonStatesInput();

            if (selectedMicrophoneInfo != null)
            {
                int channels = 2;
                try
                {
                    var dev = audioDeviceManager.ResolveInput(selectedMicrophoneInfo);
                    channels = dev?.AudioClient?.MixFormat?.Channels ?? 2;
                }
                catch (COMException ex) when ((uint)ex.HResult == 0x88890004u)
                {
                    Debug.WriteLine($"[UI] Input device invalidated while reading format: {ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    Debug.WriteLine($"[UI] Input device state unavailable while reading format: {ex.Message}");
                }

                PopulateChannelComboBoxes(Math.Max(1, channels));
            }
            else
            {
                PopulateChannelComboBoxes(2);
            }
        }

        private void BtnConnectInputAudio_Click(object sender, RoutedEventArgs e)
        {
            ApplyInputBackendToRecorder();

            if (selectedMicrophoneInfo == null) { MessageBox.Show("Žádný mikrofon není vybrán."); return; }

            var device = audioDeviceManager.ResolveInput(selectedMicrophoneInfo);
            if (device == null) { MessageBox.Show("Vybrané zaøízení nelze otevøít."); return; }

            recorder.SetDevice(device);
            recorder.CaptureChannelOverride = 0;
            if (recorder.Backend == InputBackend.Wasapi)
                recorder.ReprobeExclusiveSupport(GetSelectedSampleRate(), GetSelectedInputBitDepth(), GetSelectedInputIsFloat());

            isMicConnected = true;
            RefreshAllButtonStates();
        }

        private void BtnDisconnectInputAudio_Click(object sender, RoutedEventArgs e)
        {
            recorder?.StopAllAudio();
            isMicConnected = false;
            selectedMicrophoneInfo = null;
            ComboBoxSoundCard.SelectedIndex = -1;
            RefreshAllButtonStates();
        }

        private void BtnRefreshInputAudio_Click(object sender, RoutedEventArgs e)
        {
            BtnRefreshAudio.IsEnabled = false;
            RefreshInputAudioDevices();
            BtnRefreshAudio.IsEnabled = true;
        }

        private void RefreshInputAudioDevices()
        {
            _inputDevicesCache = audioDeviceManager.GetInputDevices();
            ComboBoxSoundCard.ItemsSource = null;
            ComboBoxSoundCard.ItemsSource = _inputDevicesCache;
            ComboBoxSoundCard.DisplayMemberPath = "Name";
            if (selectedMicrophoneInfo != null)
                ComboBoxSoundCard.SelectedItem = _inputDevicesCache.FirstOrDefault(d => d.Id == selectedMicrophoneInfo.Id);
            else
                ComboBoxSoundCard.SelectedIndex = -1;
            RefreshButtonStatesInput();
        }

        #endregion

        #region Recording button states

        private void RefreshButtonStatesRecording()
        {
            bool isRecording = recorder?.IsRecording ?? false;
            bool micSelected = selectedMicrophoneInfo != null;
            bool fileExists = File.Exists(recordedFilePath);

            BtnRecordAudio.IsEnabled = micSelected && !_isPlaying;
            BtnRecordAudio.Content = isRecording ? "Zastavit" : "Nahrát";
            BtnPlayAudio.IsEnabled = isRecorded && fileExists && !isRecording;
            BtnPlayAudio.Content = IsPlaybackActive ? "Zastavit" : "Pøehrát";

            RefreshButtonStatesInput();
            RefreshButtonStatesOutputDevices();
        }

        #endregion

        #region Recording event handlers

        private void Recorder_OnError(string message)
        {
            Dispatcher.Invoke(() => { Debug.WriteLine("[AudioRecorder] ERROR: " + message); MessageBox.Show($"Chyba nahrávání: {message}"); });
        }

        private void Recorder_OnRecordingStopped(string message)
        {
            Dispatcher.Invoke(() =>
            {
                isRecorded = true;
                UpdateInputModeStatus();
                RefreshButtonStatesRecording();
            });
        }

        private void Recorder_OnClipDetected()
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show(
                    "?? POZOR: Byl detekován CLIPPING (pøesycení signálu)!\n\n" +
                    "Audio signál pøesáhl 0 dBFS, což zpùsobuje zkreslení.\n" +
                    "Pro pøesné mìøení:\n" +
                    "• Snižte úroveò výstupu (output level)\n" +
                    "• Snižte zesílení mikrofonu (input gain)\n" +
                    "• Mìøení opakujte s nižší úrovní signálu",
                    "Clipping detekován!", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        #endregion

        #region Recording controls

        private async void BtnRecordAudio_Click(object sender, RoutedEventArgs e)
        {
            ApplyInputBackendToRecorder();
            if (selectedMicrophoneInfo == null) { MessageBox.Show("Není vybrán mikrofon."); return; }

            recorder.SetDevice(audioDeviceManager.ResolveInput(selectedMicrophoneInfo));
            recorder.CaptureChannelOverride = 0;
            if (recorder.Backend == InputBackend.Wasapi)
                recorder.ReprobeExclusiveSupport(GetSelectedSampleRate(), GetSelectedInputBitDepth(), GetSelectedInputIsFloat());

            if (!recorder.IsRecording)
            {
                if (selectedOutputDevice == null) { MessageBox.Show("Vyberte prosím ještì audio výstup."); return; }
                if (File.Exists(recordedFilePath))
                {
                    try { StopAndDisposePlayback(); File.Delete(recordedFilePath); }
                    catch (Exception ex) { MessageBox.Show("Chyba pøi pøípravì souboru k pøepsání: " + ex.Message); return; }
                }
                isRecorded = false;
                recorder.StartRecording(recordedFilePath, GetSelectedSampleRate(), GetSelectedInputBitDepth(), GetSelectedInputIsFloat());
                UpdateInputModeStatus();
            }
            else
            {
                BtnRecordAudio.IsEnabled = false;
                try { await recorder.StopRecordingAsync(); }
                finally { BtnRecordAudio.IsEnabled = true; UpdateInputModeStatus(); }
            }
            RefreshButtonStatesRecording();
        }

        private void BtnPlayAudio_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(recordedFilePath)) { MessageBox.Show("Nebyl nalezen žádný nahraný soubor."); return; }
            try
            {
                if (IsPlaybackActive) { StopAndDisposePlayback(); RefreshButtonStatesRecording(); return; }
                if (selectedOutputDevice == null) { MessageBox.Show("Není vybráno výstupní zaøízení."); return; }

                _playbackReader = new AudioFileReader(recordedFilePath);
                bool useShared = recorder.GetShareMode();
                int targetSampleRate = selectedOutputDevice.AudioClient?.MixFormat?.SampleRate ?? _playbackReader.WaveFormat.SampleRate;
                int targetChannels = selectedOutputDevice.AudioClient?.MixFormat?.Channels ?? _playbackReader.WaveFormat.Channels;
                var waveProvider = ConvertToDesiredWaveProvider(_playbackReader.ToSampleProvider(), BuildDesiredOutputFormat(targetSampleRate, targetChannels));

                if (useShared)
                {
                    _wasapiOut = new WasapiOut(selectedOutputDevice, AudioClientShareMode.Shared, false, 200);
                    _wasapiOut.Init(waveProvider);
                }
                else
                {
                    _wasapiOut = new WasapiOut(selectedOutputDevice, AudioClientShareMode.Exclusive, false, 200);
                    _wasapiOut.Init(waveProvider);
                }

                _wasapiOut.PlaybackStopped += (s, a) => { StopAndDisposePlayback(); Dispatcher.Invoke(RefreshButtonStatesRecording); };
                _wasapiOut.Play();
                _isPlaying = true;
                RefreshButtonStatesRecording();
            }
            catch (Exception ex) { MessageBox.Show("Chyba pøi pøehrávání: " + ex.Message); StopAndDisposePlayback(); RefreshButtonStatesRecording(); }
        }

        private void StopAllMicrophones()
        {
            recorder?.StopAllAudio();
            if (IsPlaybackActive) StopAndDisposePlayback();
            RefreshButtonStatesRecording();
        }

        #endregion

        #region Exclusive format probe

        private void BtnProbeExclusiveInput_Click(object sender, RoutedEventArgs e)
        {
            if (selectedMicrophoneInfo == null) { MessageBox.Show("Není vybrán mikrofon."); return; }
            var device = audioDeviceManager.ResolveInput(selectedMicrophoneInfo);
            if (device == null) { MessageBox.Show("Vybrané zaøízení nelze otevøít."); return; }

            try
            {
                recorder.SetDevice(device);
                recorder.CaptureChannelOverride = 0;
                var mix = device.AudioClient?.MixFormat;
                int probeChannels = mix?.Channels ?? 1;
                var results = recorder.ProbeExclusiveSupportedFormats(
                    new[] { 8000, 11025, 22050, 32000, 44100, 48000, 96000, 192000 },
                    new (int BitDepth, bool IsFloat)[] { (32, true), (16, false), (24, false), (32, false) },
                    channels: probeChannels);
                string report = $"Zaøízení: {device.FriendlyName}\nMix: {(mix == null ? "<null>" : $"{mix.SampleRate}Hz {mix.Channels}ch {mix.Encoding}")}\n\n"
                    + string.Join(Environment.NewLine, results);
                MessageBox.Show(report, "Exclusive režim – podporované formáty (vstup)");
            }
            catch (Exception ex) { MessageBox.Show("Test Exclusive selhal: " + ex.Message); }
        }

        #endregion

        #region Calibration

        private void BtnOpenLevelCalibration_Click(object sender, RoutedEventArgs e)
        {
            bool useRef = recorder?.UseReferenceChannel ?? false;
            int refCh = recorder?.InputReferenceChannel ?? 1;
            int sigCh = recorder?.InputSignalChannel ?? 0;
            int sr = GetSelectedSampleRate();

            MicCalibrationWindow win;
            if (IsAsioBackendSelected())
            {
                string? asioDriver = GetSelectedAsioDriverName();
                if (string.IsNullOrWhiteSpace(asioDriver)) { MessageBox.Show("Vyberte ASIO driver.", "Kalibrace", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                win = new MicCalibrationWindow(asioDriver, sigCh, useRef, refCh, sr) { Owner = this };
            }
            else
            {
                var inDevice = selectedMicrophoneInfo != null ? audioDeviceManager.ResolveInput(selectedMicrophoneInfo) : null;
                win = new MicCalibrationWindow(selectedOutputDevice, inDevice, sigCh, useRef, refCh, sr) { Owner = this };
            }
            win.ShowDialog();
        }

        #endregion
    }
}
