using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Btl.Whiteboard.Client;

public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainAppWindow = new MainWindow();

        try
        {
            MainAppWindow.SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
        }

        MainAppWindow.Activate();
    }
}
