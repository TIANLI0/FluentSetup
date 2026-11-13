using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

/// <summary>Registry values, environment variables, services and firewall rules.</summary>
public sealed partial class SystemPage : Page
{
    public MainViewModel VM => App.VM;
    public SystemPage() => InitializeComponent();
}
