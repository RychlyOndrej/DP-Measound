using ScottPlot;
using ScottPlot.Panels;
using ScottPlot.WPF;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MeaSound
{
    internal static class Spectrogram
    {
        private static readonly int[] NiceTimeSteps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000 };

        private static readonly double[] FreqCandidates = {
            10, 12.5, 16, 20, 25, 31.5, 40, 50, 63, 80,
            100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
            1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000,
            10000, 12500, 16000, 20000, 25000, 31500, 40000
        };

        /// <summary>
        /// Applies plot colors for the current theme.
        /// </summary>
        private static void ApplyTheme(ScottPlot.Plot plot, bool isDarkMode)
        {
            if (plot == null) return;

            if (isDarkMode)
            {
                plot.FigureBackground.Color = Color.FromHex("#181818");
                plot.DataBackground.Color = Color.FromHex("#1f1f1f");
                plot.Axes.Color(Color.FromHex("#e8e8e8"));
                plot.Grid.MajorLineColor = Color.FromHex("#404040");
                plot.Legend.BackgroundColor = Color.FromHex("#404040");
                plot.Legend.FontColor = Color.FromHex("#e8e8e8");
                plot.Legend.OutlineColor = Color.FromHex("#e8e8e8");
            }
            else
            {
                plot.FigureBackground.Color = Colors.White;
                plot.DataBackground.Color = Colors.White;
                plot.Axes.Color(Colors.Black);
                plot.Grid.MajorLineColor = Color.FromHex("#efefef");
                plot.Legend.BackgroundColor = Colors.White;
                plot.Legend.FontColor = Colors.Black;
                plot.Legend.OutlineColor = Colors.Black;
            }
        }

        /// <summary>
        /// Sets zoom and pan handlers for dynamic tick updates.
        /// </summary>
        private static void SetupInteractiveHandlers(WpfPlot plot, Action updateTicksCallback)
        {
            if (plot == null || updateTicksCallback == null) return;

            // Zoom handler (mouse wheel)
            plot.MouseWheel += (_, __) =>
            {
                updateTicksCallback();
                plot.Refresh();
            };

            // Pan handler (mouse drag)
            plot.MouseMove += (_, __) =>
            {
                if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed ||
                    System.Windows.Input.Mouse.RightButton == System.Windows.Input.MouseButtonState.Pressed)
                {
                    updateTicksCallback();
                    plot.Refresh();
                }
            };
        }

        /// <summary>
        /// Hooks a custom context-menu action to open a spectrogram window.
        /// </summary>
        private static void SetupCustomContextMenu(WpfPlot plot, float[,] logDataFlipped, double[] times, double[] logFreqs)
        {
            if (plot?.ContextMenu == null) return;

            var items = plot.ContextMenu.Items;
            System.Windows.Controls.MenuItem? openInNewWindowItem = null;

            foreach (var item in items)
            {
                if (item is System.Windows.Controls.MenuItem menuItem &&
                    menuItem.Header?.ToString()?.Contains("New Window") == true)
                {
                    openInNewWindowItem = menuItem;
                    break;
                }
            }

            if (openInNewWindowItem != null)
            {
                openInNewWindowItem.Click += (s, e) =>
                {
                    e.Handled = true;
                    OpenSpectrogramInNewWindow(plot, logDataFlipped, times, logFreqs);
                };
            }
        }

        /// <summary>
        /// Opens the current spectrogram in a new window.
        /// </summary>
        private static void OpenSpectrogramInNewWindow(WpfPlot originalPlot, float[,] logDataFlipped, double[] times, double[] logFreqs)
        {
            try
            {
                var popupPlot = new WpfPlot();
                DrawHeatmap(popupPlot, logDataFlipped, times, logFreqs, isPopup: true);

                var window = new System.Windows.Window
                {
                    Title = "Spectrogram - Nové okno",
                    Width = 1000,
                    Height = 700,
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
                        {
                            itemsToRemove.Add(item);
                        }
                    }
                    foreach (var item in itemsToRemove)
                    {
                        popupPlot.ContextMenu.Items.Remove(item);
                    }
                }

                window.Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Spectrogram] Error opening in new window: {ex.Message}");
            }
        }

        public static void ShowAndSaveSpectrogram(
            (float[,] data, double[] times, double[] freqs) spectrogram,
            int sampleRate,
            int hopSize,
            string baseFolder,
            string fileName = "Spectrogram.png",
            WpfPlot wpfPlot = null)
        {
            var (data, times, freqs) = spectrogram;

            int frames = data.GetLength(0);
            int bins = data.GetLength(1);

            int logBins = 512;
            double fMin = Math.Max(freqs[0], 20.0);
            double fMax = Math.Max(freqs[bins - 1], fMin + 1.0);

            var (logData, logFreqs) = ConvertToLogDataFlipped(data, frames, bins, logBins, freqs, fMin, fMax);

            if (wpfPlot != null)
                DrawHeatmap(wpfPlot, logData, times, logFreqs);

            SaveFiles(logData, logFreqs, times, baseFolder, fileName, wpfPlot);
        }

        // freqs[] = actual Hz values for each linear bin (from ComputeSpectrogram).
        private static (float[,], double[]) ConvertToLogDataFlipped(
            float[,] data, int frames, int bins, int logBins,
            double[] freqs, double fMin, double fMax)
        {
            var logFreqs = Enumerable.Range(0, logBins)
                .Select(i => fMin * Math.Pow(fMax / fMin, i / (double)(logBins - 1)))
                .ToArray();

            var logData = new float[frames, logBins];
            double freqStep = bins > 1 ? (freqs[bins - 1] - freqs[0]) / (bins - 1) : 1.0;

            for (int t = 0; t < frames; t++)
            {
                for (int j = 0; j < logBins; j++)
                {
                    double pos = (logFreqs[j] - freqs[0]) / freqStep;
                    int idxLow = Math.Max(0, Math.Min(bins - 1, (int)pos));
                    int idxHigh = Math.Min(bins - 1, idxLow + 1);
                    float frac = (float)(pos - idxLow);
                    logData[t, j] = data[t, idxLow] * (1 - frac) + data[t, idxHigh] * frac;
                }
            }

            return (logData, logFreqs);
        }

        /// <summary>
        /// Reapplies spectrogram theme and colorbar text contrast to an already rendered plot.
        /// </summary>
        public static void ApplyThemeToExistingPlot(WpfPlot? plot)
        {
            if (plot == null) return;

            bool isDarkMode = ThemeManager.Instance.IsDarkMode;
            ApplyTheme(plot.Plot, isDarkMode);

            if (plot.Tag is ColorBar existingColorBar)
                ApplyColorBarContrast(existingColorBar, isDarkMode);

            plot.Refresh();

            if (plot.Tag is ColorBar colorBarAfterRender)
                ApplyColorBarContrast(colorBarAfterRender, isDarkMode);

            plot.Refresh();
        }

        private static void DrawHeatmap(WpfPlot plot, float[,] logData, double[] times, double[] logFreqs, bool isPopup = false)
        {
            if (!isPopup)
                plot.Reset();

            var plt = plot.Plot;
            plt.Clear();

            int frames = logData.GetLength(0);
            int bins = logData.GetLength(1);

            double logFMin = Math.Log10(Math.Max(logFreqs[0], 1.0));
            double logFMax = Math.Log10(Math.Max(logFreqs[bins - 1], 1.0));

            double[,] dataD = new double[bins, frames];
            for (int r = 0; r < bins; r++)
            {
                double logF = logFMax - (logFMax - logFMin) * r / (bins - 1);
                double targetHz = Math.Pow(10.0, logF);
                int srcBin = BinarySearchNearest(logFreqs, targetHz);
                for (int t = 0; t < frames; t++)
                    dataD[r, t] = logData[t, srcBin];
            }

            var hm = plt.Add.Heatmap(dataD);
            hm.Colormap = new ScottPlot.Colormaps.Turbo();
            var colorBar = plt.Add.ColorBar(hm);
            plot.Tag = colorBar;

            plt.Axes.Bottom.Label.Text = "Cas [s]";
            plt.Axes.Left.Label.Text = "Frekvence [Hz]";

            bool isDarkMode = ThemeManager.Instance.IsDarkMode;
            ApplyTheme(plt, isDarkMode);
            ApplyColorBarContrast(colorBar, isDarkMode);
            plt.Axes.Bottom.IsVisible = true;
            plt.Axes.Left.IsVisible = true;
            plt.Axes.SetLimits(left: 0, right: frames, bottom: bins, top: 0);

            double FreqToRow(double hz)
            {
                double rowCentre = (logFMax - Math.Log10(Math.Max(hz, 1.0)))
                                   / (logFMax - logFMin) * (bins - 1);
                return rowCentre + 0.5;
            }

            static string FreqLabel(double f)
            {
                if (f >= 1000) return $"{f / 1000:0.##}k";
                if (f >= 100) return $"{f:0}";
                return $"{f:0.#}";
            }

            void ApplyFreqTicks()
            {
                var limits = plt.Axes.GetLimits();
                double rowTop = limits.Top;
                double rowBottom = limits.Bottom;

                double Clamp01(double v) => Math.Max(0.0, Math.Min(1.0, v));
                double visHzTop = Math.Pow(10.0, logFMax - Clamp01(rowTop / (bins - 1)) * (logFMax - logFMin));
                double visHzBottom = Math.Pow(10.0, logFMax - Clamp01(rowBottom / (bins - 1)) * (logFMax - logFMin));
                double visHzLo = Math.Min(visHzTop, visHzBottom);
                double visHzHi = Math.Max(visHzTop, visHzBottom);

                var visible = FreqCandidates
                    .Where(f => f >= visHzLo * 0.98 && f <= visHzHi * 1.02)
                    .ToList();

                // Limit to 8 ticks by decimating every other candidate when needed.
                const int maxTicks = 8;
                while (visible.Count > maxTicks)
                {
                    var reduced = new List<double>();
                    for (int i = 0; i < visible.Count; i++)
                        if (i % 2 == 0) reduced.Add(visible[i]);
                    visible = reduced;
                }

                var positions = visible.Select(FreqToRow).ToList();
                var labels = visible.Select(FreqLabel).ToList();

                double fLo = logFreqs[0];
                double fHi = logFreqs[bins - 1];
                if (fLo >= visHzLo * 0.98 && fLo <= visHzHi * 1.02
                    && visible.All(f => Math.Abs(f - fLo) / fLo > 0.05))
                { positions.Add(FreqToRow(fLo)); labels.Add(FreqLabel(fLo)); }
                if (fHi >= visHzLo * 0.98 && fHi <= visHzHi * 1.02
                    && visible.All(f => Math.Abs(f - fHi) / fHi > 0.05))
                { positions.Add(FreqToRow(fHi)); labels.Add(FreqLabel(fHi)); }

                plt.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
                    positions.ToArray(), labels.ToArray());
            }

            void ApplyTimeTicks()
            {
                var limits = plt.Axes.GetLimits();
                double colLo = Math.Max(0, limits.Left);
                double colHi = Math.Min(frames, limits.Right);
                int iLo = (int)Math.Floor(colLo);
                int iHi = (int)Math.Ceiling(colHi);
                int span = Math.Max(1, iHi - iLo);

                int rawStep = Math.Max(1, span / 6);
                int step = NiceTimeSteps.FirstOrDefault(s => s >= rawStep, NiceTimeSteps[^1]);

                int first = (iLo / step) * step;
                var indices = Enumerable.Range(0, (iHi - first) / step + 2)
                    .Select(i => first + i * step)
                    .Where(i => i >= 0 && i < frames)
                    .ToArray();

                plt.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
                    indices.Select(i => (double)i).ToArray(),
                    indices.Select(i => times[i].ToString("F2")).ToArray());
            }

            ApplyFreqTicks();
            ApplyTimeTicks();

            SetupInteractiveHandlers(plot, () =>
            {
                ApplyFreqTicks();
                ApplyTimeTicks();
                bool runtimeIsDarkMode = ThemeManager.Instance.IsDarkMode;
                ApplyTheme(plt, runtimeIsDarkMode);
                ApplyColorBarContrast(colorBar, runtimeIsDarkMode);
            });

            if (!isPopup)
                SetupCustomContextMenu(plot, logData, times, logFreqs);

            plot.Refresh();
            ApplyColorBarContrast(colorBar, isDarkMode);
            plot.Refresh();
        }

        /// <summary>Returns the index in <paramref name="sortedArr"/> whose value is nearest to <paramref name="target"/>.</summary>
        private static int BinarySearchNearest(double[] sortedArr, double target)
        {
            int lo = 0, hi = sortedArr.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sortedArr[mid] < target) lo = mid + 1;
                else hi = mid;
            }
            if (lo > 0 && Math.Abs(sortedArr[lo - 1] - target) < Math.Abs(sortedArr[lo] - target))
                return lo - 1;
            return lo;
        }

        private static void SaveFiles(float[,] logData, double[] logFreqs, double[] times, string baseFolder, string fileName, WpfPlot wpfPlot = null)
        {
            bool baseIsSpectrogramFolder = string.Equals(Path.GetFileName(baseFolder?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "Spectrograms", StringComparison.OrdinalIgnoreCase);
            string folder = baseIsSpectrogramFolder ? baseFolder : Path.Combine(baseFolder, "Spectrograms");
            Directory.CreateDirectory(folder);
            string filePath = Path.Combine(folder, fileName);

            bool saveWhite = Preferences.Load().SaveChartsWithWhiteBackground;

            int frames = logData.GetLength(0);
            int logBins = logData.GetLength(1);

            double logFMin = Math.Log10(Math.Max(logFreqs[0], 1.0));
            double logFMax = Math.Log10(Math.Max(logFreqs[logBins - 1], 1.0));

            // Build dataD consistent with DrawHeatmap: [row=descFreq, col=time]
            // row 0 = highest frequency (top), row logBins-1 = lowest (bottom)
            double[,] dataD = new double[logBins, frames];
            for (int r = 0; r < logBins; r++)
            {
                double logF = logFMax - (logFMax - logFMin) * r / (logBins - 1);
                double targetHz = Math.Pow(10.0, logF);
                int srcBin = BinarySearchNearest(logFreqs, targetHz);
                for (int t = 0; t < frames; t++)
                    dataD[r, t] = logData[t, srcBin];
            }

            if (wpfPlot != null)
            {
                try
                {
                    var plt = wpfPlot.Plot;
                    bool runtimeIsDark = ThemeManager.Instance.IsDarkMode;
                    bool exportIsDark = !saveWhite;

                    ApplyTheme(plt, exportIsDark);
                    if (wpfPlot.Tag is ColorBar exportColorBar)
                        ApplyColorBarContrast(exportColorBar, exportIsDark);

                    wpfPlot.Refresh();
                    if (wpfPlot.Tag is ColorBar exportColorBarAfterRender)
                        ApplyColorBarContrast(exportColorBarAfterRender, exportIsDark);
                    wpfPlot.Refresh();

                    plt.SavePng(filePath, 1600, 1000);

                    try
                    {
                        string svgPath = Path.ChangeExtension(filePath, ".svg");
                        plt.SaveSvg(svgPath, 1600, 1000);
                    }
                    catch (Exception exSvg)
                    {
                        Console.WriteLine($"[Spectrogram] SVG export failed: {exSvg.Message}");
                    }

                    ApplyTheme(plt, runtimeIsDark);
                    if (wpfPlot.Tag is ColorBar runtimeColorBar)
                        ApplyColorBarContrast(runtimeColorBar, runtimeIsDark);
                    wpfPlot.Refresh();
                }
                catch
                {
                    SaveStandalonePlot(dataD, filePath, saveWhite);
                }
            }
            else
            {
                SaveStandalonePlot(dataD, filePath, saveWhite);
            }

            // CSV export: rows are time frames, columns are ascending log-frequency bins.
            string csvPath = Path.ChangeExtension(filePath, ".csv");
            using var sw = new StreamWriter(csvPath);
            sw.Write("Time [s]");
            foreach (var f in logFreqs)
                sw.Write($";{f:F1} Hz");
            sw.WriteLine();

            for (int t = 0; t < frames; t++)
            {
                sw.Write($"{times[t]:F3}");
                for (int j = 0; j < logBins; j++)
                    sw.Write($";{logData[t, j]:F1}");
                sw.WriteLine();
            }

            Console.WriteLine($"[SignalAnalyzer] Spectrogram ulozen: {filePath}, {csvPath}");
        }

        private static void SaveStandalonePlot(double[,] dataD, string filePath, bool forceWhite)
        {
            try
            {
                var plt = new ScottPlot.Plot();
                var hm = plt.Add.Heatmap(dataD);
                hm.Colormap = new ScottPlot.Colormaps.Turbo();
                if (forceWhite)
                    ScottPlotThemeHelper.ApplyTheme(plt, isDarkMode: false);
                plt.SavePng(filePath, 1600, 1000);

                try
                {
                    string svgPath = Path.ChangeExtension(filePath, ".svg");
                    plt.SaveSvg(svgPath, 1600, 1000);
                }
                catch { }
            }
            catch { }
        }

        private static void ApplyColorBarContrast(ColorBar? colorBar, bool isDarkMode)
        {
            if (colorBar == null) return;

            Color textColor = isDarkMode ? Colors.White : Colors.Black;

            SetNestedProperty(colorBar, "LabelStyle", "ForeColor", textColor);

            object? axis = GetPropertyValue(colorBar, "Axis");
            if (axis == null) return;

            SetNestedProperty(axis, "TickLabelStyle", "ForeColor", textColor);
            SetNestedProperty(axis, "LabelStyle", "ForeColor", textColor);
            SetNestedProperty(axis, "MajorTickStyle", "Color", textColor);
            SetNestedProperty(axis, "MinorTickStyle", "Color", textColor);
            SetNestedProperty(axis, "FrameLineStyle", "Color", textColor);

            TryInvokeAxisColor(axis, textColor);
        }

        private static object? GetPropertyValue(object target, string propertyName)
        {
            var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            return prop?.CanRead == true ? prop.GetValue(target) : null;
        }

        private static void SetNestedProperty(object target, string nestedPropertyName, string leafPropertyName, object value)
        {
            object? nested = GetPropertyValue(target, nestedPropertyName);
            if (nested == null) return;

            var leaf = nested.GetType().GetProperty(leafPropertyName, BindingFlags.Instance | BindingFlags.Public);
            if (leaf?.CanWrite == true)
                leaf.SetValue(nested, value);
        }

        private static void TryInvokeAxisColor(object axis, Color textColor)
        {
            var method = axis.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m =>
                {
                    if (!string.Equals(m.Name, "Color", StringComparison.Ordinal))
                        return false;

                    var parameters = m.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(Color);
                });

            method?.Invoke(axis, new object[] { textColor });
        }
    }
}