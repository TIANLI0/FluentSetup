using FluentSetup.Builder.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

/// <summary>
/// A version field with Microsoft's versions of the prerequisite as suggestions (<see cref="VersionCatalogViewModel"/>):
/// the chevron lists them all, typing filters them, and any other valid version can be typed.
/// </summary>
public sealed partial class VersionBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(VersionBox), new PropertyMetadata("", (d, e) => ((VersionBox)d).ShowText(e.NewValue as string)));

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(object), typeof(VersionBox), new PropertyMetadata(null, (d, e) => ((VersionBox)d).Box.Header = e.NewValue));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(VersionBox), new PropertyMetadata("", (d, e) => ((VersionBox)d).Box.PlaceholderText = e.NewValue as string ?? ""));

    public static readonly DependencyProperty CatalogProperty = DependencyProperty.Register(
        nameof(Catalog), typeof(VersionCatalogViewModel), typeof(VersionBox), new PropertyMetadata(null, (d, _) => ((VersionBox)d).OnCatalogChanged()));

    public VersionBox()
    {
        InitializeComponent();
        Loaded += (_, _) => Catalog?.EnsureLoaded();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public VersionCatalogViewModel? Catalog
    {
        get => (VersionCatalogViewModel?)GetValue(CatalogProperty);
        set => SetValue(CatalogProperty, value);
    }

    private void OnCatalogChanged()
    {
        Bindings.Update();
        if (IsLoaded) Catalog?.EnsureLoaded();
    }

    private void ShowText(string? value)
    {
        if (Box.Text != (value ?? "")) Box.Text = value ?? "";
    }

    private void OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange) return;
        if (Text != sender.Text) Text = sender.Text;
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) ShowSuggestions(sender.Text);
    }

    /// <summary>An empty field lists every version as soon as it gets focus.</summary>
    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (Box.Text.Length == 0 && !Box.IsSuggestionListOpen) ShowSuggestions("");
    }

    /// <summary>The chevron (or Enter) lists every version; again closes the list.</summary>
    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is not null) return;
        if (sender.IsSuggestionListOpen) sender.IsSuggestionListOpen = false;
        else ShowSuggestions("");
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not VersionSuggestion s) return;
        sender.Text = s.Version;
        Text = s.Version;
    }

    private void ShowSuggestions(string filter)
    {
        var needle = filter.Trim();
        var all = Catalog?.Items ?? [];
        var list = all.Where(v => needle.Length == 0 || v.Version.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ||
                                  v.Detail.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        // A version that is already complete still shows the others to switch to.
        if (list.Count == 1 && list[0].Version == needle) list = [.. all];
        Box.ItemsSource = list;
        Box.IsSuggestionListOpen = list.Count > 0;
    }
}
