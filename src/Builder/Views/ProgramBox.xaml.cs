using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

/// <summary>Relative path of a program among the installed files: free text plus the collected .exe files as suggestions.</summary>
public sealed partial class ProgramBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ProgramBox), new PropertyMetadata("", (d, e) => ((ProgramBox)d).ShowText(e.NewValue as string)));

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(object), typeof(ProgramBox), new PropertyMetadata(null, (d, e) => ((ProgramBox)d).Box.Header = e.NewValue));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(ProgramBox), new PropertyMetadata("", (d, e) => ((ProgramBox)d).Box.PlaceholderText = e.NewValue as string ?? ""));

    public static readonly DependencyProperty ChoicesProperty = DependencyProperty.Register(
        nameof(Choices), typeof(IEnumerable<string>), typeof(ProgramBox), new PropertyMetadata(null));

    public ProgramBox() => InitializeComponent();

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

    public IEnumerable<string>? Choices
    {
        get => (IEnumerable<string>?)GetValue(ChoicesProperty);
        set => SetValue(ChoicesProperty, value);
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

    /// <summary>The chevron (or Enter) lists every program; again closes the list.</summary>
    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is not null) return;
        if (sender.IsSuggestionListOpen) sender.IsSuggestionListOpen = false;
        else ShowSuggestions("");
    }

    private void ShowSuggestions(string filter)
    {
        var needle = filter.Trim().Replace('/', '\\');
        var list = (Choices ?? []).Where(c => needle.Length == 0 || c.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        Box.ItemsSource = list;
        Box.IsSuggestionListOpen = list.Count > 0;
    }
}
