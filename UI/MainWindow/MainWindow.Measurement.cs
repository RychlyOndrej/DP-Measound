using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MeaSound
{
    public partial class MainWindow
    {
        private double _configuredWienerLambda = 1e-5;

        #region Measurement parameters

        private void TxtNumRepeats_Changed(object sender, TextChangedEventArgs e)
        {
            if (!int.TryParse(TxtNumRepeats.Text, out int repeats) || repeats <= 0) TxtNumRepeats.Text = "1";
            UpdateMeasurementIndex(0);
        }

        private void TxtStepsPerRev_Changed(object sender, TextChangedEventArgs e)
        {
            if (!int.TryParse(TxtStepsPerRev.Text, out int steps) || steps <= 0) TxtStepsPerRev.Text = "1";
            if (TxtAngleStep != null)
            {
                double angleStep = steps > 0 ? 360.0 / steps : 360.0;
                TxtAngleStep.Text = $"= {angleStep:0.##} °/krok";
            }
        }

        private void ComboBoxDeconvolutionMethod_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDeconvolutionUiState();

        private void UpdateDeconvolutionUiState()
        {
            bool isWiener = ComboBoxDeconvolutionMethod?.SelectedItem is ComboBoxItem item
                            && string.Equals(item.Tag?.ToString(), nameof(AnalysisMethod.Wiener), StringComparison.OrdinalIgnoreCase);

            if (TxtWienerLambdaLabel != null)
                TxtWienerLambdaLabel.Visibility = isWiener ? Visibility.Visible : Visibility.Collapsed;

            if (TxtWienerLambda != null)
                TxtWienerLambda.Visibility = isWiener ? Visibility.Visible : Visibility.Collapsed;
        }

        protected bool MeasureNoise() => CheckBtnRemoveNoise.IsChecked == true;

        private static readonly char[] CustomFrequencySeparators = [' ', '\t', '\r', '\n', ',', ';'];

        private bool TryParseCustomMonitoringFrequencies(out List<int> frequencies, out List<string> invalidTokens)
        {
            frequencies = new List<int>();
            invalidTokens = new List<string>();

            string input = CustomFrequencyBox?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(input))
                return true;

            var unique = new HashSet<int>();
            string[] tokens = input.Split(CustomFrequencySeparators, StringSplitOptions.RemoveEmptyEntries);

            foreach (string token in tokens)
            {
                if (!int.TryParse(token, out int freq) || freq <= 0)
                {
                    invalidTokens.Add(token);
                    continue;
                }

                unique.Add(freq);
            }

            frequencies = unique.OrderBy(f => f).ToList();
            return invalidTokens.Count == 0;
        }

        private List<int> GetSelectedFrequencies()
        {
            var selected = new HashSet<int>();

            foreach (var child in FrequencPanel.Children)
            {
                if (child is CheckBox checkBox && checkBox.IsChecked == true && int.TryParse(checkBox.Tag?.ToString(), out int freq) && freq > 0)
                    selected.Add(freq);
            }

            if (CustomFrequencyCheckBox.IsChecked == true && TryParseCustomMonitoringFrequencies(out var customFrequencies, out _))
            {
                foreach (int customFreq in customFrequencies)
                    selected.Add(customFreq);
            }

            return selected.OrderBy(f => f).ToList();
        }

        private void CustomFrequencyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (CustomFrequencyCheckBox == null || string.IsNullOrWhiteSpace(CustomFrequencyBox?.Text))
                return;

            CustomFrequencyCheckBox.IsChecked = true;
        }

        #endregion

        #region Measurement progress

        private void UpdateMeasurementIndex(int currentIndex)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)(() => UpdateMeasurementIndex(currentIndex)), DispatcherPriority.Background); return; }
            if (TxtMeasurementIndex == null || TxtNumRepeats == null) return;
            TxtMeasurementIndex.Text = int.TryParse(TxtNumRepeats.Text, out int totalRepeats) ? $"{currentIndex} / {totalRepeats}" : $"{currentIndex} / ?";
            _measurementLanProgressServer.UpdateMeasurementIndex(TxtMeasurementIndex.Text);

            if (ProgressMeasurement != null && int.TryParse(TxtNumRepeats.Text, out int repeats) && repeats > 0)
            {
                ProgressMeasurement.Maximum = repeats;
                ProgressMeasurement.Value = Math.Min(currentIndex, repeats);
            }

            try { PolarPlot?.Dispatcher.Invoke(() => PolarPlot.Refresh(), DispatcherPriority.Background); } catch (Exception ex) { Debug.WriteLine($"Unable to refresh PolarPlot: {ex.Message}"); }
        }

        private void UpdateStepProgress(int completedSteps, int totalSteps)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)(() => UpdateStepProgress(completedSteps, totalSteps)), DispatcherPriority.Background);
                return;
            }

            if (ProgressMeasurement == null)
                return;

            ProgressMeasurement.Maximum = Math.Max(1, totalSteps);
            ProgressMeasurement.Value = Math.Min(completedSteps, totalSteps);
            _measurementLanProgressServer.UpdateStepProgress(completedSteps, totalSteps);
        }

        private void SetMeasurementStatus(string status)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)(() => SetMeasurementStatus(status)), DispatcherPriority.Background);
                return;
            }

            if (TxtMeasurementStatus != null)
                TxtMeasurementStatus.Text = string.IsNullOrWhiteSpace(status) ? "-" : status;

            _measurementLanProgressServer.UpdateStatus(status);
            UpdateLanMeasurementDetailsFromUi();
        }

        private void BlockAction_Measuring(bool isMeasuring)
        {
            BtnDisconnectController.IsEnabled = !isMeasuring;
            BtnDisconnectAudio.IsEnabled = !isMeasuring;
            BtnRecordAudio.IsEnabled = !isMeasuring;
            BtnPlayAudio.IsEnabled = !isMeasuring;
        }

        private void ClearMeasurementPlots()
        {
            try { spectrogramPlot?.Dispatcher.Invoke(() => { try { spectrogramPlot.Plot.Clear(); spectrogramPlot.Refresh(); } catch { } }); } catch { }
        }

        #endregion

        #region Measurement start and cancel

        private bool ValidatePreStartConditions()
        {
            bool serialConnected = _serialPortManager.Port != null && _serialPortManager.Port.IsOpen;
            if (!serialConnected)
            {
                MessageBox.Show("Není pøipojen øídicí kontroler (sériový port).", "Kontrola pøed mìøením", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetMeasurementStatus("Mìøení nelze spustit: není pøipojen kontroler.");
                return false;
            }

            if (recorder.Backend == InputBackend.Asio)
            {
                if (string.IsNullOrWhiteSpace(recorder.AsioDriverName))
                {
                    MessageBox.Show("Není vybraný ASIO driver (výstup/vstup).", "Kontrola pøed mìøením", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetMeasurementStatus("Mìøení nelze spustit: není vybraný ASIO driver.");
                    return false;
                }
            }
            else
            {
                if (selectedOutputDevice == null)
                {
                    MessageBox.Show("Není pøipojeno výstupní audio zaøízení.", "Kontrola pøed mìøením", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetMeasurementStatus("Mìøení nelze spustit: není pøipojen výstup.");
                    return false;
                }

                if (!isMicConnected)
                {
                    MessageBox.Show("Není pøipojen vstupní mikrofon.", "Kontrola pøed mìøením", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SetMeasurementStatus("Mìøení nelze spustit: není pøipojen mikrofon.");
                    return false;
                }
            }

            bool isMotorLocked = ToggleMotorLock?.IsChecked == false;
            if (!isMotorLocked)
            {
                var result = MessageBox.Show(
                    "Motor není zamèený. Mìøení mùže být nepøesné.\n\nChcete pokraèovat i pøesto?",
                    "Motor není zamèený",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (result != MessageBoxResult.Yes)
                {
                    SetMeasurementStatus("Mìøení zrušeno uživatelem: motor není zamèený.");
                    return false;
                }
            }

            return true;
        }

        private async System.Threading.Tasks.Task CancelMeasurementAsync()
        {
            BtnStartMeasurement.IsEnabled = false;
            try { _measurementCts?.Cancel(); if (_measurementTask != null) await _measurementTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine("Error while canceling measurement: " + ex.Message); }
            finally
            {
                SetMeasurementStatus("Mìøení zrušeno.");
                BtnStartMeasurement.Content = StartMeasurementText;
                _measurementCts?.Dispose();
                _measurementCts = null;
                _measurementTask = null;
                BlockAction_Measuring(false);
                BtnStartMeasurement.IsEnabled = true;
            }
        }

        private async void BtnStartMeasurement_Click(object sender, RoutedEventArgs e)
        {
            BlockAction_Measuring(true);
            if (BtnStartMeasurement.Content?.ToString() == CancelMeasurementText) { await CancelMeasurementAsync(); return; }

            SetMeasurementStatus("Pøíprava mìøení...");
            ClearMeasurementPlots();
            int sampleRate = GetSelectedSampleRate(), bitDepth = GetSelectedInputBitDepth();
            bool isFloat = GetSelectedInputIsFloat();

            ApplyInputBackendToRecorder();
            recorder.CaptureChannelOverride = 0;

            if (!ValidatePreStartConditions())
            {
                BlockAction_Measuring(false);
                return;
            }

            if (recorder.Backend == InputBackend.Wasapi && selectedMicrophoneInfo != null)
            { recorder.SetDevice(audioDeviceManager.ResolveInput(selectedMicrophoneInfo)); recorder.ReprobeExclusiveSupport(sampleRate, bitDepth, isFloat); }

            var selectedShowFrequencies = GetSelectedFrequencies();
            if (selectedShowFrequencies.Count == 0) { MessageBox.Show("Vyberte alespoò jednu frekvenci."); SetMeasurementStatus("Mìøení nelze spustit: nejsou vybrané frekvence."); BlockAction_Measuring(false); return; }

            if (!int.TryParse(TxtNumRepeats.Text, out int totalRepeats)) totalRepeats = 1;
            if (!int.TryParse(TxtStepsPerRev.Text, out int totalSteps)) totalSteps = 1;

            UpdateMeasurementIndex(0);
            UpdateStepProgress(0, Math.Max(1, totalRepeats) * (Math.Max(1, totalSteps) + 1));

            if (ComboBoxSignalType.SelectedItem is not ComboBoxItem selectedItemSignal) { MessageBox.Show("Vyberte typ signálu."); SetMeasurementStatus("Mìøení nelze spustit: nevybraný typ signálu."); BlockAction_Measuring(false); return; }

            TestSignalType signalType;
            try { signalType = Enum.Parse<TestSignalType>(selectedItemSignal.Content.ToString()); }
            catch { MessageBox.Show("Nepodporovaný typ signálu."); SetMeasurementStatus("Mìøení nelze spustit: nepodporovaný typ signálu."); BlockAction_Measuring(false); return; }

            var outputInfo = selectedOutputInfo;
            if (recorder.Backend != InputBackend.Asio)
            { if (outputInfo == null || selectedOutputDevice == null) { MessageBox.Show("Vyberte a pøipojte výstupní zaøízení!"); SetMeasurementStatus("Mìøení nelze spustit: není výstupní zaøízení."); BlockAction_Measuring(false); return; } }
            else
            { if (string.IsNullOrWhiteSpace(recorder.AsioDriverName)) { MessageBox.Show("Vyberte ASIO driver."); SetMeasurementStatus("Mìøení nelze spustit: není vybraný ASIO driver."); BlockAction_Measuring(false); return; } }

            AudioDeviceInfo inputInfo;
            if (recorder.Backend == InputBackend.Asio)
            {
                inputInfo = ComboBoxSoundCard.SelectedItem as AudioDeviceInfo
                    ?? new AudioDeviceInfo
                    {
                        Id = "ASIO",
                        Name = $"ASIO: {recorder.AsioDriverName ?? "Unknown"}"
                    };
            }
            else
            {
                if (ComboBoxSoundCard.SelectedItem is not AudioDeviceInfo selectedInputInfo)
                {
                    MessageBox.Show("Vyberte mikrofon!");
                    SetMeasurementStatus("Mìøení nelze spustit: není vybraný mikrofon.");
                    BlockAction_Measuring(false);
                    return;
                }

                inputInfo = selectedInputInfo;
            }

            float[]? freqs = signalType switch { TestSignalType.MultiTone => ParseMultiToneFrequencies(), TestSignalType.SteppedSine => ParseSteppedSineFrequencies(), _ => null };

            AnalysisMethod analysisMethod = GetAnalysisMethodFromUi(signalType, GetSweepTypeFromComboBox());
            if (analysisMethod == AnalysisMethod.Cancelled)
            {
                SetMeasurementStatus("Mìøení zrušeno v konfiguraci analýzy.");
                BlockAction_Measuring(false);
                return;
            }

            string path;
            try { path = GetMeasurementFolder(); }
            catch (Exception ex) { MessageBox.Show(ex.Message); SetMeasurementStatus("Mìøení nelze spustit: neplatná cílová složka."); BlockAction_Measuring(false); return; }

            string sessionSuffix = (FindName("TxtSessionSuffix") as TextBox)?.Text ?? string.Empty;

            excelSaver = new ExcelDataSaver();
            BtnStartMeasurement.Content = CancelMeasurementText;
            _measurementCts?.Dispose();
            _measurementCts = new CancellationTokenSource();
            var token = _measurementCts.Token;

            if (!int.TryParse(TxtDelaySeconds.Text, out int delaySeconds)) delaySeconds = 0;
            if (delaySeconds > 0)
                SetMeasurementStatus($"Èekám {delaySeconds} s pøed mìøením...");

            await WaitWithCancelAsync(delaySeconds, token);

            measurementManager = new MeasurementManager(recorder, _serialPortManager, excelSaver, PolarPlot, spectrogramPlot, audioDeviceManager, FftPlot);
            measurementManager.OnMeasurementProgress = UpdateMeasurementIndex;
            measurementManager.OnStepProgress = UpdateStepProgress;
            measurementManager.OnStatusChanged = SetMeasurementStatus;
            measurementManager.WienerLambda = _configuredWienerLambda;
            int.TryParse(TxtMicAngle.Text, out int micAngleValue);

            try
            {
                if (!token.IsCancellationRequested)
                {
                    SetMeasurementStatus("Mìøení bìží...");
                    var backendSnapshot = recorder.Backend;
                    var asioNameSnapshot = recorder.AsioDriverName;
                    var channelOverrideSnapshot = recorder.CaptureChannelOverride;

                    _measurementTask = RunMeasurementOnStaThread(totalRepeats, totalSteps, micAngleValue, outputInfo, inputInfo, signalType, selectedShowFrequencies, sampleRate, bitDepth, isFloat, TxtMicDistance.Text ?? string.Empty, MeasureNoise(), ConstantFrequency(), GetSweepTypeFromComboBox(), GetMLSOrder(), GetCurrentDuration(), StartFrequency(), EndFrequency(), path, freqs, TextBoxCustomSoundFilePath.Text ?? string.Empty, token, backendSnapshot, asioNameSnapshot, channelOverrideSnapshot, analysisMethod, sessionSuffix);
                    await _measurementTask;
                }
            }
            catch (OperationCanceledException)
            {
                SetMeasurementStatus("Mìøení zrušeno.");
            }
            catch (Exception ex)
            {
                SetMeasurementStatus("Mìøení selhalo.");
                Dispatcher.Invoke(() => MessageBox.Show("Chyba bìhem mìøení: " + ex.Message));
            }
            finally
            {
                if (!token.IsCancellationRequested && BtnStartMeasurement.Content?.ToString() == CancelMeasurementText)
                    SetMeasurementStatus("Mìøení dokonèeno.");

                BtnStartMeasurement.Content = StartMeasurementText;
                _measurementCts?.Dispose();
                _measurementCts = null;
                _measurementTask = null;
                BlockAction_Measuring(false);
            }
        }

        #endregion

        #region Measurement ASIO STA thread helper

        private System.Threading.Tasks.Task RunMeasurementOnStaThread(
            int totalRepeats, int totalSteps, int micAngleValue,
            AudioDeviceInfo? outputInfo, AudioDeviceInfo inputInfo,
            TestSignalType signalType, List<int> selectedShowFrequencies,
            int sampleRate, int bitDepth, bool isFloat,
            string distanceMetersValue, bool recordNoiseValue,
            float constantFreq, SweepType sweepType, int MLSOrder, int duration,
            int startFreq, int endFreq, string path, float[]? freqs,
            string customSoundFilePath, CancellationToken token,
            InputBackend backendSnapshot, string? asioNameSnapshot, int channelOverrideSnapshot,
            AnalysisMethod analysisMethod = AnalysisMethod.Farina,
            string sessionSuffix = "")
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<object?>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            var th = new System.Threading.Thread(() =>
            {
                try
                {
                    measurementManager.RunMeasurement(totalRepeats, totalSteps, micAngleValue, outputInfo, inputInfo, signalType, selectedShowFrequencies, inputInfo.Name ?? "UnknownMicrophone", sampleRate, bitDepth, isFloat, distanceMetersValue, recordNoiseValue, analysisMethod, constantFreq, sweepType, MLSOrder, duration, startFreq, endFreq, path, freqs, polarAxis, polarAxisRadius, customSoundFilePath, token, backendSnapshot, asioNameSnapshot, channelOverrideSnapshot, 0, sessionSuffix);
                    tcs.TrySetResult(null);
                }
                catch (OperationCanceledException) { tcs.TrySetCanceled(token); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            th.IsBackground = true;
            try { th.SetApartmentState(System.Threading.ApartmentState.STA); }
            catch { Dispatcher.Invoke(() => MessageBox.Show("ASIO vyžaduje STA vlákno, ale toto prostøedí jej nepodporuje.")); tcs.TrySetException(new InvalidOperationException("Unable to set STAThread for ASIO.")); }
            if (!tcs.Task.IsCompleted) th.Start();
            return tcs.Task;
        }

        #endregion

        #region Measurement delay helper

        private async System.Threading.Tasks.Task WaitWithCancelAsync(int seconds, CancellationToken cancellationToken)
        {
            for (int i = 0; i < seconds; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                Dispatcher.Invoke(() => TxtDelaySeconds.Text = (seconds - i).ToString());
                try { await System.Threading.Tasks.Task.Delay(1000, cancellationToken); } catch (TaskCanceledException) { break; }
            }
        }

        #endregion

        #region Measurement analysis method selection

        private AnalysisMethod GetAnalysisMethodFromUi(TestSignalType signalType, SweepType sweepType)
        {
            if (ComboBoxDeconvolutionMethod?.SelectedItem is not ComboBoxItem selected)
            {
                MessageBox.Show("Vyberte dekonvoluci.");
                return AnalysisMethod.Cancelled;
            }

            string methodTag = selected.Tag?.ToString() ?? nameof(AnalysisMethod.DirectFft);
            if (!Enum.TryParse<AnalysisMethod>(methodTag, ignoreCase: true, out var method))
                method = AnalysisMethod.DirectFft;

            if (method == AnalysisMethod.Farina &&
                !(signalType == TestSignalType.SineSweep && sweepType == SweepType.ExponentialSweep))
            {
                MessageBox.Show("Farina je dostupná pouze pro Exponential Sweep (ESS).");
                return AnalysisMethod.Cancelled;
            }

            if (method == AnalysisMethod.Wiener && !recorder.UseReferenceChannel)
            {
                MessageBox.Show("Wiener vyžaduje zapnutý reference channel (loopback).");
                return AnalysisMethod.Cancelled;
            }

            if (method == AnalysisMethod.Wiener)
            {
                if (!TryParseWienerLambda(TxtWienerLambda?.Text, out double parsedLambda))
                {
                    MessageBox.Show("Neplatná hodnota Wiener ?. Zadejte exponent (napø. -7) nebo hodnotu (napø. 1E-7).");
                    return AnalysisMethod.Cancelled;
                }

                _configuredWienerLambda = parsedLambda;
            }

            return method;
        }

        private static bool TryParseWienerLambda(string? text, out double lambda)
        {
            lambda = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string raw = text.Trim().Replace(',', '.');

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed > 0)
            {
                lambda = parsed;
                return true;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int exponentOnly))
            {
                lambda = Math.Pow(10, exponentOnly);
                return lambda > 0;
            }

            return false;
        }

        #endregion
    }
}
