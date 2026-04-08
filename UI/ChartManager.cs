using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;

namespace MeaSound
{
    internal static class ChartManagerScottPlot
    {
        private static WpfPlot? _wpfPlot;
        private static PolarAxis? _polarAxis;
        private static readonly List<Scatter> _series = new();
        private static Dictionary<int, SortedDictionary<int, double>> _polarData = new();

        private const double DefaultMinDb = -30;
        private const double DefaultMaxDb = 4;
        private const int SpokeCount = 24;
        private static readonly ScottPlot.Angle AxisRotation = ScottPlot.Angle.FromDegrees(-90);

        public static PolarAxis? GetPolarAxis() => _polarAxis;

        public static void Initialize(WpfPlot wpfPlot, string title = "Polární graf")
        {
            _wpfPlot = wpfPlot ?? throw new ArgumentNullException(nameof(wpfPlot));
            ResetPlot(title);
            AddDemoData();
            
            ApplyTheme(ThemeManager.Instance.IsDarkMode);
            SetupInteractiveHandlers(_wpfPlot);

            _wpfPlot.Plot.Axes.AutoScale();
            UpdatePolarAxisLabels();

            _wpfPlot.Refresh();
        }

        public static void ApplyTheme(bool isDarkMode)
        {
            if (_wpfPlot == null) return;

            var plot = _wpfPlot.Plot;

            if (isDarkMode)
            {
                plot.FigureBackground.Color = Colors.DarkSlateGray;
                plot.DataBackground.Color = Colors.Black;
                plot.Legend.BackgroundColor = Colors.DarkGray;
                plot.Legend.FontColor = Colors.White;
                plot.Legend.OutlineColor = Colors.White;
            }
            else
            {
                plot.FigureBackground.Color = Colors.White;
                plot.DataBackground.Color = Colors.White;
                plot.Legend.BackgroundColor = Colors.White;
                plot.Legend.FontColor = Colors.Black;
                plot.Legend.OutlineColor = Colors.Black;
            }

            plot.Axes.Left.IsVisible = false;
            plot.Axes.Bottom.IsVisible = false;
            plot.Axes.Right.IsVisible = false;
            plot.Axes.Top.IsVisible = false;
            plot.Grid.IsVisible = false;

            if (_polarAxis != null)
                UpdatePolarAxisColors(isDarkMode);

            _wpfPlot.Refresh();
        }

        private static void UpdatePolarAxisColors(bool isDarkMode)
        {
            if (_polarAxis == null) return;

            Color textColor = isDarkMode ? Colors.WhiteSmoke : Colors.Black;
            Color majorLineColor = isDarkMode ? Colors.Gray : Colors.DarkGray;
            Color minorLineColor = isDarkMode ? Colors.DimGray : Colors.LightGray;

            foreach (var circle in _polarAxis.Circles)
            {
                circle.LabelStyle.ForeColor = textColor;
                if (circle.LineWidth > 1.5f)
                    circle.LineColor = majorLineColor;
                else
                    circle.LineColor = minorLineColor.WithAlpha(0.5);
            }

            foreach (var spoke in _polarAxis.Spokes)
            {
                spoke.LineColor = minorLineColor.WithAlpha(0.5);
                spoke.LabelStyle.ForeColor = textColor;
            }
        }

