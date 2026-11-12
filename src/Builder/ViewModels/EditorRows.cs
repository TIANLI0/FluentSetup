using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;
using FluentSetup.Builder.Services;

namespace FluentSetup.Builder.ViewModels;

/// <summary>
/// Text forms of project lists and per-language tables for the editors. Text the user did not change gives back the
/// original value, so opening and saving a project never rewrites what was not touched (blank or padded entries such as
/// "" in install.addToPath, unknown enum values, escaping).
/// </summary>
public static class TextForms
{
    public static string Lines(IEnumerable<string>? items) => items is null ? "" : string.Join(Environment.NewLine, items);

    public static List<string> SplitLines(string? text) =>
        (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Normalize(string? s) => (s ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');

    /// <summary>Same text, ignoring how a TextBox stores line breaks.</summary>
    public static bool Same(string? text, string original) => Normalize(text) == Normalize(original);

    /// <summary>One item per line.</summary>
    public static List<string> FromLines(string? text, List<string>? original) =>
        original is not null && Same(text, Lines(original)) ? [.. original] : SplitLines(text);

    /// <summary>Items separated by commas (ids and SDDL rights never contain them).</summary>
    public static string Csv(IEnumerable<string>? items) => items is null ? "" : string.Join(", ", items);

    public static List<string> FromCsv(string? text, List<string>? original) =>
        original is not null && (text ?? "").Trim() == Csv(original)
            ? [.. original]
            : (text ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string Ints(IEnumerable<int>? values) =>
        values is null ? "" : string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Whole numbers separated by commas or spaces; <paramref name="valid"/> is false if a part is not one.</summary>
    public static List<int> ParseInts(string? text, out bool valid)
    {
        valid = true;
        var result = new List<int>();
        foreach (var part in (text ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) result.Add(n);
            else valid = false;
        }
        return result;
    }

    // Per-language texts: one "en-US = text" line each. Line breaks inside a text are shown as \n.
    private static string Escape(string? v) => (v ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    private static string Unescape(string v) => v.Replace("\\n", "\n", StringComparison.Ordinal);

    public static string Localized(Dictionary<string, string>? texts) =>
        texts is null ? "" : string.Join(Environment.NewLine, texts.Select(kv => $"{kv.Key} = {Escape(kv.Value)}"));

    /// <summary>A line without "=" is taken as English text.</summary>
    public static Dictionary<string, string> FromLocalized(string? text, Dictionary<string, string>? original)
    {
        if (original is not null && Same(text, Localized(original))) return new(original);
        var result = new Dictionary<string, string>();
        foreach (var line in SplitLines(text))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            var (lang, value) = eq < 0 ? ("en-US", line) : (line[..eq].Trim(), line[(eq + 1)..].Trim());
            if (lang.Length > 0) result[lang] = Unescape(value);
        }
        return result;
    }

    /// <summary>The first text of a per-language table, for headers.</summary>
    public static string FirstText(string? localizedText)
    {
        var first = FromLocalized(localizedText, null).FirstOrDefault();
        return first.Value ?? "";
    }

    // ui.strings: one "zh-CN: welcome.body = text" line per override.
    public static string Strings(Dictionary<string, Dictionary<string, string>>? strings) =>
        strings is null ? "" : string.Join(Environment.NewLine, strings.SelectMany(lang => (lang.Value ?? []).Select(kv => $"{lang.Key}: {kv.Key} = {Escape(kv.Value)}")));

    public static Dictionary<string, Dictionary<string, string>> FromStrings(string? text, Dictionary<string, Dictionary<string, string>>? original)
    {
        if (original is not null && Same(text, Strings(original))) return original.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value ?? []));
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var line in SplitLines(text))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (colon <= 0 || eq < colon) continue;
            var lang = line[..colon].Trim();
            var key = line[(colon + 1)..eq].Trim();
            if (lang.Length == 0 || key.Length == 0) continue;
            if (!result.TryGetValue(lang, out var table)) result[lang] = table = [];
            table[key] = Unescape(line[(eq + 1)..].Trim());
        }
        return result;
    }

    /// <summary>The original string when unchanged (also when it has surrounding spaces, or is null), else the trimmed text.</summary>
    public static string Keep(string? text, string? original) => (text ?? "") == (original ?? "") ? original! : (text ?? "").Trim();

    public static int IndexOf(string[] values, string? value, int fallback)
    {
        var i = Array.IndexOf(values, value);
        return i < 0 ? fallback : i;
    }

    /// <summary>Index of a stored value among the choices; -1 (nothing selected) for a value the choices do not know.</summary>
    public static int Find(string[] values, string? value) => value is null ? -1 : Array.IndexOf(values, value);

    /// <summary>The selected value; with nothing selected (an unknown stored value) the stored value is kept.</summary>
    public static string Pick(string[] values, int index, string? original) =>
        index >= 0 && index < values.Length ? values[index] : original!;

    /// <summary>Deep copy through the project serializer.</summary>
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProjectSerializer.Options), ProjectSerializer.Options)!;

    public static int ToInt(double value, int fallback) =>
        double.IsNaN(value) ? fallback : (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);
}

