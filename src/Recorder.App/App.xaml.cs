using Microsoft.UI.Xaml;
namespace Recorder.App;
public partial class App : Application
{
    private MainWindow? window;
    public App() { InitializeComponent(); }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        bool smoke = Environment.GetCommandLineArgs().Contains("--smoke-test");
        if (!smoke)
        {
            var instance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("SherpaRecorder.WinUI.App.v1");
            if (!instance.IsCurrent)
            {
                await instance.RedirectActivationToAsync(Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs());
                Exit(); return;
            }
            instance.Activated += (_, _) => window?.ShowWindow();
        }
        window = new MainWindow(smoke);
        window.Activate();
    }
}
