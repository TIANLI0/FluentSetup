using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;
using FluentSetup.Builder.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.Views;

/// <summary>
/// "New project": fields are pre-filled from the main executable's version resource found in the chosen folder,
/// so a typical app needs nothing but a confirmation.
/// </summary>
public sealed class NewProjectDialog : ContentDialog
{
    private readonly TextBox _name = new();
    private readonly TextBox _id = new();
    private readonly TextBox _publisher = new();
    private readonly TextBox _version = new();
    private readonly ComboBox _exe = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _error = new() { Style = (Style)Application.Current.Resources["FieldErrorStyle"] };
    private bool _idEdited;

    public string AppNameValue => _name.Text.Trim();
    public string AppIdValue => _id.Text.Trim();
    public string PublisherValue => _publisher.Text.Trim();
    public string VersionValue => _version.Text.Trim();
    public string MainExecutableValue => _exe.SelectedItem as string ?? "";

    public NewProjectDialog(string folder)
    {
        Title = Loc.Get("NewProject_Title");
        PrimaryButtonText = Loc.Get("NewProject_Create");
        CloseButtonText = Loc.Get("Common_Cancel");
        DefaultButton = ContentDialogButton.Primary;

        // Same guesses as "fluentsetup init": the main program and its version resource.
        var guess = ProjectGuess.FromFolder(folder);
        foreach (var e in guess.Executables) _exe.Items.Add(e);
        if (guess.MainExecutable.Length > 0) _exe.SelectedItem = guess.MainExecutable;
        _name.Text = guess.Name;
        _publisher.Text = guess.Publisher;
        _version.Text = guess.Version;
        _id.Text = guess.Id;

        _name.Header = Loc.Get("NewProject_Name");
        _id.Header = Loc.Get("NewProject_Id");
        _publisher.Header = Loc.Get("NewProject_Publisher");
        _version.Header = Loc.Get("NewProject_Version");
        _exe.Header = Loc.Get("NewProject_MainExe");
        _name.TextChanged += (_, _) => { if (!_idEdited) _id.Text = MakeId(_publisher.Text, _name.Text); Validate(); };
        _publisher.TextChanged += (_, _) => { if (!_idEdited) _id.Text = MakeId(_publisher.Text, _name.Text); };
        _id.TextChanged += (_, _) => { if (_id.FocusState != FocusState.Unfocused) _idEdited = true; Validate(); };
        _version.TextChanged += (_, _) => Validate();

        var source = new TextBlock
        {
            Text = Loc.Format("NewProject_Source", folder),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["RowDescriptionStyle"],
        };
        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };
        panel.Children.Add(source);
        panel.Children.Add(_name);
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.Children.Add(_publisher);
        Grid.SetColumn(_version, 1);
        row.Children.Add(_version);
        panel.Children.Add(row);
        panel.Children.Add(_id);
        panel.Children.Add(_exe);
        panel.Children.Add(_error);
        Content = panel;
        Validate();
    }

    private static string MakeId(string publisher, string name) => ProjectGuess.MakeId(publisher, name);

    private void Validate()
    {
        string error = "";
        if (string.IsNullOrWhiteSpace(_name.Text)) error = Loc.Get("NewProject_NameRequired");
        else if (!PathRules.IsValidAppId(_id.Text.Trim())) error = Loc.Get("NewProject_IdInvalid");
        else if (!ProjectValidator.IsValidVersion(_version.Text.Trim())) error = Loc.Get("NewProject_VersionInvalid");
        _error.Text = error;
        _error.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = error.Length == 0;
    }
}