/// <summary>An item of an editable list (option, action, runtime, …). Edits mark the project dirty and revalidate.</summary>
public abstract partial class EditorRow : ObservableObject
{
    /// <summary>Set once the row is filled; from then on edits are reported to the view model.</summary>
    public MainViewModel? Owner { get; set; }

    /// <summary>Validation errors of this item (set by the view model).</summary>
    [ObservableProperty] public partial string Error { get; set; } = "";

    /// <summary>Whether the item's card is open (new items start open).</summary>
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>Properties that only reflect state in the UI and never change the project.</summary>
    protected virtual bool IsViewState(string name) => name is nameof(Error) or nameof(IsExpanded);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        var name = e.PropertyName ?? "";
        if (Owner is null || name.Length == 0 || IsViewState(name)) return;
        if (GetType().GetProperty(name) is { CanWrite: true }) Owner.OnRowChanged(this, name);
    }

    [RelayCommand] private void Remove() => Owner?.RemoveRow(this);
    [RelayCommand] private void MoveUp() => Owner?.MoveRow(this, -1);
    [RelayCommand] private void MoveDown() => Owner?.MoveRow(this, 1);
}

/// <summary>Where setup gets a missing prerequisite: a bundled installer or a verified download (requirements.*).</summary>
public partial class PrerequisiteSourceEditor : EditorRow
{
    internal static readonly string[] Failures = ["warn", "fail"];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SourceSummary))] public partial string Installer { get; set; } = "";
    [ObservableProperty] public partial string ArgumentsText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SourceSummary))] public partial bool HasDownload { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SourceSummary))] public partial string DownloadUrl { get; set; } = "";
    [ObservableProperty] public partial string DownloadSha256 { get; set; } = "";
    [ObservableProperty] public partial double DownloadSize { get; set; }
    [ObservableProperty] public partial string HelpUrl { get; set; } = "";
    [ObservableProperty] public partial int OnFailureIndex { get; set; }
    [ObservableProperty] public partial string VariantsText { get; set; } = "";
    [ObservableProperty] public partial string ComputeStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsComputing { get; set; }

    protected override bool IsViewState(string name) => base.IsViewState(name) || name is nameof(ComputeStatus) or nameof(IsComputing);

    public ObservableCollection<string>? ExecutableChoices => Owner?.ExecutableChoices;

    /// <summary>"Bundled: redist\x.exe", "Download: host" or "Not installed by setup".</summary>
    public string SourceSummary =>
        Installer.Trim().Length > 0 ? Loc.Format("Source_SummaryBundled", Installer.Trim())
        : HasDownload && Uri.TryCreate(DownloadUrl.Trim(), UriKind.Absolute, out var u) ? Loc.Format("Source_SummaryDownload", u.Host)
        : Loc.Get("Source_SummaryNone");

    public void LoadSource(PrerequisiteSource s)
    {
        Installer = s.Installer ?? "";
        ArgumentsText = TextForms.Lines(s.Arguments);
        HasDownload = s.Download is not null;
        DownloadUrl = s.Download?.Url ?? "";
        DownloadSha256 = s.Download?.Sha256 ?? "";
        DownloadSize = s.Download?.Size ?? 0;
        HelpUrl = s.HelpUrl ?? "";
        OnFailureIndex = TextForms.Find(Failures, s.OnFailure);
        VariantsText = TextForms.Csv(s.Variants);
    }

    /// <summary>Writes the editor into <paramref name="target"/>, which holds the values it was loaded from.</summary>
    public void ApplySource(PrerequisiteSource target)
    {
        target.Installer = TextForms.Keep(Installer, target.Installer);
        target.Arguments = TextForms.FromLines(ArgumentsText, target.Arguments);
        target.Download = HasDownload
            ? new DownloadSettings
            {
                Url = TextForms.Keep(DownloadUrl, target.Download?.Url),
                Sha256 = TextForms.Keep(DownloadSha256, target.Download?.Sha256),
                Size = double.IsNaN(DownloadSize) ? 0 : (long)Math.Round(DownloadSize),
            }
            : null;
        target.HelpUrl = TextForms.Keep(HelpUrl, target.HelpUrl);
        target.OnFailure = TextForms.Pick(Failures, OnFailureIndex, target.OnFailure);
        target.Variants = TextForms.FromCsv(VariantsText, target.Variants);
    }

    /// <summary>Downloads the file (https only) to fill in its SHA-256 and size.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ComputeDownloadAsync(CancellationToken token)
    {
        if (!Uri.TryCreate(DownloadUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            ComputeStatus = Loc.Get("Download_NeedHttps");
            return;
        }
        ComputeStatus = Loc.Get("Download_Starting");
        IsComputing = true;
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (!IsComputing) return;  // a late report after the end
            ComputeStatus = p.Total is > 0
                ? Loc.Format("Download_ProgressPercent", (int)(p.Done * 100 / p.Total.Value), PackageBuilder.Format(p.Done), PackageBuilder.Format(p.Total.Value))
                : Loc.Format("Download_Progress", PackageBuilder.Format(p.Done));
        });
        try
        {
            var (sha256, size) = await DownloadHasher.ComputeAsync(uri, progress, token);
            DownloadSha256 = sha256;
            DownloadSize = size;
            ComputeStatus = Loc.Format("Download_Done", PackageBuilder.Format(size));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ComputeStatus = Loc.Get("Download_Cancelled");
        }
        catch (Exception ex)
        {
            ComputeStatus = Loc.Format("Download_Failed", ex.Message);
        }
        finally
        {
            IsComputing = false;
        }
    }
}

