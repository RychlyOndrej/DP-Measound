using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace MeaSound
{
    public partial class LanProgressPopupWindow : Window
    {
        private readonly MeasurementLanProgressServer _server;
        private bool _isInitializing;
        private readonly DispatcherTimer _previewTimer;

        internal LanProgressPopupWindow(MeasurementLanProgressServer server)
        {
            _server = server;
            InitializeComponent();

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _previewTimer.Tick += (_, __) => RefreshWebPreview();
            _previewTimer.Start();

            LoadSettings();
        }

        private void LoadSettings()
        {
            _isInitializing = true;
            var prefs = Preferences.Load();

            ChkEnableLanProgress.IsChecked = prefs.EnableLanProgress;
            TxtLanProgressPassword.Password = prefs.LanProgressPassword ?? string.Empty;

            _server.SetPassword(TxtLanProgressPassword.Password);

            _isInitializing = false;
            RefreshLanProgressUi();
            RefreshWebPreview();
        }

        private void ChkEnableLanProgress_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            var prefs = Preferences.Load();
            prefs.EnableLanProgress = ChkEnableLanProgress.IsChecked == true;
            prefs.Save();

            if (ChkEnableLanProgress.IsChecked != true && _server.IsRunning)
                _server.Stop();

            RefreshLanProgressUi();
        }

        private void BtnSaveLanPassword_Click(object sender, RoutedEventArgs e)
        {
            string password = TxtLanProgressPassword.Password.Trim();
            if (string.IsNullOrWhiteSpace(password))
            {
                TxtLanProgressHint.Text = "Heslo nem˘ûe b˝t pr·zdnÈ.";
                return;
            }

            var prefs = Preferences.Load();
            prefs.LanProgressPassword = password;
            prefs.Save();
            _server.SetPassword(password);
            TxtLanProgressHint.Text = "Heslo uloûeno.";
        }

        private void TxtLanProgressPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            TxtLanProgressHint.Text = "Pro pouûitÌ novÈho hesla kliknÏte na Uloûit heslo.";
        }

        private void BtnToggleLanProgress_Click(object sender, RoutedEventArgs e)
        {
            if (_server.IsRunning)
            {
                _server.Stop();
                RefreshLanProgressUi();
                return;
            }

            if (ChkEnableLanProgress.IsChecked != true)
            {
                TxtLanProgressHint.Text = "Nejprve zapnÏte volbu pro sdÌlenÌ p¯es lok·lnÌ sÌù.";
                return;
            }

            string password = TxtLanProgressPassword.Password.Trim();
            if (string.IsNullOrWhiteSpace(password))
            {
                TxtLanProgressHint.Text = "Nejprve nastavte heslo pro p¯Ìstup z telefonu.";
                return;
            }

            _server.SetPassword(password);

            if (!_server.Start(preferredPort: 8787, out string? error))
            {
                TxtLanProgressHint.Text = $"SdÌlenÌ se nepoda¯ilo spustit: {error}";
                return;
            }

            TxtLanProgressHint.Text = "SdÌlenÌ bÏûÌ. Otev¯ete odkaz v telefonu a p¯ihlaste se heslem.";
            RefreshLanProgressUi();
        }

        private void BtnCopyLanProgressUrl_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtLanProgressUrl.Text;
            if (string.IsNullOrWhiteSpace(url))
                return;

            try
            {
                Clipboard.SetText(url);
                TxtLanProgressHint.Text = "Odkaz zkopÌrov·n do schr·nky.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LAN] Copy URL failed: " + ex.Message);
                TxtLanProgressHint.Text = "KopÌrov·nÌ odkazu selhalo.";
            }
        }

        private void RefreshWebPreview()
        {
            var snapshot = _server.GetSnapshot();
            TxtPreviewStatus.Text = snapshot.Status;
            TxtPreviewMeasurementIndex.Text = snapshot.MeasurementIndex;
            TxtPreviewSteps.Text = $"{snapshot.CompletedSteps} / {snapshot.TotalSteps} ({snapshot.ProgressPercent:0.0} %)";
            TxtPreviewUpdated.Text = snapshot.UpdatedAtUtc.ToLocalTime().ToString("HH:mm:ss");
            TxtPreviewInputMode.Text = snapshot.InputMode;
            TxtPreviewSignalAndFormat.Text = $"{snapshot.SignalType} | {snapshot.SampleRate} / {snapshot.BitDepth}";
            TxtPreviewDevices.Text = $"IN: {snapshot.InputDevice} | OUT: {snapshot.OutputDevice}";
        }

        private void RefreshLanProgressUi()
        {
            bool enabled = ChkEnableLanProgress.IsChecked == true;
            bool running = _server.IsRunning;

            BtnToggleLanProgress.Content = running ? "Zastavit sdÌlenÌ" : "Spustit sdÌlenÌ";
            BtnCopyLanProgressUrl.IsEnabled = running;
            BtnSaveLanPassword.IsEnabled = enabled;
            TxtLanProgressPassword.IsEnabled = enabled;

            TxtLanProgressUrl.Text = running ? _server.PublicUrl : string.Empty;

            if (!running)
            {
                TxtLanProgressHint.Text = enabled
                    ? "SdÌlenÌ je povoleno. Uloûte heslo a kliknÏte na Spustit sdÌlenÌ."
                    : "NeaktivnÌ. ZapnÏte volbu a spusùte sdÌlenÌ.";
            }
        }
    }
}
