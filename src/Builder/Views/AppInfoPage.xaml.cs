using FluentSetup.Builder.Services;
using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

public sealed partial class AppInfoPage : Page
{
    public MainViewModel VM => App.VM;
    public string IconDescription => string.IsNullOrEmpty(VM.Icon) ? Loc.Get("AppIcon_Default") : VM.Icon;
    public string LicenseDescription => string.IsNullOrEmpty(VM.License) ? Loc.Get("AppLicense_None") : VM.License;

    public AppInfoPage()
    {
        InitializeComponent();
        VM.PropertyChanged += OnVmChanged;
        Unloaded += (_, _) => VM.PropertyChanged -= OnVmChanged;
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Icon) or nameof(MainViewModel.License)) Bindings.Update();
    }

    private void OnClearIcon(object sender, RoutedEventArgs e) => VM.Icon = "";
    private void OnClearLicense(object sender, RoutedEventArgs e) => VM.License = "";
}
