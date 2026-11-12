using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

public sealed partial class UninstallPage : Page
{
    public MainViewModel VM => App.VM;
    public UninstallPage() => InitializeComponent();
}
