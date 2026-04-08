using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace MeaSound
{
    public partial class MainWindow
    {
        #region Serial port button states

        private void RefreshButtonStatesSerial()
        {
            bool portOpen = _serialPortManager.Port != null && _serialPortManager.Port.IsOpen;
            BtnConnectController.IsEnabled = !portOpen && ComboBoxController.SelectedIndex >= 0;
            BtnDisconnectController.IsEnabled = portOpen;
            BtnRefreshControllers.IsEnabled = !portOpen;
            ComboBoxController.IsEnabled = !portOpen;
        }

        #endregion

        #region Serial port connection

        private void RefreshPorts()
        {
            var ports = _serialPortManager.RefreshPorts();
            ComboBoxController.Items.Clear();
            foreach (var port in ports) ComboBoxController.Items.Add(port);
            if (ComboBoxController.Items.Count > 0) ComboBoxController.SelectedIndex = -1;
            RefreshButtonStatesSerial();
        }

        private void ComboBoxController_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshButtonStatesSerial();

        private async void BtnConnectController_Click(object sender, RoutedEventArgs e)
        {
            if (ComboBoxController.SelectedItem is not string selectedPort) return;
            string portName = selectedPort.Split(' ')[0];
            if (!portName.StartsWith("COM")) { MessageBox.Show("Neplatný sériový port."); return; }
            if (BaudrateComboBox.SelectedItem is not ComboBoxItem selectedItem || !int.TryParse(selectedItem.Content?.ToString(), out int baudRate) || baudRate <= 0)
            { MessageBox.Show("Zvol prosím rychlost pøenosu."); return; }

            string? connectionError = null;
            bool connected = await System.Threading.Tasks.Task.Run(() => _serialPortManager.TryConnectAndValidate(portName, baudRate, out connectionError));
            if (!connected)
            {
                await HandleCriticalConnectionFailureAsync(connectionError ?? "Nepodaøilo se navázat spojení se zaøízením.");
                return;
            }

            RefreshButtonStatesSerial();
        }

        private async System.Threading.Tasks.Task HandleCriticalConnectionFailureAsync(string reason)
        {
            MessageBox.Show(
                $"{reason}\n\nMìøení bude ukonèeno a USB bude odpojeno.",
                "Chyba komunikace zaøízení",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            try
            {
                if (_measurementTask != null || _measurementCts != null)
                    await CancelMeasurementAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("CancelMeasurementAsync failed: " + ex.Message);
            }

            try { _serialPortManager.Disconnect(); } catch (Exception ex) { Debug.WriteLine("Serial disconnect failed: " + ex.Message); }
            RefreshButtonStatesSerial();
        }

        private void BtnDisconnectController_Click(object sender, RoutedEventArgs e) { _serialPortManager.Disconnect(); RefreshButtonStatesSerial(); }
        private void BtnRefreshControllers_Click(object sender, RoutedEventArgs e) => RefreshPorts();

        #endregion

        #region Serial port indicator

        private void SerialIndicator()
        {
            _serialPortManager.OnConnectionStateChanged += state =>
            {
                EllipseCommunication.Dispatcher.Invoke(() =>
                {
                    EllipseCommunication.Background = state switch
                    {
                        SerialConnectionState.Ok => IndicatorGreen,
                        SerialConnectionState.Communicating => IndicatorActiveGreen,
                        _ => IndicatorOrange
                    };
                });
            };
        }

        #endregion

        #region Motor control

        private async void ToggleMotorLock_Checked(object sender, RoutedEventArgs e)
        {
            if (_serialPortManager.Port != null && _serialPortManager.Port.IsOpen)
            {
                ToggleMotorLock.IsEnabled = false; // Blokování proti spamu
                bool success = await _serialPortManager.UnlockMotorAsync();
                ToggleMotorLock.IsEnabled = true;

                if (success)
                {
                    ToggleMotorLock.Content = "Odemèen";
                }
                else
                {
                    // Pokud nepotvrdil, pøepneme UI zpìt
                    ToggleMotorLock.IsChecked = false;
                    MessageBox.Show("Motor nepotvrdil odemèení! Zkontrolujte hardware.", "Chyba komunikace", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else MessageBox.Show("Sériový port není pøipojen!", "Chyba", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void ToggleMotorLock_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_serialPortManager.Port != null && _serialPortManager.Port.IsOpen)
            {
                ToggleMotorLock.IsEnabled = false;
                bool success = await _serialPortManager.LockMotorAsync();
                ToggleMotorLock.IsEnabled = true;

                if (success)
                {
                    ToggleMotorLock.Content = "Zamèen";

                    // Po opìtovném zamèení motoru by mìlo následovat vynulování pozice, 
                    // protože s ním uživatel mohl fyzicky pohnout.
                    await _serialPortManager.ResetZeroPositionAsync();
                }
                else
                {
                    ToggleMotorLock.IsChecked = true;
                    MessageBox.Show("Motor nepotvrdil zamèení! Zkontrolujte hardware.", "Chyba komunikace", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else MessageBox.Show("Sériový port není pøipojen!", "Chyba", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void RotateByText_Click(object sender, RoutedEventArgs e)
        {
            if (_serialPortManager.Port != null && _serialPortManager.Port.IsOpen)
            {
                if (float.TryParse(RotateAngleBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float angle))
                {
                    RotateByText.IsEnabled = false; // Zabránìní uživateli v opakovaném stisku

                    bool success = await _serialPortManager.RotateMotorAsync(angle);

                    RotateByText.IsEnabled = true;

                    if (success)
                    {
                        RotateAngleBox.Text = "0"; // Úspìch, vyèistíme políèko
                    }
                    else
                    {
                        MessageBox.Show("Motor nedojel do požadované pozice nebo nepotvrdil akci (timeout).", "Chyba polohování", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else MessageBox.Show("Zadejte platný úhel (napø. 22.25 nebo 22,25)!", "Chyba", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else MessageBox.Show("Sériový port není pøipojen!", "Chyba", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        #endregion
    }
}