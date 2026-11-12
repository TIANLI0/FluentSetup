using System.Diagnostics;
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

        var exes = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>()
                       .Where(n => !n.Contains("unins", StringComparison.OrdinalIgnoreCase) && !n.Contains("setup", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        foreach (var e in exes) _exe.Items.Add(e);
        // Best guess for the main program: the one named like the folder, else the largest.
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var guess = exes.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e).Equals(folderName, StringComparison.OrdinalIgnoreCase))
                    ?? exes.OrderByDescending(e => new FileInfo(Path.Combine(folder, e)).Length).FirstOrDefault();
        if (guess is not null) _exe.SelectedItem = guess;

        string name = folderName, publisher = "", version = "1.0.0";
        if (guess is not null)
        {
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(folder, guess));
            if (!string.IsNullOrWhiteSpace(info.ProductName)) name = info.ProductName.Trim();
            if (!string.IsNullOrWhiteSpace(info.CompanyName)) publisher = info.CompanyName.Trim();
            var v = (info.ProductVersion ?? "").Split('+')[0].Trim();
            if (ProjectValidator.IsValidVersion(v)) version = v;
            else if (info.FileMajorPart > 0 || info.FileMinorPart > 0) version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
        _name.Text = name;
        _publisher.Text = publisher;
        _version.Text = version;
        _id.Text = MakeId(publisher, name);

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

    private static string MakeId(string publisher, string name)
    {
        static string Clean(string s) => string.Concat(s.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        var parts = new[] { Clean(publisher), Clean(name) }.Where(p => p.Length > 0);
        var id = string.Join('.', parts);
        return id.Length > 64 ? id[..64] : id;
    }

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