        public static void OpenInNewWindow()
        {
            if (_wpfPlot == null || _polarAxis == null) return;

            try
            {
                var popupPlot = new WpfPlot();
                foreach (var plottable in _wpfPlot.Plot.GetPlottables())
                    popupPlot.Plot.Add.Plottable(plottable);

                var originalLimits = _wpfPlot.Plot.Axes.GetLimits();
                popupPlot.Plot.Axes.SetLimits(originalLimits);
                popupPlot.Plot.Title("Polární graf - Nové okno");
                SetupInteractiveHandlers(popupPlot, isPopup: true);

                var window = new System.Windows.Window
                {
                    Title = "Polární graf - Nové okno",
                    Width = 800,
                    Height = 600,
                    Content = popupPlot,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen
                };

                if (popupPlot.ContextMenu != null)
                {
                    var itemsToRemove = new List<object>();
                    foreach (var item in popupPlot.ContextMenu.Items)
                    {
                        if (item is System.Windows.Controls.MenuItem menuItem &&
                            menuItem.Header?.ToString()?.Contains("New Window") == true)
                            itemsToRemove.Add(item);
                    }
                    foreach (var item in itemsToRemove)
                        popupPlot.ContextMenu.Items.Remove(item);
                }

                popupPlot.Refresh();
                window.Show();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PolarPlot] Error opening in new window: {ex.Message}");
                System.Windows.MessageBox.Show($"Chyba pøi otevírání v novém oknì: {ex.Message}", "Chyba",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private static void SetupInteractiveHandlers(WpfPlot plot, bool isPopup = false)
        {
            if (plot == null) return;

            plot.MouseWheel += (sender, e) =>
            {
                if (sender is WpfPlot wpfPlot) { UpdatePolarAxisLabelsForPlot(wpfPlot); wpfPlot.Refresh(); }
            };

            plot.MouseMove += (sender, e) =>
            {
                if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed ||
                    System.Windows.Input.Mouse.MiddleButton == System.Windows.Input.MouseButtonState.Pressed)
                {
                    if (sender is WpfPlot wpfPlot) { UpdatePolarAxisLabelsForPlot(wpfPlot); wpfPlot.Refresh(); }
                }
            };
        }

        private static void UpdatePolarAxisLabels()
        {
            if (_wpfPlot == null) return;
            UpdatePolarAxisLabelsForPlot(_wpfPlot);
        }

        private static void UpdatePolarAxisLabelsForPlot(WpfPlot plot)
        {
            if (plot == null) return;

            try
            {
                PolarAxis? polarAxis = null;
                foreach (var plottable in plot.Plot.GetPlottables())
                {
                    if (plottable is PolarAxis pa) { polarAxis = pa; break; }
                }
                if (polarAxis == null) return;

                var axisLimits = plot.Plot.Axes.GetLimits();
                double visibleRange = Math.Max(
                    Math.Abs(axisLimits.Right - axisLimits.Left),
                    Math.Abs(axisLimits.Top - axisLimits.Bottom));

                DrawAxisCircles(polarAxis, _currentDisplayMin, _currentDisplayMax, visibleRange);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PolarPlot] Error updating labels: {ex.Message}");
            }
        }

        public static void Clear()
        {
            if (_wpfPlot == null) return;
            _polarData = new Dictionary<int, SortedDictionary<int, double>>();
            _currentDisplayMin = DefaultMinDb;
            _currentDisplayMax = 0.0;
            ResetPlot(title: null);
            _wpfPlot.Plot.Axes.AutoScale();
            UpdatePolarAxisLabels();
            _wpfPlot.Refresh();
        }

        public static void SetData(Dictionary<int, SortedDictionary<int, double>> newData)
        {
            if (newData == null || newData.Count == 0) { Clear(); return; }
            _polarData = newData;
            UpdatePlot();
        }

        private static void ResetPlot(string? title)
        {
            if (_wpfPlot == null) return;

            try
            {
                _wpfPlot.Plot.Clear();
                if (!string.IsNullOrWhiteSpace(title))
                    _wpfPlot.Plot.Title(title);

                _series.Clear();
                _polarAxis = CreatePolarAxis(DefaultMinDb, DefaultMaxDb);
                ApplyTheme(ThemeManager.Instance.IsDarkMode);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error resetting polar plot: " + ex.Message);
            }
        }

        private static PolarAxis CreatePolarAxis(double displayMinDb, double displayMaxDb)
        {
            if (_wpfPlot == null) throw new InvalidOperationException("Plot is not initialized.");

            double range = displayMaxDb - displayMinDb;
            var axis = _wpfPlot.Plot.Add.PolarAxis(radius: range);
            axis.Rotation = AxisRotation;

            DrawAxisCircles(axis, displayMinDb, displayMaxDb);
            axis.SetSpokes(count: SpokeCount, length: range, degreeLabels: true);
            return axis;
        }

