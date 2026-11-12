using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

/// <summary>Editor for a prerequisite source (bundled installer, arguments, download, help link, on failure, variants).</summary>
public sealed partial class PrerequisiteSourceView : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(PrerequisiteSourceEditor), typeof(PrerequisiteSourceView),
        new PropertyMetadata(null, (d, _) => ((PrerequisiteSourceView)d).Bindings.Update()));

    public MainViewModel VM => App.VM;

    public PrerequisiteSourceEditor? Source
    {
        get => (PrerequisiteSourceEditor?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public PrerequisiteSourceView() => InitializeComponent();
}