/// <summary>requirements.dotNet[]: a .NET shared runtime.</summary>
public sealed partial class DotNetRow : PrerequisiteSourceEditor
{
    internal static readonly string[] Runtimes = ["desktop", "core", "aspnetcore"];
    internal static readonly string[] RollForwards = ["patch", "minor", "major"];
    private DotNetRequirement _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial int RuntimeIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Version { get; set; } = "";
    [ObservableProperty] public partial int RollForwardIndex { get; set; } = 1;

    public string Title => $"{(RuntimeIndex is >= 0 and < 3 ? ExtensionValidator.DotNetRuntime(Runtimes[RuntimeIndex]).Label : _original.Runtime)} {Version.Trim()}".Trim();

    /// <summary>"desktop" and "Microsoft.WindowsDesktop.App" both select the Desktop Runtime.</summary>
    private static int RuntimeIndexOf(string? value)
    {
        var full = ExtensionValidator.DotNetRuntime(value ?? "").Runtime;
        var i = Array.FindIndex(Runtimes, r => ExtensionValidator.DotNetRuntime(r).Runtime == full);
        return full.Length == 0 ? -1 : i;
    }

    public static DotNetRow From(DotNetRequirement r)
    {
        var row = new DotNetRow { _original = r };
        row.RuntimeIndex = RuntimeIndexOf(r.Runtime);
        row.Version = r.Version ?? "";
        row.RollForwardIndex = TextForms.Find(RollForwards, r.RollForward);
        row.LoadSource(r);
        return row;
    }

