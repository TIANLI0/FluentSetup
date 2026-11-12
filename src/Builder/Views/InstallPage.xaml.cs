using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

public sealed partial class InstallPage : Page
{
    public MainViewModel VM => App.VM;
    public Visibility IsEither => VM.ScopeIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShowUserDir => VM.ScopeIndex != 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShowMachineDir => VM.ScopeIndex != 0 ? Visibility.Visible : Visibility.Collapsed;

    public InstallPage()
    {
        InitializeComponent();
        VM.PropertyChanged += OnVmChanged;
        Unloaded += (_, _) => VM.PropertyChanged -= OnVmChanged;
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ScopeIndex)) Bindings.Update();
    }
}