        private static void DrawAxisCircles(PolarAxis axis, double displayMinDb, double displayMaxDb, double? visibleRange = null)
        {
            double range = displayMaxDb - displayMinDb;

            double circleStep, labelStep;
            bool useDecimals = false;

            if (visibleRange.HasValue)
            {
                double zoomRatio = visibleRange.Value / range;
                if (zoomRatio < 1.3 && zoomRatio > 0.7)      { circleStep = 0.5;  labelStep = 1;   useDecimals = false; }
                else if (zoomRatio < 0.7)                      { circleStep = 0.25; labelStep = 0.5; useDecimals = true;  }
                else if (zoomRatio > 4.0)                      { circleStep = 5.0;  labelStep = 5.0; useDecimals = false; }
                else                                           { circleStep = 1.0;  labelStep = 2.0; useDecimals = false; }
            }
            else
            {
                circleStep = 2.5; labelStep = 5.0; useDecimals = false;
            }

            int count = Math.Max(4, (int)Math.Ceiling(range / circleStep) + 1);
            var radii = new double[count];
            var labels = new string[count];
            double step = range / (count - 1);

            for (int i = 0; i < count; i++)
            {
                double r = i * step;
                radii[i] = r;
                double dbValue = r + displayMinDb;
                bool isRound = Math.Abs(dbValue % labelStep) < 0.5 || Math.Abs(dbValue % labelStep) > (labelStep - 0.5);
                labels[i] = isRound ? (useDecimals ? $"{dbValue:F1} dB" : $"{dbValue:F0} dB") : string.Empty;
            }

            axis.SetCircles(radii, labels);

            bool isDarkMode = ThemeManager.Instance.IsDarkMode;
            Color textColor        = isDarkMode ? Colors.WhiteSmoke              : Colors.Black;
            Color majorColor       = isDarkMode ? Colors.White.WithAlpha(0.9)    : Colors.Black.WithAlpha(0.8);
            Color intermediateColor= isDarkMode ? Colors.White.WithAlpha(0.7)    : Colors.Gray.WithAlpha(0.5);
            Color minorColor       = isDarkMode ? Colors.White.WithAlpha(0.5)    : Colors.Gray.WithAlpha(0.4);
            Color tinyColor        = isDarkMode ? Colors.Gray.WithAlpha(0.5)     : Colors.Gray.WithAlpha(0.2);

            for (int i = 0; i < axis.Circles.Count; i++)
            {
                double dbValue = radii[i] + displayMinDb;
                axis.Circles[i].LabelStyle.ForeColor = textColor;

                if (string.IsNullOrEmpty(labels[i]))
                {
                    axis.Circles[i].LineWidth = 0.5f;
                    axis.Circles[i].LineColor = tinyColor;
                }
                else
                {
                    bool isMajor        = Math.Abs(dbValue % 10) < 0.5 || Math.Abs(dbValue % 10) > 9.5;
                    bool isIntermediate = Math.Abs(dbValue % 5)  < 0.5 || Math.Abs(dbValue % 5)  > 4.5;

                    if (isMajor)             { axis.Circles[i].LineWidth = 2.0f; axis.Circles[i].LineColor = majorColor; }
                    else if (isIntermediate) { axis.Circles[i].LineWidth = 1.2f; axis.Circles[i].LineColor = intermediateColor; }
                    else                     { axis.Circles[i].LineWidth = 0.8f; axis.Circles[i].LineColor = minorColor; }
                }
            }

            foreach (var spoke in axis.Spokes)
            {
                spoke.LabelStyle.ForeColor = textColor;
                spoke.LineColor = isDarkMode ? Colors.Gray.WithAlpha(0.5) : Colors.Gray.WithAlpha(0.5);
            }
        }

        private static double _currentDisplayMin = DefaultMinDb;
        private static double _currentDisplayMax = 0.0;