    public DotNetRequirement ToModel()
    {
        var r = TextForms.Copy(_original);
        // "Microsoft.WindowsDesktop.App" stays as written while "desktop" stays selected.
        r.Runtime = RuntimeIndex == RuntimeIndexOf(_original.Runtime) ? _original.Runtime : TextForms.Pick(Runtimes, RuntimeIndex, _original.Runtime);
        r.Version = TextForms.Keep(Version, _original.Version);
        r.RollForward = TextForms.Pick(RollForwards, RollForwardIndex, _original.RollForward);
        ApplySource(r);
        return r;
    }
}

/// <summary>options[]: a checkbox on the installer's first page.</summary>
public sealed partial class OptionRow : EditorRow
{
    private UserOptionSettings _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Id { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string LabelText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial bool Default { get; set; }
    /// <summary>0 = like the default, 1 = selected, 2 = not selected.</summary>
    [ObservableProperty] public partial int QuietDefaultIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial bool Required { get; set; }
    [ObservableProperty] public partial bool HasStartup { get; set; }
    [ObservableProperty] public partial string StartupName { get; set; } = "";
    [ObservableProperty] public partial string StartupArgumentsText { get; set; } = "";
    [ObservableProperty] public partial bool HasDetect { get; set; }
    [ObservableProperty] public partial string DetectRunValue { get; set; } = "";
    [ObservableProperty] public partial string DetectScheduledTask { get; set; } = "";

    public string Title => Id.Trim().Length > 0 ? Id.Trim() : Loc.Get("Options_Untitled");

    public string Subtitle
    {
        get
        {
            var state = Required ? Loc.Get("Options_StateRequired") : Default ? Loc.Get("Options_StateOn") : Loc.Get("Options_StateOff");
            var label = TextForms.FirstText(LabelText);
            return label.Length == 0 ? state : $"{label} · {state}";
        }
    }

    partial void OnIdChanged(string oldValue, string newValue) => Owner?.OnOptionRenamed(oldValue, newValue);

    public static OptionRow From(UserOptionSettings o) => new()
    {
        _original = o,
        Id = o.Id ?? "",
        LabelText = TextForms.Localized(o.Label),
        Default = o.Default,
        QuietDefaultIndex = o.QuietDefault switch { true => 1, false => 2, null => 0 },
        Required = o.Required,
        HasStartup = o.Startup is not null,
        StartupName = o.Startup?.Name ?? "",
        StartupArgumentsText = TextForms.Lines(o.Startup?.Arguments),
        HasDetect = o.Detect is not null,
        DetectRunValue = o.Detect?.RunValue ?? "",
        DetectScheduledTask = o.Detect?.ScheduledTask ?? "",
    };

    public UserOptionSettings ToModel()
    {
        var o = _original;
        return new UserOptionSettings
        {
            Id = TextForms.Keep(Id, o.Id),
            Label = TextForms.FromLocalized(LabelText, o.Label),
            Default = Default,
            QuietDefault = QuietDefaultIndex switch { 1 => true, 2 => false, _ => null },
            Required = Required,
            Startup = HasStartup
                ? new RunValueSettings { Name = TextForms.Keep(StartupName, o.Startup?.Name), Arguments = TextForms.FromLines(StartupArgumentsText, o.Startup?.Arguments) }
                : null,
            Detect = HasDetect
                ? new OptionDetectSettings { RunValue = TextForms.Keep(DetectRunValue, o.Detect?.RunValue), ScheduledTask = TextForms.Keep(DetectScheduledTask, o.Detect?.ScheduledTask) }
                : null,
        };
    }
}

/// <summary>actions[]: a program from the app's files run after install or before uninstall.</summary>
public sealed partial class ActionRow : EditorRow
{
    internal static readonly string[] Whens = ["afterInstall", "beforeUninstall"];
    internal static readonly string[] Failures = ["fail", "warn", "ignore"];
    internal static readonly string[] Hives = ["HKLM", "HKCU"];
    private ActionSettings _original = new();

