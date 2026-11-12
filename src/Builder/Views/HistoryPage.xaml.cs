using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

public sealed partial class HistoryPage : Page
{
    public MainViewModel VM => App.VM;
    public HistoryPage() => InitializeComponent();

    private void OnInspect(object sender, RoutedEventArgs e) => VM.InspectCommand.Execute(((FrameworkElement)sender).Tag as HistoryRow);
    private void OnOpenFolder(object sender, RoutedEventArgs e) => VM.OpenHistoryFolderCommand.Execute(((FrameworkElement)sender).Tag as HistoryRow);
}
