using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

public sealed partial class FilesPage : Page
{
    public MainViewModel VM => App.VM;

    public FilesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = VM.RefreshFilesAsync();
    }

    private void OnRemoveExtra(object sender, RoutedEventArgs e) => VM.RemoveExtraSourceCommand.Execute(((FrameworkElement)sender).Tag as ExtraSourceRow);
}

/// <summary>x:Bind function helpers.</summary>
public static class Converters
{
    public static InfoBarSeverity ToSeverity(string severity) => severity switch
    {
        "Error" => InfoBarSeverity.Error,
        "Warning" => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational,
    };

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfNot(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfImage(Microsoft.UI.Xaml.Media.ImageSource? value) => value is null ? Visibility.Collapsed : Visibility.Visible;
}
