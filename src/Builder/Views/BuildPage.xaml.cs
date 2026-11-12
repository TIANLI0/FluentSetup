using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

public sealed partial class BuildPage : Page
{
    public MainViewModel VM => App.VM;
    public Visibility LogEmpty => VM.BuildLog.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public BuildPage()
    {
        InitializeComponent();
        VM.BuildLog.CollectionChanged += OnLogChanged;
        Unloaded += (_, _) => VM.BuildLog.CollectionChanged -= OnLogChanged;
    }

    private void OnLogChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        Bindings.Update();
        if (VM.BuildLog.Count > 0) LogList.ScrollIntoView(VM.BuildLog[^1]);
    }
}
