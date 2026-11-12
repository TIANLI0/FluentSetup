using FluentSetup.Builder.Services;
using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

public sealed partial class OverviewPage : Page
{
    public MainViewModel VM => App.VM;
    public IEnumerable<HistoryRow> RecentBuilds => VM.History.Take(5);
    public Microsoft.UI.Xaml.Visibility NoRecent => VM.RecentProjects.Count == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public OverviewPage()
    {
        InitializeComponent();
        VM.History.CollectionChanged += (_, _) => Bindings.Update();
        VM.RecentProjects.CollectionChanged += (_, _) => Bindings.Update();
    }

    private static void Go(string tag) => (Ui.Window as MainWindow)?.Navigate(tag);

    private void OnRowClick(object sender, RoutedEventArgs e) => Go((string)((FrameworkElement)sender).Tag);
    private void OnProblemClick(object sender, RoutedEventArgs e) => Go((string)((FrameworkElement)sender).Tag);
    private void OnRecentClick(object sender, ItemClickEventArgs e) => VM.OpenRecentCommand.Execute(e.ClickedItem as string);

    private void OnBuildClick(object sender, RoutedEventArgs e)
    {
        Go("build");
        VM.BuildCommand.Execute(null);
    }
}
