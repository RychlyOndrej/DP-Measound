using ScottPlot;
using ScottPlot.WPF;

namespace MeaSound
{
    internal static class ScottPlotThemeHelper
    {
        public static void ApplyTheme(WpfPlot wpfPlot, bool isDarkMode)
        {
            if (wpfPlot == null) return;
            ApplyTheme(wpfPlot.Plot, isDarkMode);
            wpfPlot.Refresh();
        }

        public static void ApplyTheme(ScottPlot.Plot plot, bool isDarkMode)
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
    }
}
