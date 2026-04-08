using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeaSound
{
    public partial class MainWindow
    {
        #region Signal type UI

        private void ComboBoxSignalType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = (ComboBoxSignalType.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (selected == null) return;
            WaveformPanel.Visibility = (selected == "SineSweep" || selected == "ConstantTone") ? Visibility.Visible : Visibility.Collapsed;
            SweepPanel.Visibility = selected == "SineSweep" ? Visibility.Visible : Visibility.Collapsed;
            MLSPanel.Visibility = selected == "MLS" ? Visibility.Visible : Visibility.Collapsed;
            MultiTonePanel.Visibility = selected == "MultiTone" ? Visibility.Visible : Visibility.Collapsed;
            SteppedSinePanel.Visibility = selected == "SteppedSine" ? Visibility.Visible : Visibility.Collapsed;
            ConstantTone.Visibility = selected == "ConstantTone" ? Visibility.Visible : Visibility.Collapsed;
            CustomSoundFilePanel.Visibility = selected == "CustomFile" ? Visibility.Visible : Visibility.Collapsed;
            DurationPanel.Visibility = (selected != "MLS" && selected != "CustomFile") ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnSelectCustomSoundFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Audio Files (*.wav;*.mp3;*.flac)|*.wav;*.mp3;*.flac|All Files (*.*)|*.*",
                Title = "Vyberte audio soubor"
            };
            if (dialog.ShowDialog() == true) TextBoxCustomSoundFilePath.Text = dialog.FileName;
        }

        #endregion

        #region Frequency textbox placeholders

        private void TextBoxMultiToneFrequencies_GotFocus(object sender, RoutedEventArgs e)
        { if (sender is TextBox tb && tb.Text == "400,800") { tb.Text = ""; tb.ClearValue(TextBox.ForegroundProperty); } }

        private void TextBoxMultiToneFrequencies_LostFocus(object sender, RoutedEventArgs e)
        { if (sender is TextBox tb && string.IsNullOrWhiteSpace(tb.Text)) { tb.Text = "400,800"; tb.Foreground = Brushes.Gray; } }

        private void TextBoxSteppedSineFrequencies_GotFocus(object sender, RoutedEventArgs e)
        { if (sender is TextBox tb && tb.Text == "100,200,500,1000,2000,5000,10000") { tb.Text = ""; tb.ClearValue(TextBox.ForegroundProperty); } }

        private void TextBoxSteppedSineFrequencies_LostFocus(object sender, RoutedEventArgs e)
        { if (sender is TextBox tb && string.IsNullOrWhiteSpace(tb.Text)) { tb.Text = "100,200,500,1000,2000,5000,10000"; tb.Foreground = Brushes.Gray; } }

        #endregion

        #region Signal parameter parsers

        private float[]? ParseMultiToneFrequencies()
        {
            var freqs = TextBoxMultiToneFrequencies.Text
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : -1f)
                .Where(f => f > 0).ToArray();
            return freqs.Length > 0 ? freqs : null;
        }

        private float[]? ParseSteppedSineFrequencies()
        {
            var freqs = TextBoxSteppedSineFrequencies.Text
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : -1f)
                .Where(f => f > 0).ToArray();
            return freqs.Length > 0 ? freqs : null;
        }

        private SweepType GetSweepTypeFromComboBox()
        {
            if (ComboBoxSweepType.SelectedItem is ComboBoxItem stItem)
                return stItem.Tag?.ToString() switch { "Linear" => SweepType.Linear, "ExponentialSweep" => SweepType.ExponentialSweep, "PowerLaw" => SweepType.PowerLaw, _ => SweepType.Linear };
            return SweepType.Linear;
        }

        private int GetMLSOrder() { if (!int.TryParse(TextBoxMLSOrder.Text, out int order)) return 10; return Math.Clamp(order, 1, 20); }
        private int GetCurrentDuration() { if (!int.TryParse(TextBoxDuration.Text, out int d)) d = 2; return Math.Clamp(d, 1, 20); }
        private int StartFrequency() { if (!int.TryParse(TextBoxSweepStart.Text, out int f)) return 20; return Math.Clamp(f, 20, 20000); }
        private int EndFrequency() { if (!int.TryParse(TextBoxSweepEnd.Text, out int f)) return 20000; return Math.Clamp(f, 20, 20000); }
        private int ConstantFrequency() { if (!int.TryParse(TextBoxConstFreq.Text, out int f)) return 600; return Math.Clamp(f, 10, 22000); }

        #endregion

        #region Test signal playback

        private void BtnPlaySelectedSignal_Click(object sender, RoutedEventArgs e)
        {
            if (selectedOutputDevice == null) { MessageBox.Show("Výstupní zaøízení není pøipojeno."); return; }
            if (ComboBoxSignalType.SelectedItem is not ComboBoxItem selectedItem) { MessageBox.Show("Vyberte typ signálu."); return; }

            float[]? playMultipleFreqs = null;
            if (MultiTonePanel.Visibility == Visibility.Visible)
            {
                playMultipleFreqs = ParseMultiToneFrequencies();
                if (playMultipleFreqs == null || playMultipleFreqs.Length == 0) { MessageBox.Show("Zadejte platné frekvence pro Multi-tone (oddìlte [èárkou,mezerou]) (napø. 400,800). "); return; }
            }
            else if (SteppedSinePanel.Visibility == Visibility.Visible)
            {
                playMultipleFreqs = ParseSteppedSineFrequencies();
                if (playMultipleFreqs == null || playMultipleFreqs.Length == 0) { MessageBox.Show("Zadejte platné frekvence pro Stepped Sine (oddìlte èárkou, napø. 100,200,500,1000,2000,5000,10000)."); return; }
            }

            try
            {
                StopAndDisposePlayback();
                int sampleRate = GetSelectedSampleRate();
                if (signalGenerator == null || signalGenerator.SampleRate != sampleRate) signalGenerator = new SignalGenerator(sampleRate, 1);

                var signalType = Enum.Parse<TestSignalType>(selectedItem.Content.ToString());
                WaveformType waveform = WaveformType.Sine;
                if (WaveformPanel.Visibility == Visibility.Visible && ComboBoxWaveform.SelectedItem is ComboBoxItem wfItem)
                    waveform = Enum.Parse<WaveformType>(wfItem.Content.ToString());

                float[] samples = signalGenerator.GenerateSamples(signalType, GetCurrentDuration(), ConstantFrequency(), GetMLSOrder(), StartFrequency(), EndFrequency(), waveform, GetSweepTypeFromComboBox(), playMultipleFreqs, TextBoxCustomSoundFilePath.Text);
                if (samples.Length == 0) { MessageBox.Show("Chyba pøi generování signálu."); return; }

                var arrayProvider = new ArraySampleProvider(samples, sampleRate, 1);
                int deviceSampleRate = selectedOutputDevice?.AudioClient?.MixFormat?.SampleRate ?? sampleRate;
                int deviceChannels = selectedOutputDevice?.AudioClient?.MixFormat?.Channels ?? 1;
                ISampleProvider finalProvider = deviceSampleRate != sampleRate ? new WdlResamplingSampleProvider(arrayProvider, deviceSampleRate) : arrayProvider;

                // Apply calibration gain
                float calGain = Preferences.Load().GetCalibrationGainLinear();
                finalProvider = new VolumeSampleProvider(finalProvider) { Volume = calGain };

                bool useShared = recorder.GetShareMode();
                var desiredOutputFormat = BuildDesiredOutputFormat(deviceSampleRate, deviceChannels);
                var waveProvider = ConvertToDesiredWaveProvider(finalProvider, desiredOutputFormat);

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

                _wasapiOut.PlaybackStopped += (s, a) => Dispatcher.Invoke(() => { _isPlaying = false; StopAndDisposePlayback(); RefreshButtonStatesOutputDevices(); });
                _wasapiOut.Play();
                _isPlaying = true;

                double signalDuration = (double)samples.Length / (deviceSampleRate * 1) + 0.2;
                var shortSignalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(signalDuration) };
                shortSignalTimer.Tick += (s, ev) => { shortSignalTimer.Stop(); try { _wasapiOut?.Stop(); } catch { } _isPlaying = false; RefreshButtonStatesOutputDevices(); };
                shortSignalTimer.Start();
            }
            catch (Exception ex) { MessageBox.Show("Chyba pøi pøehrávání testovacího signálu: " + ex.Message); }
            RefreshButtonStatesOutputDevices();
        }

        private void BtnStopPlayback_Click(object sender, RoutedEventArgs e)
        {
            if (_wasapiOut != null) { _wasapiOut.Stop(); _isPlaying = false; RefreshButtonStatesOutputDevices(); }
        }

        private void WasapiOut_PlaybackStopped(object sender, NAudio.Wave.StoppedEventArgs e)
        {
            if (e.Exception != null) Dispatcher.Invoke(() => MessageBox.Show("Chyba pøi pøehrávání: " + e.Exception.Message));
            Dispatcher.Invoke(() => { _isPlaying = false; RefreshButtonStatesOutputDevices(); });
        }

        #endregion
    }
}
