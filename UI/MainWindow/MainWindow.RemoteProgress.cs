using ScottPlot.WPF;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MeaSound
{
    public partial class MainWindow
    {
        private DispatcherTimer? _lanGraphSnapshotTimer;

        private void LoadLanProgressSettings()
        {
            var prefs = Preferences.Load();
            _measurementLanProgressServer.SetPassword(prefs.LanProgressPassword);
            EnsureLanGraphSnapshotTimerStarted();
            UpdateLanMeasurementDetailsFromUi();
        }

        private void EnsureLanGraphSnapshotTimerStarted()
        {
            if (_lanGraphSnapshotTimer != null)
                return;

            _lanGraphSnapshotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _lanGraphSnapshotTimer.Tick += (_, __) => RefreshLanGraphSnapshots();
            _lanGraphSnapshotTimer.Start();
        }

        private void RefreshLanGraphSnapshots()
        {
            if (!_measurementLanProgressServer.IsRunning)
                return;

            _measurementLanProgressServer.UpdateGraphImage("polar", TryCapturePlotImage(PolarPlot, "polar"));
            _measurementLanProgressServer.UpdateGraphImage("fft", TryCapturePlotImage(FftPlot, "fft"));
            _measurementLanProgressServer.UpdateGraphImage("spectrogram", TryCapturePlotImage(spectrogramPlot, "spectrogram"));
        }

        private static byte[]? TryCapturePlotImage(WpfPlot? plot, string name)
        {
            if (plot == null)
                return null;

            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"MeaSound_Lan_{name}.png");
                plot.Plot.SavePng(tempPath, 900, 500);
                return File.Exists(tempPath) ? File.ReadAllBytes(tempPath) : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LAN] Failed to capture plot '{name}': {ex.Message}");
                return null;
            }
        }

        private void UpdateLanMeasurementDetailsFromUi()
        {
            string inputMode = InputModeStatus;
            string signalType = (ComboBoxSignalType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "-";
            string sampleRate = $"{GetSelectedSampleRate()} Hz";
            string bitDepth = GetSelectedInputIsFloat() ? "32-bit Float" : $"{GetSelectedInputBitDepth()}-bit PCM";
            string inputDevice = selectedMicrophoneInfo?.Name ?? "-";
            string outputDevice = selectedOutputInfo?.Name ?? "-";

            _measurementLanProgressServer.UpdateMeasurementDetails(inputMode, signalType, sampleRate, bitDepth, inputDevice, outputDevice);
        }

        private void BtnOpenLanProgressPopup_Click(object sender, RoutedEventArgs e)
        {
            UpdateLanMeasurementDetailsFromUi();
            var popup = new LanProgressPopupWindow(_measurementLanProgressServer)
            {
                Owner = this
            };
            popup.ShowDialog();
        }
    }
}