        private static void UpdatePlot()
        {
            if (_wpfPlot == null || _polarAxis == null || _polarData == null || _polarData.Count == 0)
                return;

            double dataMin = double.MaxValue, dataMax = double.MinValue;
            int totalDataPoints = 0;

            foreach (var fe in _polarData.Values)
            {
                totalDataPoints += fe.Values.Count;
                foreach (var v in fe.Values)
                {
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    dataMin = Math.Min(dataMin, v);
                    dataMax = Math.Max(dataMax, v);
                }
            }

            if (dataMin == double.MaxValue || dataMax == double.MinValue) { dataMin = DefaultMinDb; dataMax = DefaultMaxDb; }

            double displayMax = 0.0;
            double desiredDisplayMin = Math.Floor(dataMin / 5.0) * 5.0;

            const double minRange = 10.0;
            if (displayMax - desiredDisplayMin < minRange) desiredDisplayMin = displayMax - minRange;
            desiredDisplayMin -= 5.0;

            bool rangeChanged = Math.Abs(_currentDisplayMin - desiredDisplayMin) > 2.0 ||
                                Math.Abs(_currentDisplayMax - displayMax) > 0.1;

            _currentDisplayMin = desiredDisplayMin;
            _currentDisplayMax = displayMax;

            double range = displayMax - desiredDisplayMin;
            var perFreq = new List<(int freq, double[] xs, double[] ys, bool clipped)>(_polarData.Count);

            foreach (var freqEntry in _polarData.OrderBy(f => f.Key))
            {
                var angles = freqEntry.Value;
                if (angles == null || angles.Count == 0) continue;

                int validCount = 0;
                foreach (var v in angles.Values)
                    if (!double.IsNaN(v) && !double.IsInfinity(v)) validCount++;
                if (validCount == 0) continue;

                var xs = new double[validCount];
                var ys = new double[validCount];
                bool anyClipped = false;
                int i = 0;

                foreach (var kvp in angles)
                {
                    double db = kvp.Value;
                    if (double.IsNaN(db) || double.IsInfinity(db)) continue;

                    double angleNormalized = kvp.Key % 360;
                    if (angleNormalized < 0) angleNormalized += 360;

                    anyClipped |= (db < desiredDisplayMin) || (db > displayMax);
                    double radius = Math.Clamp(db, desiredDisplayMin, displayMax) - desiredDisplayMin;

                    var pt = _polarAxis.GetCoordinates(radius, angleNormalized);
                    xs[i] = pt.X; ys[i] = pt.Y; i++;
                }

                if (i != validCount) { Array.Resize(ref xs, i); Array.Resize(ref ys, i); }
                if (xs.Length == 0) continue;

                perFreq.Add((freqEntry.Key, xs, ys, anyClipped));
            }

            if (perFreq.Count == 0) return;

            if (rangeChanged)
            {
                try { _wpfPlot.Plot.Remove(_polarAxis); } catch { }
                _polarAxis = CreatePolarAxis(desiredDisplayMin, displayMax);
            }

            foreach (var s in _series)
                try { _wpfPlot.Plot.Remove(s); } catch { }
            _series.Clear();

            int colorIndex = 0;
            foreach (var item in perFreq.OrderBy(t => t.freq))
            {
                var scatter = _wpfPlot.Plot.Add.Scatter(item.xs, item.ys);
                var col = (colorIndex % 10) switch
                {
                    0 => ScottPlot.Colors.Red,     1 => ScottPlot.Colors.Blue,
                    2 => ScottPlot.Colors.Green,   3 => ScottPlot.Colors.Gold,
                    4 => ScottPlot.Colors.Purple,  5 => ScottPlot.Colors.Orange,
                    6 => ScottPlot.Colors.Cyan,    7 => ScottPlot.Colors.Magenta,
                    8 => ScottPlot.Colors.Brown,   _ => ScottPlot.Colors.Lime
                };
                scatter.MarkerSize = 5; scatter.LineWidth = 2;
                scatter.Label = $"{item.freq} Hz";
                scatter.LineColor = col; scatter.MarkerColor = col;
                _series.Add(scatter);

                if (item.clipped)
                {
                    var m = _wpfPlot.Plot.Add.Scatter(item.xs, item.ys);
                    m.LineWidth = 0; m.MarkerSize = 6;
                    m.MarkerShape = MarkerShape.FilledCircle;
                    m.MarkerColor = col; m.Label = null;
                    _series.Add(m);
                }
                colorIndex++;
            }

            _wpfPlot.Plot.Axes.AutoScale();
            UpdatePolarAxisLabels();

            _wpfPlot.Refresh();
        }

        private static void AddDemoData()
        {
            if (_wpfPlot == null || _polarAxis == null) return;

            double baseRadius = (DefaultMaxDb - DefaultMinDb) / 2;
            double amplitude = 6.0;

            double CalcRadius(double angleDeg)
            {
                double wave = Math.Cos(angleDeg * Math.PI / 180 * 2);
                return baseRadius + (wave > 0 ? wave * 0.7 : wave) * amplitude;
            }

            var angles = Enumerable.Range(0, 361).Select(a => (double)a).ToArray();
            var xs = new double[angles.Length];
            var ys = new double[angles.Length];

            for (int i = 0; i < angles.Length; i++)
            {
                var pt = _polarAxis.GetCoordinates(CalcRadius(angles[i]), angles[i]);
                xs[i] = pt.X; ys[i] = pt.Y;
            }

            var line = _wpfPlot.Plot.Add.Scatter(xs, ys);
            line.LineColor = ScottPlot.Colors.Blue.WithAlpha(0.8);
            line.LineWidth = 2; line.MarkerSize = 0;
            line.Label = "Demo tvar (linie)";

            var markerAngles = Enumerable.Range(0, 37).Select(a => a * 10.0).ToArray();
            var mx = new double[markerAngles.Length];
            var my = new double[markerAngles.Length];

            for (int i = 0; i < markerAngles.Length; i++)
            {
                var pt = _polarAxis.GetCoordinates(CalcRadius(markerAngles[i]), markerAngles[i]);
                mx[i] = pt.X; my[i] = pt.Y;
            }

            var markers = _wpfPlot.Plot.Add.Scatter(mx, my);
            markers.LineWidth = 0; markers.MarkerShape = MarkerShape.FilledCircle;
            markers.MarkerColor = ScottPlot.Colors.Red; markers.MarkerSize = 6;
            markers.Label = "Vzorky (10°)";

            _series.Clear();
            _series.Add(line);
            _series.Add(markers);
            _wpfPlot.Refresh();
        }

