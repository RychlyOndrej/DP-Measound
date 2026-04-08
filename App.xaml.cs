using System.Windows;

namespace MeaSound
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            ThemeManager.Instance.Initialize();
        }
    }
}
