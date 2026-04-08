using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace MeaSound
{
    /// <summary>
    /// Main application window split into partial classes by functional area.
    /// </summary>
    public partial class MainWindow : System.Windows.Window
    {
        #region Constants

        private const string RecordedFileName = "recorded_audio.wav";
        private const string StartMeasurementText = "Spustit měření";
        private const string CancelMeasurementText = "Zrušit měření";

        private const int DwmUseImmersiveDarkMode = 20;
        private const int DwmUseImmersiveDarkModeLegacy = 19;
        private const int DwmCaptionColor = 35;
        private const int DwmTextColor = 36;

        #endregion

        #region Fields – Audio devices

        private AudioDeviceInfo? selectedMicrophoneInfo;
        private AudioDeviceInfo? selectedOutputInfo;
        private List<AudioDeviceInfo> _inputDevicesCache = new();
        private List<AudioDeviceInfo> _outputDevicesCache = new();

        private MMDevice? selectedOutputDevice;
        private WasapiOut? _wasapiOut;
        private AudioFileReader? _playbackReader;

        #endregion

        #region Fields – State flags

        private bool _isPlaying;
        private bool isMicConnected;
        private bool isRecorded;

        #endregion

        #region Fields – Recording / Measurement

        private string recordedFilePath;
        private string? measurementBaseFolder;
        private CancellationTokenSource? _measurementCts;
        private System.Threading.Tasks.Task? _measurementTask;

        #endregion

        #region Fields – Managers & services

        private readonly SerialPortManager _serialPortManager;
        private readonly AudioDeviceManager audioDeviceManager;
        private readonly AudioRecorder recorder;
        private ExcelDataSaver excelSaver;
        private MeasurementManager measurementManager;
        private SignalGenerator? signalGenerator;
        private readonly MeasurementLanProgressServer _measurementLanProgressServer = new();

        #endregion

        #region Fields – UI / Charts

        public double polarAxisRadius = 40;
        public PolarAxis polarAxis;

        private readonly Brush IndicatorGreen = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 200, 0));
        private readonly Brush IndicatorActiveGreen = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 150, 0));
        private readonly Brush IndicatorOrange = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 165, 0));

        private bool _isLeftPanelVisible = true;
        private GridLength _leftCol0Width;
        private GridLength _leftCol1Width;

        #endregion

        #region Properties

        private string _inputModeStatus = "Vstup: -";

        public string InputModeStatus
        {
            get => _inputModeStatus;
            set => _inputModeStatus = value;
        }

        private bool IsPlaybackActive => _wasapiOut != null && _wasapiOut.PlaybackState == PlaybackState.Playing;

        #endregion

        #region Constructor & Initialization

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            ThemeManager.Instance.Initialize();
            ThemeManager.Instance.OnThemeChanged += OnThemeChanged;

            audioDeviceManager = new AudioDeviceManager();
            audioDeviceManager.OnError += msg => MessageBox.Show(msg);

            _serialPortManager = new SerialPortManager();
            excelSaver = new ExcelDataSaver();

            recordedFilePath = GetRecordingFilePath();

            recorder = new AudioRecorder();
            recorder.OnError += Recorder_OnError;
            recorder.OnRecordingStopped += Recorder_OnRecordingStopped;
            recorder.OnClipDetected += Recorder_OnClipDetected;
            recorder.OnSettingsMismatchConfirmationRequested = ConfirmSettingsMismatch;

            RefreshPorts();
            SerialIndicator();
            InitializePolarPlot();

            RefreshInputAudioDevices();
            RefreshAudioOutputDevices();
            InitializeAudioInputComponents();

            if (selectedMicrophoneInfo != null)
            {
                var mmDevice = audioDeviceManager.ResolveInput(selectedMicrophoneInfo);
                recorder.SetDevice(mmDevice);
            }

            measurementManager = new MeasurementManager(recorder, _serialPortManager, excelSaver, PolarPlot, spectrogramPlot, audioDeviceManager, FftPlot);
            measurementManager.OnMeasurementProgress = UpdateMeasurementIndex;

            UpdateThemeButton();
            RefreshAllButtonStates();

            _leftCol0Width = LeftCol0.Width;
            _leftCol1Width = LeftCol1.Width;
            UpdateLeftPanelButton();

            SourceInitialized += (_, __) => ApplyWindowChromeTheme();
            Closing += MainWindow_Closing;
            Loaded += (_, __) =>
            {
                try { ComboBoxInputBackend_SelectionChanged(this, null); } catch { }
                try { UpdateInputModeStatus(); } catch { }
                try { PopulateChannelComboBoxes(2); } catch { }
                try { ApplyReferenceChannelSettings(); } catch { }
                try { UpdateDeconvolutionUiState(); } catch { }
                try { LoadLanProgressSettings(); } catch { }
                try { ApplyWindowChromeTheme(); } catch (Exception ex) { Debug.WriteLine($"[MainWindow] Error applying title bar theme: {ex.Message}"); }
            };
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            try { _measurementLanProgressServer.Dispose(); } catch { }
        }

        private void InitializePolarPlot()
        {
            ChartManagerScottPlot.Initialize(PolarPlot, "Polární graf");
            polarAxis = ChartManagerScottPlot.GetPolarAxis();
        }

        private void InitializeAudioInputComponents()
        {
            RefreshInputAudioDevices();

            if (selectedMicrophoneInfo != null)
            {
                var match = _inputDevicesCache.FirstOrDefault(d => d.Id == selectedMicrophoneInfo.Id);
                ComboBoxSoundCard.SelectedItem = match;

                var mmDevice = audioDeviceManager.ResolveInput(selectedMicrophoneInfo);
                recorder.SetDevice(mmDevice);
            }
            else
            {
                ComboBoxSoundCard.SelectedIndex = -1;
                recorder.SetDevice(null);
            }

            RefreshButtonStatesInput();
        }

        #endregion

        #region UI State Management

        private void RefreshAllButtonStates()
        {
            RefreshButtonStatesSerial();
            RefreshButtonStatesInput();
            RefreshButtonStatesOutputDevices();
            RefreshButtonStatesRecording();
        }

        private void UpdateInputModeStatus()
        {
            string status;
            if (recorder.Backend == InputBackend.Asio)
                status = "Vstup: ASIO";
            else
                status = recorder.IsExclusiveActive ? "Vstup: WASAPI Exclusive" : "Vstup: WASAPI Shared";

            InputModeStatus = status;

            try
            {
                var tb = FindName("TxtInputModeStatus") as TextBlock;
                if (tb != null)
                    tb.Text = status;
            }
            catch { }

            try { UpdateLanMeasurementDetailsFromUi(); } catch { }
        }

        #endregion

        #region Theme Management

        private void OnThemeChanged()
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    bool isDarkMode = ThemeManager.Instance.IsDarkMode;
                    ChartManagerScottPlot.ApplyTheme(isDarkMode);
                    ApplyThemeToAllWpfPlots(this, isDarkMode);
                    Spectrogram.ApplyThemeToExistingPlot(spectrogramPlot);
                    ApplyWindowChromeTheme();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MainWindow] Error applying theme to charts: {ex.Message}");
                }
            });
        }

        private void ApplyWindowChromeTheme()
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            if (windowHandle == IntPtr.Zero)
                return;

            int darkMode = ThemeManager.Instance.IsDarkMode ? 1 : 0;

            TrySetDwmWindowAttribute(windowHandle, DwmUseImmersiveDarkMode, darkMode);
            TrySetDwmWindowAttribute(windowHandle, DwmUseImmersiveDarkModeLegacy, darkMode);

            int captionColor = ThemeManager.Instance.IsDarkMode
                ? ToColorRef(31, 31, 31)
                : ToColorRef(240, 240, 240);
            int textColor = ThemeManager.Instance.IsDarkMode
                ? ToColorRef(255, 255, 255)
                : ToColorRef(0, 0, 0);

            TrySetDwmWindowAttribute(windowHandle, DwmCaptionColor, captionColor);
            TrySetDwmWindowAttribute(windowHandle, DwmTextColor, textColor);
        }

        private static int ToColorRef(byte red, byte green, byte blue)
            => red | (green << 8) | (blue << 16);

        private static void TrySetDwmWindowAttribute(IntPtr windowHandle, int attribute, int value)
        {
            int attributeValue = value;
            int result = DwmSetWindowAttribute(windowHandle, attribute, ref attributeValue, sizeof(int));
            if (result != 0)
                Debug.WriteLine($"[MainWindow] DwmSetWindowAttribute failed. Attr={attribute}, HResult={result}");
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        /// <summary>
        /// Recursively applies theme colors to all <see cref="WpfPlot"/> controls.
        /// </summary>
        private void ApplyThemeToAllWpfPlots(DependencyObject parent, bool isDarkMode)
        {
            if (parent == null) return;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is WpfPlot wpfPlot)
                {
                    ScottPlotThemeHelper.ApplyTheme(wpfPlot, isDarkMode);
                }

                ApplyThemeToAllWpfPlots(child, isDarkMode);
            }
        }

        private void BtnToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Instance.ToggleTheme();
            UpdateThemeButton();
        }

        private void UpdateThemeButton()
        {
            if (BtnToggleTheme != null)
                BtnToggleTheme.Content = ThemeManager.Instance.IsDarkMode ? "Light Mode" : "Dark Mode";

            if (ChkWhiteBackground != null)
            {
                var prefs = Preferences.Load();
                ChkWhiteBackground.IsChecked = prefs.SaveChartsWithWhiteBackground;
            }
        }

        private void ChkWhiteBackground_Changed(object sender, RoutedEventArgs e)
        {
            var prefs = Preferences.Load();
            prefs.SaveChartsWithWhiteBackground = ChkWhiteBackground.IsChecked == true;
            prefs.Save();
        }

        #endregion

        #region Layout Toggle

        private void BtnToggleLeftPanel_Click(object sender, RoutedEventArgs e)
        {
            _isLeftPanelVisible = !_isLeftPanelVisible;

            if (_isLeftPanelVisible)
            {
                LeftCol0.Width = _leftCol0Width;
                LeftCol1.Width = _leftCol1Width;
                LeftPanelScrollViewer.Visibility = Visibility.Visible;
            }
            else
            {
                _leftCol0Width = LeftCol0.Width;
                _leftCol1Width = LeftCol1.Width;

                LeftCol0.Width = new GridLength(0);
                LeftCol1.Width = new GridLength(0);
                LeftPanelScrollViewer.Visibility = Visibility.Collapsed;
            }

            UpdateLeftPanelButton();
        }

        private void UpdateLeftPanelButton()
        {
            if (BtnToggleLeftPanel != null)
                BtnToggleLeftPanel.Content = _isLeftPanelVisible ? "Skryt panel" : "Zobrazit panel";
        }

        #endregion

        #region Helpers

        private static string GetDownloadsFolderPath()
        {
            string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfilePath, "Downloads");
        }

        private static string GetRecordingFilePath()
        {
            return Path.Combine(GetDownloadsFolderPath(), RecordedFileName);
        }

        private void StopAndDisposePlayback()
        {
            try
            {
                if (_wasapiOut != null)
                {
                    try { _wasapiOut.Stop(); } catch { }
                    try { _wasapiOut.Dispose(); } catch { }
                    _wasapiOut = null;
                }
            }
            finally
            {
                try { _playbackReader?.Dispose(); } catch { }
                _playbackReader = null;
                _isPlaying = false;
            }
        }

        private bool ConfirmSettingsMismatch(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return true;

            MessageBoxResult result = MessageBoxResult.No;

            void ShowDialog()
            {
                result = MessageBox.Show(
                    this,
                    message,
                    "Neshoda audio nastavení",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
            }

            if (Dispatcher.CheckAccess()) ShowDialog();
            else Dispatcher.Invoke(ShowDialog);

            return result == MessageBoxResult.Yes;
        }

        #endregion
    }
}