    [ObservableProperty] public partial string Id { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial int WhenIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title)), NotifyPropertyChangedFor(nameof(Subtitle))] public partial string Run { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string ArgumentsText { get; set; } = "";
    [ObservableProperty] public partial double TimeoutSeconds { get; set; } = 300;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ExitCodesError))] public partial string ExitCodesText { get; set; } = "0";
    [ObservableProperty] public partial int OnFailureIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IfOptionItem))] public partial string IfOption { get; set; } = "";
    [ObservableProperty] public partial bool HasSkipIfInstalled { get; set; }
    [ObservableProperty] public partial int SkipHiveIndex { get; set; }
    [ObservableProperty] public partial string SkipKey { get; set; } = "";
    [ObservableProperty] public partial string SkipValue { get; set; } = "";
    [ObservableProperty] public partial string SkipVersion { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string NameText { get; set; } = "";
    [ObservableProperty] public partial bool HasFallbackArguments { get; set; }
    [ObservableProperty] public partial string FallbackArgumentsText { get; set; } = "";
    [ObservableProperty] public partial bool HasFallbackRunValue { get; set; }
    [ObservableProperty] public partial string FallbackRunValueName { get; set; } = "";
    [ObservableProperty] public partial string FallbackRunValueArgumentsText { get; set; } = "";

    protected override bool IsViewState(string name) => base.IsViewState(name) || name == nameof(IfOptionItem);

    public ObservableCollection<string>? ExecutableChoices => Owner?.ExecutableChoices;
    public ObservableCollection<string>? IfOptionChoices => Owner?.IfOptionChoices;

    /// <summary>ComboBox selection for ifOption; null pushes (while the choices are rebuilt) are ignored.</summary>
    public object? IfOptionItem
    {
        get => Owner?.IfOptionItemFor(IfOption);
        set
        {
            if (value is string s && Owner is not null && Owner.IfOptionIdFor(s) is var id && id != IfOption) IfOption = id;
        }
    }

    internal void RefreshIfOptionItem() => OnPropertyChanged(nameof(IfOptionItem));

    public string Title
    {
        get
        {
            var name = TextForms.FirstText(NameText);
            if (name.Length > 0) return name;
            var run = Run.Trim();
            return run.Length > 0 ? Path.GetFileName(run.Replace('/', '\\')) : Loc.Get("Actions_Untitled");
        }
    }

    public string Subtitle
    {
        get
        {
            var when = WhenIndex == 1 ? Loc.Get("Actions_WhenBeforeUninstall") : Loc.Get("Actions_WhenAfterInstall");
            var command = string.Join(' ', new[] { Run.Trim() }.Concat(TextForms.SplitLines(ArgumentsText)).Where(s => s.Length > 0));
            return command.Length == 0 ? when : $"{when} · {command}";
        }
    }

    public string ExitCodesError
    {
        get
        {
            TextForms.ParseInts(ExitCodesText, out var valid);
            return valid ? "" : Loc.Get("Actions_ExitCodesInvalid");
        }
    }

    public static ActionRow From(ActionSettings a)
    {
        var skip = a.SkipIfInstalled ?? new RegistryVersionCondition();
        var row = new ActionRow
        {
            _original = a,
            Id = a.Id ?? "",
            Run = a.Run ?? "",
            ArgumentsText = TextForms.Lines(a.Arguments),
            TimeoutSeconds = a.TimeoutSeconds,
            ExitCodesText = TextForms.Ints(a.SuccessExitCodes),
            IfOption = a.IfOption ?? "",
            HasSkipIfInstalled = a.SkipIfInstalled is not null,
            SkipKey = skip.Key ?? "",
            SkipValue = skip.Value ?? "",
            SkipVersion = skip.Version ?? "",
            NameText = TextForms.Localized(a.Name),
            HasFallbackArguments = a.FallbackArguments is not null,
            FallbackArgumentsText = TextForms.Lines(a.FallbackArguments),
            HasFallbackRunValue = a.FallbackRunValue is not null,
            FallbackRunValueName = a.FallbackRunValue?.Name ?? "",
            FallbackRunValueArgumentsText = TextForms.Lines(a.FallbackRunValue?.Arguments),
        };
        row.WhenIndex = TextForms.Find(Whens, a.When);
        row.OnFailureIndex = TextForms.Find(Failures, a.OnFailure);
        row.SkipHiveIndex = TextForms.Find(Hives, skip.Hive);
        return row;
    }

    public ActionSettings ToModel()
    {
        var a = _original;
        var skip = a.SkipIfInstalled;
        return new ActionSettings
        {
            Id = TextForms.Keep(Id, a.Id),
            When = TextForms.Pick(Whens, WhenIndex, a.When),
            Run = TextForms.Keep(Run, a.Run),
            Arguments = TextForms.FromLines(ArgumentsText, a.Arguments),
            TimeoutSeconds = TextForms.ToInt(TimeoutSeconds, a.TimeoutSeconds),
            SuccessExitCodes = a.SuccessExitCodes is not null && TextForms.Same(ExitCodesText, TextForms.Ints(a.SuccessExitCodes))
                ? [.. a.SuccessExitCodes]
                : TextForms.ParseInts(ExitCodesText, out _),
            OnFailure = TextForms.Pick(Failures, OnFailureIndex, a.OnFailure),
            IfOption = TextForms.Keep(IfOption, a.IfOption),
            SkipIfInstalled = HasSkipIfInstalled
                ? new RegistryVersionCondition
                {
                    Hive = TextForms.Pick(Hives, SkipHiveIndex, skip?.Hive),
                    Key = TextForms.Keep(SkipKey, skip?.Key),
                    Value = TextForms.Keep(SkipValue, skip?.Value),
                    Version = TextForms.Keep(SkipVersion, skip?.Version),
                }
                : null,
            Name = TextForms.FromLocalized(NameText, a.Name),
            FallbackArguments = HasFallbackArguments ? TextForms.FromLines(FallbackArgumentsText, a.FallbackArguments) : null,
            FallbackRunValue = HasFallbackRunValue
                ? new RunValueSettings
                {
                    Name = TextForms.Keep(FallbackRunValueName, a.FallbackRunValue?.Name),
                    Arguments = TextForms.FromLines(FallbackRunValueArgumentsText, a.FallbackRunValue?.Arguments),
                }
                : null,
        };
    }
}

