using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace MeaSound
{
    public partial class MainWindow
    {
        #region Output devices refresh and button states

        private void RefreshAudioOutputDevices()
        {
            _outputDevicesCache = audioDeviceManager.GetOutputDevices();
            ComboBoxSpeaker.ItemsSource = null;
            ComboBoxSpeaker.ItemsSource = _outputDevicesCache;
            ComboBoxSpeaker.DisplayMemberPath = "Name";
            if (selectedOutputInfo != null)
                ComboBoxSpeaker.SelectedItem = _outputDevicesCache.FirstOrDefault(d => d.Id == selectedOutputInfo.Id);
            else
                ComboBoxSpeaker.SelectedIndex = -1;
            if (selectedOutputDevice != null && (selectedOutputInfo == null || _outputDevicesCache.All(d => d.Id != selectedOutputInfo.Id)))
                selectedOutputDevice = null;
            RefreshButtonStatesOutputDevices();
        }

        private void RefreshButtonStatesOutputDevices()
        {
            bool deviceConnected = selectedOutputDevice != null;
            bool isPlaying = IsPlaybackActive;
            bool canConnect = !deviceConnected && (ComboBoxSpeaker.SelectedIndex >= 0 || selectedOutputInfo != null);

            BtnConnectOutputDevice.IsEnabled = canConnect;
            BtnDisconnectOutputDevice.IsEnabled = deviceConnected && !isPlaying;
            BtnRefreshOutputDevices.IsEnabled = !deviceConnected;
            BtnPlaySelectedSignal.IsEnabled = deviceConnected && !isPlaying;
            BtnStopPlayback.IsEnabled = deviceConnected && isPlaying;
            ComboBoxSpeaker.IsEnabled = !deviceConnected;
        }

        #endregion

        #region Output device connection

        private void BtnConnectOutputDevice_Click(object sender, RoutedEventArgs e)
        {
            var outputInfo = ComboBoxSpeaker.SelectedItem as AudioDeviceInfo ?? selectedOutputInfo;
            if (outputInfo == null) { MessageBox.Show("Vyberte výstupní zaøízení."); return; }
            var device = audioDeviceManager.ResolveOutput(outputInfo);
            if (device == null) { MessageBox.Show("Vybrané zaøízení nelze otevøít."); return; }
            try
            {
                StopAndDisposePlayback();
                selectedOutputInfo = outputInfo;
                selectedOutputDevice = device;
                ComboBoxSpeaker.SelectedItem = _outputDevicesCache.FirstOrDefault(d => d.Id == selectedOutputInfo.Id) ?? selectedOutputInfo;
            }
            catch (Exception ex) { MessageBox.Show("Chyba pøi pøipojování výstupního zaøízení: " + ex.Message); }
            RefreshAllButtonStates();
        }

        private void BtnDisconnectOutputDevice_Click(object sender, RoutedEventArgs e)
        {
            StopAndDisposePlayback();
            selectedOutputDevice = null; selectedOutputInfo = null;
            ComboBoxSpeaker.SelectedIndex = -1;
            MessageBox.Show("Výstupní zaøízení odpojeno.");
            RefreshAllButtonStates();
        }

        private void ComboBoxSpeaker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedOutputInfo = ComboBoxSpeaker.SelectedItem as AudioDeviceInfo;
            RefreshButtonStatesOutputDevices();
        }

        private void BtnRefreshOutputDevices_Click(object sender, RoutedEventArgs e)
        {
            BtnRefreshOutputDevices.IsEnabled = false;
            RefreshAudioOutputDevices();
            BtnRefreshOutputDevices.IsEnabled = true;
        }

        #endregion

        #region Audio format helpers

        private int GetSelectedSampleRate()
        {
            if (ComboBoxSampleRate?.SelectedItem is ComboBoxItem item && item.Tag != null && int.TryParse(item.Tag.ToString(), out int sr)) return sr;
            return 44100;
        }

        private int GetSelectedOutputBitDepth()
        {
            if (ComboBoxOutputBitDepth?.SelectedItem is ComboBoxItem item)
            {
                var tag = item.Tag?.ToString();
                if (string.Equals(tag, "32f", StringComparison.OrdinalIgnoreCase)) return 32;
                if (int.TryParse(tag, out int bits)) return bits;
            }
            return 16;
        }

        private bool GetSelectedOutputIsFloat()
        {
            if (ComboBoxOutputBitDepth?.SelectedItem is ComboBoxItem item)
                return string.Equals(item.Tag?.ToString(), "32f", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private int GetSelectedInputBitDepth()
        {
            if (ComboBoxInputBitDepth?.SelectedItem is ComboBoxItem item)
            {
                var tag = item.Tag?.ToString();
                if (string.Equals(tag, "32f", StringComparison.OrdinalIgnoreCase)) return 32;
                if (int.TryParse(tag, out int bits)) return bits;
            }
            return 16;
        }

        private bool GetSelectedInputIsFloat()
        {
            if (ComboBoxInputBitDepth?.SelectedItem is ComboBoxItem item)
                return string.Equals(item.Tag?.ToString(), "32f", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private WaveFormat BuildDesiredOutputFormat(int sampleRate, int channels)
        {
            return GetSelectedOutputIsFloat()
                ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
                : new WaveFormat(sampleRate, GetSelectedOutputBitDepth(), channels);
        }

        private static IWaveProvider ConvertToDesiredWaveProvider(ISampleProvider source, WaveFormat desiredFormat)
        {
            ISampleProvider sp = source;
            if (sp.WaveFormat.SampleRate != desiredFormat.SampleRate) sp = new WdlResamplingSampleProvider(sp, desiredFormat.SampleRate);
            if (sp.WaveFormat.Channels != desiredFormat.Channels)
                sp = desiredFormat.Channels switch { 1 => sp.ToMono(), 2 => sp.ToStereo(), _ => throw new NotSupportedException($"Unsupported channel count: {desiredFormat.Channels}") };
            if (desiredFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                return new SampleToWaveProvider(sp);

            return desiredFormat.BitsPerSample switch
            {
                16 => new SampleToWaveProvider16(sp),
                24 or 32 => new SampleToPcmWaveProvider(sp, desiredFormat.BitsPerSample),
                _ => throw new NotSupportedException($"Unsupported PCM bit depth: {desiredFormat.BitsPerSample}")
            };
        }

        #endregion

        #region Save path

        private void BtnChoosePath_Click(object sender, RoutedEventArgs e) => ChooseSaveFolder();

        private void ChooseSaveFolder()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Vyberte složku pro uložení mìøení",
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                measurementBaseFolder = dialog.SelectedPath;
                TxtSavePath.Text = measurementBaseFolder;
                measurementManager?.ResetSession();
            }
        }

        private string GetMeasurementFolder()
        {
            if (string.IsNullOrWhiteSpace(measurementBaseFolder))
            {
                ChooseSaveFolder();
                if (string.IsNullOrWhiteSpace(measurementBaseFolder))
                    throw new InvalidOperationException("Nebyla vybrána žádná složka pro uložení mìøení.");
            }
            return measurementBaseFolder!;
        }

        #endregion
    }
}
