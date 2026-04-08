using System;
using System.Linq;
using System.Windows;

namespace MeaSound
{
    public class ThemeManager
    {
        private static ThemeManager? _instance;
        private bool _isDarkMode = false;

        public static ThemeManager Instance => _instance ??= new ThemeManager();

        public bool IsDarkMode
        {
            get => _isDarkMode;
            private set => _isDarkMode = value;
        }

        public event Action? OnThemeChanged;

        private Preferences _prefs;

        public ThemeManager()
        {
            _prefs = Preferences.Load();
            IsDarkMode = _prefs.UseDarkTheme;
        }

        public void SetDarkMode(bool dark)
        {
            IsDarkMode = dark;
            ApplyTheme();
            _prefs.UseDarkTheme = dark;
            _prefs.Save();
            OnThemeChanged?.Invoke();
        }

        public void ToggleTheme() => SetDarkMode(!IsDarkMode);

        private void ApplyTheme()
        {
            if (Application.Current == null) return;
            var app = Application.Current;

            for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                var md = app.Resources.MergedDictionaries[i];
                if (md.Source == null) continue;
                var s = md.Source.OriginalString ?? string.Empty;
                if (s.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)
                    || s.Contains("MahApps.Metro;component/Styles/Themes/"))
                    app.Resources.MergedDictionaries.RemoveAt(i);
            }

            try
            {
                string mahTheme = IsDarkMode
                    ? "pack://application:,,,/MahApps.Metro;component/Styles/Themes/Dark.Blue.xaml"
                    : "pack://application:,,,/MahApps.Metro;component/Styles/Themes/Light.Blue.xaml";
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(mahTheme, UriKind.Absolute) });
            }
            catch { }

            string themePath = IsDarkMode ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(themePath, UriKind.Relative) });
        }

        public void Initialize() => ApplyTheme();
    }
}
