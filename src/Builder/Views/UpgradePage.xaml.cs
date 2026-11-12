using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

public sealed partial class UpgradePage : Page
{
    public MainViewModel VM => App.VM;
    public UpgradePage() => InitializeComponent();
}
