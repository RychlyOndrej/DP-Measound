using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeaSound
{
    /// <summary>
    /// Dialog for selecting the frequency-response analysis method.
    /// </summary>
    internal class AnalysisMethodDialog : Window
    {
        public AnalysisMethod SelectedMethod { get; private set; } = AnalysisMethod.DirectFft;

        /// <summary>Wiener deconvolution regularization parameter.</summary>
        public double WienerLambda { get; private set; } = 1e-5;

        private readonly RadioButton _rbDirectFft;
        private readonly RadioButton? _rbWiener;
        private readonly RadioButton? _rbFarina;
        private readonly TextBox? _tbLambda;

        public AnalysisMethodDialog(string signalLabel, bool offerFarina = false, bool hasLoopback = false,
            double initialLambda = 1e-5)
        {
            Title = "Metoda analyzy frekvencni odezvy";
            Width = 440;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
           
            var subtitleBrush = Application.Current.TryFindResource("SubtitleForeground") as Brush
                                ?? Brushes.Gray;

            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = $"Typ signalu: {signalLabel}",
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6)
            });
            root.Children.Add(new TextBlock
            {
                Text = "Zvolte metodu analyzy frekvencni odezvy:",
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap
            });

            // Direct FFT
            _rbDirectFft = new RadioButton
            {
                Content = "Direct FFT",
                IsChecked = !offerFarina,
                Margin = new Thickness(0, 0, 0, 2),
                FontWeight = FontWeights.SemiBold,
                GroupName = "Method"
            };
            root.Children.Add(_rbDirectFft);
            root.Children.Add(new TextBlock
            {
                Text = "Jedno FFT pres cely aktivni usek signalu. Zadna dekonvoluce. " +
                       "Vhodne pro sum, tony, multitone, linearni sweep.",
                FontSize = 11,
                Foreground = subtitleBrush,
                Margin = new Thickness(20, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap
            });

            // Wiener deconvolution
            if (hasLoopback)
            {
                _rbWiener = new RadioButton
                {
                    Content = "Wiener dekonvoluce (Přes Loopback)",
                    Margin = new Thickness(0, 0, 0, 2),
                    FontWeight = FontWeights.SemiBold,
                    GroupName = "Method"
                };
                root.Children.Add(_rbWiener);
                root.Children.Add(new TextBlock
                {
                    Text = "Regularizovana spektralni inverze: H = Y·X* / (|X|²+λ). " +
                           "Pocita impulsni odezvu. Vhodne pro linearni sweep a MLS.",
                    FontSize = 11,
                    Foreground = subtitleBrush,
                    Margin = new Thickness(20, 0, 0, 6),
                    TextWrapping = TextWrapping.Wrap
                });

                // Lambda input row
                var lambdaPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(20, 0, 0, 12),
                    VerticalAlignment = VerticalAlignment.Center
                };
                lambdaPanel.Children.Add(new TextBlock
                {
                    Text = "Lambda (λ):",
                    FontSize = 11,
                    Foreground = subtitleBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                });
                _tbLambda = new TextBox
                {
                    Text = initialLambda.ToString("G3"),
                    Width = 100,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Regularization parameter (e.g. 1e-5). Higher values suppress more noise."
                };
                lambdaPanel.Children.Add(_tbLambda);
                lambdaPanel.Children.Add(new TextBlock
                {
                    Text = "  (napr. 1E-5, 1E-4, 1E-6)",
                    FontSize = 10,
                    Foreground = subtitleBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                root.Children.Add(lambdaPanel);

                // Enable/disable lambda field based on Wiener selection
                _rbWiener.Checked += (_, _) => _tbLambda.IsEnabled = true;
                _rbDirectFft.Checked += (_, _) => _tbLambda.IsEnabled = false;
                _tbLambda.IsEnabled = _rbWiener.IsChecked == true;
            }

            if (offerFarina)
            {
                _rbFarina = new RadioButton
                {
                    Content = "Farina dekonvoluce (Bez Loopback)",
                    IsChecked = true,
                    Margin = new Thickness(0, 0, 0, 2),
                    FontWeight = FontWeights.SemiBold,
                    GroupName = "Method"
                };
                root.Children.Add(_rbFarina);
                root.Children.Add(new TextBlock
                {
                    Text = "Analyticka metoda pro exponencialni sweep (ESS). " +
                           "Inverzni filtr se vypocte ze stejnych parametru jako sweep. " +
                           "Nejvyssi SNR, oddeluje harmonicke zkresleni.",
                    FontSize = 11,
                    Foreground = subtitleBrush,
                    Margin = new Thickness(20, 0, 0, 12),
                    TextWrapping = TextWrapping.Wrap
                });

                if (_tbLambda != null)
                    _rbFarina.Checked += (_, _) => _tbLambda.IsEnabled = false;
            }

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var btnOk = new Button
            {
                Content = "OK",
                Width = 80,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            btnOk.Click += (_, _) =>
            {
                SelectedMethod = _rbFarina?.IsChecked == true ? AnalysisMethod.Farina
                               : _rbWiener?.IsChecked == true ? AnalysisMethod.Wiener
                                                              : AnalysisMethod.DirectFft;

                if (_tbLambda != null)
                {
                    string raw = (_tbLambda.Text ?? string.Empty).Trim().Replace(",", ".");
                    double parsed = 0;
                    bool ok = false;

                    ok = double.TryParse(raw,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out parsed);

                    if (!ok && int.TryParse(raw,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int exponentOnly))
                    {
                        parsed = Math.Pow(10, exponentOnly);
                        ok = true;
                    }

                    if (ok && parsed > 0)
                        WienerLambda = parsed;
                }

                DialogResult = true;
            };

            var btnCancel = new Button { Content = "Zrusit", Width = 80, IsCancel = true };
            btnCancel.Click += (_, _) => DialogResult = false;

            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            root.Children.Add(btnPanel);

            Content = root;
        }
    }
}