        public static bool PrepareForSave(bool forceWhiteBackground)
        {
            if (_wpfPlot == null || _polarAxis == null) return false;

            bool wasDark = ThemeManager.Instance.IsDarkMode;

            if (forceWhiteBackground && wasDark)
            {
                var plot = _wpfPlot.Plot;
                plot.FigureBackground.Color = Colors.White;
                plot.DataBackground.Color = Colors.White;
                plot.Legend.BackgroundColor = Colors.White;
                plot.Legend.FontColor = Colors.Black;
                plot.Legend.OutlineColor = Colors.Black;
                DrawAxisCirclesForSave(_polarAxis, _currentDisplayMin, _currentDisplayMax, isDarkMode: false);
            }
            else
            {
                DrawAxisCirclesForSave(_polarAxis, _currentDisplayMin, _currentDisplayMax, isDarkMode: wasDark);
            }

            _wpfPlot.Plot.Axes.AutoScale();
            _wpfPlot.Refresh();
            return wasDark;
        }

        public static void RestoreAfterSave(bool wasDark)
        {
            if (_wpfPlot == null || _polarAxis == null) return;
            ApplyTheme(wasDark);
            UpdatePolarAxisLabels();
            _wpfPlot.Refresh();
        }

        private static void DrawAxisCirclesForSave(PolarAxis axis, double displayMinDb, double displayMaxDb, bool isDarkMode)
        {
            double range = displayMaxDb - displayMinDb;
            const double saveCircleStep = 1.0, saveLabelStep = 2.0;

            int count = Math.Max(4, (int)Math.Ceiling(range / saveCircleStep) + 1);
            var radii = new double[count];
            var labels = new string[count];
            double step = range / (count - 1);

            for (int i = 0; i < count; i++)
            {
                radii[i] = i * step;
                double dbValue = radii[i] + displayMinDb;
                bool isRound = Math.Abs(dbValue % saveLabelStep) < 0.5 || Math.Abs(dbValue % saveLabelStep) > (saveLabelStep - 0.5);
                labels[i] = isRound ? $"{dbValue:F0} dB" : string.Empty;
            }

            axis.SetCircles(radii, labels);

            Color textColor        = isDarkMode ? Colors.WhiteSmoke           : Colors.Black;
            Color majorColor       = isDarkMode ? Colors.White.WithAlpha(0.9) : Colors.Black.WithAlpha(0.8);
            Color intermediateColor= isDarkMode ? Colors.White.WithAlpha(0.7) : Colors.Gray.WithAlpha(0.5);
            Color minorColor       = isDarkMode ? Colors.White.WithAlpha(0.5) : Colors.Gray.WithAlpha(0.4);
            Color tinyColor        = isDarkMode ? Colors.Gray.WithAlpha(0.5)  : Colors.Gray.WithAlpha(0.2);

            for (int i = 0; i < axis.Circles.Count; i++)
            {
                double dbValue = radii[i] + displayMinDb;
                axis.Circles[i].LabelStyle.ForeColor = textColor;

                if (string.IsNullOrEmpty(labels[i]))
                {
                    axis.Circles[i].LineWidth = 0.5f; axis.Circles[i].LineColor = tinyColor;
                }
                else
                {
                    bool isMajor        = Math.Abs(dbValue % 10) < 0.5 || Math.Abs(dbValue % 10) > 9.5;
                    bool isIntermediate = Math.Abs(dbValue % 5)  < 0.5 || Math.Abs(dbValue % 5)  > 4.5;

                    if (isMajor)             { axis.Circles[i].LineWidth = 2.0f; axis.Circles[i].LineColor = majorColor; }
                    else if (isIntermediate) { axis.Circles[i].LineWidth = 1.2f; axis.Circles[i].LineColor = intermediateColor; }
                    else                     { axis.Circles[i].LineWidth = 0.8f; axis.Circles[i].LineColor = minorColor; }
                }
            }

            foreach (var spoke in axis.Spokes)
            {
                spoke.LabelStyle.ForeColor = textColor;
                spoke.LineColor = Colors.Gray.WithAlpha(0.5);
            }
        }
    }
}
