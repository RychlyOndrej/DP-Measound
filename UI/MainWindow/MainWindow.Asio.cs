using NAudio.Wave;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace MeaSound
{
    public partial class MainWindow
    {
        #region ASIO backend selection

        private void ComboBoxInputBackend_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (ComboBoxInputBackend == null || AsioDriverPanel == null || ComboBoxAsioDriver == null) return;

                bool isAsio = IsAsioBackendSelected();
                AsioDriverPanel.Visibility = isAsio ? Visibility.Visible : Visibility.Collapsed;
                UpdateInputUiForBackend(isAsio);

                if (!isAsio)
                {
                    RefreshInputAudioDevices();
                    if (selectedMicrophoneInfo != null)
                    {
                        var dev = audioDeviceManager.ResolveInput(selectedMicrophoneInfo);
                        int channels = dev?.AudioClient?.MixFormat?.Channels ?? 2;
                        PopulateChannelComboBoxes(Math.Max(1, channels));
                    }
                    else
                    {
                        PopulateChannelComboBoxes(2);
                    }

                    ApplyReferenceChannelSettings();
                    return;
                }

                // I v ASIO necháme možnost vybrat mikrofon (napø. pro UI/metadata)
                RefreshInputAudioDevices();

                ComboBoxAsioDriver.SelectionChanged -= OnAsioDriverSelectionChanged;
                ComboBoxAsioDriver.ItemsSource = null;
                ComboBoxAsioDriver.Items.Clear();
                foreach (var name in AudioRecorder.GetAsioDriverNames())
                    ComboBoxAsioDriver.Items.Add(name);

                if (ComboBoxAsioDriver.Items.Count > 0)
                    ComboBoxAsioDriver.SelectedIndex = 0;

                ComboBoxAsioDriver.SelectionChanged += OnAsioDriverSelectionChanged;
                RefreshAsioChannelNames(ComboBoxAsioDriver.SelectedItem?.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[UI] Failed to load ASIO drivers: " + ex.Message);
                try { if (AsioDriverPanel != null) AsioDriverPanel.Visibility = Visibility.Collapsed; } catch { }
            }
        }

        private void UpdateInputUiForBackend(bool isAsio)
        {
            var wasapiGrid = FindName("WasapiInputSettingsGrid") as FrameworkElement;
            if (wasapiGrid != null)
                wasapiGrid.Visibility = Visibility.Visible;

            var inputHeader = FindName("TxtInputSectionHeader") as TextBlock;
            if (inputHeader != null)
                inputHeader.Text = "Vstup";
        }

        private void OnAsioDriverSelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshAsioChannelNames(ComboBoxAsioDriver.SelectedItem?.ToString());
        private bool IsAsioBackendSelected() => (ComboBoxInputBackend?.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "asio";
        private string? GetSelectedAsioDriverName() => ComboBoxAsioDriver?.SelectedItem?.ToString();

        #endregion

        #region ASIO channel names

        private void RefreshAsioChannelNames(string? driverName)
        {
            if (string.IsNullOrWhiteSpace(driverName))
            {
                PopulateChannelComboBoxesAsio(new List<(int Index, string Name)> { (0, "Line 1"), (1, "Line 2") });
                return;
            }

            AsioOut? temp = null;
            try
            {
                temp = new AsioOut(driverName);

                var inputNames = Enumerable.Range(0, temp.DriverInputChannelCount).Select(i => (Index: i, Name: temp.AsioInputChannelName(i))).ToList();
                if (inputNames.Count == 0)
                    inputNames = new List<(int Index, string Name)> { (0, "Line 1"), (1, "Line 2") };

                PopulateChannelComboBoxesAsio(inputNames.Select(x => (x.Index, x.Name)).ToList());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UI] Failed to read ASIO channel names from '{driverName}': {ex.Message}");
                PopulateChannelComboBoxesAsio(new List<(int Index, string Name)> { (0, "Line 1"), (1, "Line 2") });
            }
            finally { try { temp?.Dispose(); } catch { } }
        }

        private void PopulateChannelComboBoxesAsio(List<(int Index, string Name)> inputs)
        {
            if (ComboBoxInputSignalChannel == null || ComboBoxInputReferenceChannel == null || recorder == null) return;
            int prevSignal = recorder?.InputSignalChannel ?? 0, prevRef = recorder?.InputReferenceChannel ?? 1;
            bool prevUseRef = recorder?.UseReferenceChannel ?? false;

            ComboBoxInputSignalChannel.Items.Clear();
            ComboBoxInputReferenceChannel.Items.Clear();
            ComboBoxInputReferenceChannel.Items.Add(new ComboBoxItem { Tag = "none", Content = "None" });
            foreach (var (idx, name) in inputs)
            {
                string label = $"In {idx + 1} – {name}";
                ComboBoxInputSignalChannel.Items.Add(new ComboBoxItem { Tag = idx.ToString(), Content = label });
                ComboBoxInputReferenceChannel.Items.Add(new ComboBoxItem { Tag = idx.ToString(), Content = label });
            }
            ComboBoxInputSignalChannel.SelectedIndex = Math.Clamp(prevSignal, 0, inputs.Count - 1);
            ComboBoxInputReferenceChannel.SelectedIndex = !prevUseRef ? 0 : Math.Clamp(prevRef + 1, 0, ComboBoxInputReferenceChannel.Items.Count - 1);
            ApplyReferenceChannelSettings();
        }

        #endregion

        #region Channel configuration

        private void PopulateChannelComboBoxes(int channelCount)
        {
            if (ComboBoxInputSignalChannel == null || ComboBoxInputReferenceChannel == null) return;
            int prevSignal = recorder?.InputSignalChannel ?? 0, prevRef = recorder?.InputReferenceChannel ?? 1;
            bool prevUseRef = recorder?.UseReferenceChannel ?? false;

            ComboBoxInputSignalChannel.Items.Clear();
            ComboBoxInputReferenceChannel.Items.Clear();
            ComboBoxInputReferenceChannel.Items.Add(new ComboBoxItem { Tag = "none", Content = "None" });
            for (int i = 0; i < channelCount; i++)
            {
                string label = $"Input {i + 1}";
                ComboBoxInputSignalChannel.Items.Add(new ComboBoxItem { Tag = i.ToString(), Content = label });
                ComboBoxInputReferenceChannel.Items.Add(new ComboBoxItem { Tag = i.ToString(), Content = label });
            }
            ComboBoxInputSignalChannel.SelectedIndex = Math.Clamp(prevSignal, 0, channelCount - 1);
            ComboBoxInputReferenceChannel.SelectedIndex = !prevUseRef ? 0 : Math.Clamp(prevRef + 1, 0, ComboBoxInputReferenceChannel.Items.Count - 1);
            ApplyReferenceChannelSettings();
        }

        private void ApplyReferenceChannelSettings()
        {
            if (ComboBoxInputReferenceChannel == null || ComboBoxInputSignalChannel == null || recorder == null) return;

            int signalChannel = 0;
            if (ComboBoxInputSignalChannel.SelectedItem is ComboBoxItem signalItem && int.TryParse(signalItem.Tag?.ToString(), out int sigCh)) signalChannel = sigCh;

            bool useReference = false; int refChannel = 1;
            if (ComboBoxInputReferenceChannel.SelectedItem is ComboBoxItem refItem)
            {
                string? tag = refItem.Tag?.ToString();
                if (string.Equals(tag, "none", StringComparison.OrdinalIgnoreCase)) useReference = false;
                else if (int.TryParse(tag, out int ch)) { useReference = true; refChannel = ch; }
            }
            recorder.InputSignalChannel = signalChannel;
            recorder.UseReferenceChannel = useReference;
            recorder.InputReferenceChannel = refChannel;
        }

        private void ComboBoxInputReferenceChannel_Changed(object sender, SelectionChangedEventArgs e) => ApplyReferenceChannelSettings();
        private void ComboBoxInputSignalChannel_Changed(object sender, SelectionChangedEventArgs e) => ApplyReferenceChannelSettings();

        #endregion

        #region Backend application

        private void ApplyInputBackendToRecorder()
        {
            if (IsAsioBackendSelected()) { recorder.Backend = InputBackend.Asio; recorder.AsioDriverName = GetSelectedAsioDriverName(); }
            else { recorder.Backend = InputBackend.Wasapi; recorder.AsioDriverName = null; }
            ApplyReferenceChannelSettings();
            try { UpdateInputModeStatus(); } catch { }
        }

        #endregion
    }
}
