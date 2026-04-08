using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MeaSound
{
    internal class SerialPortManager
    {
        public SerialPort? Port { get; private set; }
        public Action<string>? OnDataReceived;
        public Action<string>? OnError;
        public Action<string[]>? OnPortsRefreshed;

        public event Action<SerialConnectionState>? OnConnectionStateChanged;

        private readonly object _portLock = new object();
        private float _absoluteTargetAngle = 0.0f;

        public string[] RefreshPorts()
        {
            try
            {
                var ports = SerialPort.GetPortNames();
                return ports
                    .Select(port => $"{port} - {GetPortDescription(port)}")
                    .ToArray();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Chyba pøi získávání portù: {ex.Message}",
                    "Chyba",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return Array.Empty<string>();
            }
        }

        private string GetPortDescription(string portName)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");

                foreach (var obj in searcher.Get())
                {
                    string? caption = obj["Caption"]?.ToString();
                    if (caption != null && caption.Contains(portName))
                        return caption;
                }
            }
            catch
            {
                // ignorujeme chyby
            }

            return "Neznámé zaøízení";
        }

        public bool TryConnectAndValidate(string portName, int baudRate, out string? errorMessage)
        {
            errorMessage = null;

            Disconnect();

            Port = new SerialPort(portName, baudRate)
            {
                NewLine = "\n",
                ReadTimeout = 500,
                WriteTimeout = 500
            };

            try
            {
                Port.Open();
                Thread.Sleep(200); // Dáme ESP32 èas na probuzení
                Port.DiscardInBuffer();
            }
            catch (UnauthorizedAccessException ex)
            {
                errorMessage = $"Nelze otevøít port {portName}. Je již použit nebo nemáte oprávnìní.\n{ex.Message}";
                OnConnectionStateChanged?.Invoke(SerialConnectionState.Error);
                Disconnect();
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = $"Chyba pøi otevírání portu: {ex.Message}";
                OnConnectionStateChanged?.Invoke(SerialConnectionState.Error);
                Disconnect();
                return false;
            }

            if (!CheckDeviceConnection())
            {
                errorMessage = "Zaøízení je pøipojené, ale neodpovídá na test komunikace (PING/PONG).";
                Disconnect();
                return false;
            }

            // Po úspìšném pøipojení provedeme reset pozice v ESP32 na nulu
            Task.Run(async () => await ResetZeroPositionAsync()).Wait();

            return true;
        }

        public void Disconnect()
        {
            try
            {
                if (Port == null) return;

                // Pøidáno bezpeèné zavírání pro pøípad fyzického odpojení (IOException)
                try { if (Port.IsOpen) Port.Close(); } catch (IOException) { }

                Port.Dispose();
                Port = null;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Chyba pøi odpojování: {ex.Message}");
            }
        }

        public bool CheckDeviceConnection(int timeoutMs = 1000)
        {
            OnConnectionStateChanged?.Invoke(SerialConnectionState.Communicating);
            int maxRetries = 5;

            if (Port == null)
            {
                Debug.WriteLine("Port není inicializován.");
                OnConnectionStateChanged?.Invoke(SerialConnectionState.Error);
                return false;
            }

            lock (_portLock)
            {
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        if (!Port.IsOpen)
                            Port.Open();

                        Port.DiscardInBuffer();
                        Port.WriteLine("PING");

                        string? response = null;
                        var sw = System.Diagnostics.Stopwatch.StartNew();

                        while (sw.ElapsedMilliseconds < timeoutMs)
                        {
                            if (Port.BytesToRead > 0)
                            {
                                response = Port.ReadLine()?.Trim();
                                break;
                            }
                            Thread.Sleep(5);
                        }

                        if (response == "PONG")
                        {
                            OnConnectionStateChanged?.Invoke(SerialConnectionState.Ok);
                            Debug.WriteLine("Úspìch: USB komunikuje");
                            return true;
                        }

                        Debug.WriteLine($"Pokus {attempt} selhal - odpovìï: {response}");
                    }
                    catch (IOException ex)
                    {
                        Debug.WriteLine($"Hardware odpojen: {ex.Message}");
                        OnConnectionStateChanged?.Invoke(SerialConnectionState.Error);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Výjimka pøi pokusu {attempt}: {ex.Message}");
                    }

                    Thread.Sleep(70); // krátká pauza pøed dalším pokusem
                }
            }

            Debug.WriteLine("Selhalo navázání spojení");
            OnConnectionStateChanged?.Invoke(SerialConnectionState.Error);
            return false;
        }

        // ====================================================================
        // TRANSAKÈNÍ LOGIKA PRO OVLÁDÁNÍ MOTORU (MASTER-SLAVE)
        // ====================================================================

        private async Task<bool> SendTransactionAsync(string command, string expectedResponse, int timeoutMs)
        {
            if (Port == null || !Port.IsOpen) return false;

            return await Task.Run(() =>
            {
                lock (_portLock)
                {
                    try
                    {
                        Port.DiscardInBuffer();
                        Port.WriteLine(command);
                        Debug.WriteLine($"[TX] {command}");

                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < timeoutMs)
                        {
                            if (Port.BytesToRead > 0)
                            {
                                string response = Port.ReadLine()?.Trim() ?? "";
                                Debug.WriteLine($"[RX] {response}");

                                if (response == expectedResponse) return true;
                                if (response == "ACK_STOP") return false; // Pohyb pøerušen
                            }
                            Thread.Sleep(5);
                        }

                        Debug.WriteLine($"[TIMEOUT] Èekání na {expectedResponse} vypršelo.");
                        return false;
                    }
                    catch (IOException ex)
                    {
                        Debug.WriteLine($"Kabel byl pravdìpodobnì odpojen: {ex.Message}");
                        OnConnectionStateChanged?.Invoke(SerialConnectionState.Error);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Chyba komunikace: {ex.Message}");
                        return false;
                    }
                }
            });
        }

        public async Task<bool> UnlockMotorAsync()
        {
            return await SendTransactionAsync("EN_OFF", "ACK_EN_OFF", 2000);
        }

        public async Task<bool> LockMotorAsync()
        {
            return await SendTransactionAsync("EN_ON", "ACK_EN_ON", 2000);
        }

        public async Task<bool> ResetZeroPositionAsync()
        {
            bool success = await SendTransactionAsync("RESET", "ACK_RESET", 2000);
            if (success) _absoluteTargetAngle = 0.0f; // Synchronizace PC nuly s ESP32 nulou
            return success;
        }

        public async Task<bool> RotateMotorAsync(float degreesToAdd, int timeoutMs = 15000)
        {
            _absoluteTargetAngle += degreesToAdd;

            // InvariantCulture zaruèí teèku jako oddìlovaè
            string angleStr = _absoluteTargetAngle.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // ESP32 pøíjme absolutní pozici (GOTO) a musí poslat (DONE:uhel) až když dojede.
            return await SendTransactionAsync($"GOTO:{angleStr}", $"DONE:{angleStr}", timeoutMs);
        }
    }
}