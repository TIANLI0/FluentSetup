using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

/// <summary>Values the user enters during setup (the installer's Settings page) and the config files they go into.</summary>
public sealed partial class InputsPage : Page
{
    public MainViewModel VM => App.VM;
    public InputsPage() => InitializeComponent();
}
