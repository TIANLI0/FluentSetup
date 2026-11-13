using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

/// <summary>More shortcuts, file types and URL schemes.</summary>
public sealed partial class ShortcutsPage : Page
{
    public MainViewModel VM => App.VM;
    public ShortcutsPage() => InitializeComponent();
}
