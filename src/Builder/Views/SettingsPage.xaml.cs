using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

public sealed partial class SettingsPage : Page
{
    public MainViewModel VM => App.VM;
    public string AboutText => $"FluentSetup Builder {VM.AppVersionText} · .NET {Environment.Version} · Windows App SDK 2.5";
    public SettingsPage() => InitializeComponent();
}
