using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml;

namespace FluentSetup.Builder;

public partial class App : Application
{
    private MainWindow? _window;

    /// <summary>The single view model shared by all pages.</summary>
    public static MainViewModel VM { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Keep the Builder alive on unexpected UI errors and tell the user what happened.
            e.Handled = true;
            _ = Services.Ui.MessageAsync(Services.Loc.Get("Error_UnexpectedTitle"), e.Exception?.Message ?? e.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        VM = new MainViewModel();
        _window = new MainWindow();
        _window.Activate();
        // fluentsetup-builder.exe <project.json> opens that project.
        var cmd = Environment.GetCommandLineArgs();
        if (cmd.Length > 1 && File.Exists(cmd[1])) VM.OpenPath(cmd[1]);
    }
}