/// <summary>files.variants[]: an alternative build of the app with its own source folder.</summary>
public sealed partial class VariantRow : EditorRow
{
    private VariantSettings _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Id { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string LabelText { get; set; } = "";
    [ObservableProperty] public partial string DescriptionText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial bool Default { get; set; }
    [ObservableProperty] public partial string Source { get; set; } = "";

    public string Title => Id.Trim().Length > 0 ? Id.Trim() : Loc.Get("Variants_Untitled");

    public string Subtitle
    {
        get
        {
            var label = TextForms.FirstText(LabelText);
            return Default ? (label.Length == 0 ? Loc.Get("Variants_Default") : $"{label} · {Loc.Get("Variants_Default")}") : label;
        }
    }

    public static VariantRow From(VariantSettings v) => new()
    {
        _original = v,
        Id = v.Id ?? "",
        LabelText = TextForms.Localized(v.Label),
        DescriptionText = TextForms.Localized(v.Description),
        Default = v.Default,
        Source = v.Source ?? "",
    };

    public VariantSettings ToModel() => new()
    {
        Id = TextForms.Keep(Id, _original.Id),
        Label = TextForms.FromLocalized(LabelText, _original.Label),
        Description = TextForms.FromLocalized(DescriptionText, _original.Description),
        Default = Default,
        Source = TextForms.Keep(Source, _original.Source),
    };

    [RelayCommand] private Task BrowseAsync() => Owner?.BrowseVariantSourceAsync(this) ?? Task.CompletedTask;
}

/// <summary>uninstall.elevated.serviceAccess[]: user-account entries to remove from a service's DACL.</summary>
public sealed partial class ServiceAccessRow : EditorRow
{
    private ServiceAccessSettings _original = new();

    [ObservableProperty] public partial string Service { get; set; } = "";
    [ObservableProperty] public partial string RightsText { get; set; } = "";

    public static ServiceAccessRow From(ServiceAccessSettings s) => new() { _original = s, Service = s.Service ?? "", RightsText = TextForms.Csv(s.Rights) };

    public ServiceAccessSettings ToModel() => new()
    {
        Service = TextForms.Keep(Service, _original.Service),
        Rights = TextForms.FromCsv(RightsText, _original.Rights),
    };
}

/// <summary>variables: a value for $(Name) placeholders.</summary>
public sealed partial class VariableRow : EditorRow
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Value { get; set; } = "";
}
