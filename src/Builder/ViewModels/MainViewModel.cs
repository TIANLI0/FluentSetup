using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;
using FluentSetup.Builder.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace FluentSetup.Builder.ViewModels;

public sealed record FileRow(string RelativePath, string SizeText, bool Preserve);
public sealed record ProblemRow(string Severity, string Glyph, string Code, string Field, string Message, string Page);
public sealed record LogRow(string Time, string Message);

public sealed partial class ExtraSourceRow : ObservableObject
{
    [ObservableProperty] public partial string Source { get; set; } = "";
    [ObservableProperty] public partial string Target { get; set; } = "";
}

public sealed partial class HistoryRow : ObservableObject
{
    public required BuildReport Report { get; init; }
    public string When => Report.FinishedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    public string Version => Report.Version;
    public string Result => Report.Success ? Loc.Get("History_Succeeded") : Loc.Get("History_Failed");
    public string Glyph => Report.Success ? "" : "";
    public string Size => Report.Success ? PackageBuilder.Format(Report.Sizes.Total) : "—";
    public string Detail => Report.Success ? Report.OutputPath ?? "" : Report.Error ?? "";
    public bool HasOutput => Report.Success && !string.IsNullOrEmpty(Report.OutputPath) && File.Exists(Report.OutputPath);
}

/// <summary>Per-field validation messages, bound next to the inputs.</summary>
public sealed partial class FieldErrors : ObservableObject
{
    private Dictionary<string, string> _errors = [];
    public string Get(string field) => _errors.TryGetValue(field, out var m) ? m : "";

    /// <summary>Errors of these fields and everything below them ("a.b" covers "a.b", "a.b.c", "a.b[0]" and "a.b/x").</summary>
    public string Under(params string[] prefixes) => string.Join(Environment.NewLine, _errors
        .Where(kv => prefixes.Any(p => kv.Key == p || (kv.Key.StartsWith(p, StringComparison.Ordinal) && kv.Key[p.Length] is '.' or '[' or '/')))
        .Select(kv => kv.Value));

    public void Set(IEnumerable<Diagnostic> diagnostics)
    {
        _errors = diagnostics.Where(d => d.Severity == Severity.Error)
                             .GroupBy(d => d.Field)
                             .ToDictionary(g => g.Key, g => string.Join(Environment.NewLine, g.Select(d => d.Message)));
        OnPropertyChanged(string.Empty);
    }

    public string AppId => Get("app.id");
    public string AppName => Get("app.name");
    public string AppVersion => Get("app.version");
    public string AppPublisher => Get("app.publisher");
    public string AppWebsite => Get("app.website");
    public string AppIcon => Get("app.icon");
    public string AppMainExecutable => Get("app.mainExecutable");
    public string FilesSource => Get("files.source");
    public string InstallUserDir => Get("install.userDir");
    public string InstallMachineDir => Get("install.machineDir");
    public string UiAccentColor => Get("ui.accentColor");
    public string UiLicense => Get("ui.license");
    public string OutputFileName => Get("output.fileName");
    public string OutputDirectory => Get("output.directory");
    public string Signing => string.Join(Environment.NewLine, new[] { Get("signing"), Get("signing.certificateFile"), Get("signing.timestampUrl") }.Where(s => s.Length > 0));

    // Install, upgrade and uninstall
    public string InstallStartMenuFolder => Get("install.startMenuFolder");
    public string InstallIntegration => Under("install.addToPath", "install.ngen");
    public string InstallLegacy => Under("install.adoptExistingInstall", "install.legacyUninstallKeys");
    public string InstallRunning => Under("install.closeProcesses", "install.closeCommand", "install.relaunchArguments");
    public string InstallCleanup => Under("install.cleanup");
    public string UninstallCleanup => Under("uninstall.removeData", "uninstall.removeScheduledTasks", "uninstall.removeRunValues", "uninstall.removeShortcuts", "uninstall.removeRegistryKeys");
    public string UninstallUserData => Under("uninstall.userData");
    public string UninstallElevated => Under("uninstall.elevated.ifExists", "uninstall.elevated.traceSessions", "uninstall.elevated.scheduledTasks", "uninstall.elevated.removeData", "uninstall.elevated.removeRegistryKeys");

    // Prerequisites
    public string NetFramework => Under("requirements.netFramework", "requirements.netFrameworkSetup");
    public string WebView2 => Under("requirements.webView2");
    public string WindowsAppRuntime => Under("requirements.windowsAppRuntime");

    // Options, actions, variants, appearance, variables
    public string Options => Get("options");
    public string Actions => Get("actions");
    public string FilesVariants => Get("files.variants");
    public string OutputVariants => Get("output.variants");
    public string UiLicenses => Under("ui.licenseMode", "ui.licenses");
    public string UiLocalization => Under("ui.localization");
    public string UiStrings => Under("ui.strings");
}

/// <summary>The whole Builder state: one open project, its validation, files, builds and history.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _validateTimer;
    private SetupProject _model = new();
    private bool _loading;
    private CancellationTokenSource? _buildCts;
    private CancellationTokenSource? _collectCts;

    public AppSettings Settings { get; } = AppSettings.Load();
    public FieldErrors Errors { get; } = new();
    public ObservableCollection<ProblemRow> Problems { get; } = [];
    public ObservableCollection<FileRow> Files { get; } = [];
    public ObservableCollection<string> ExecutableChoices { get; } = [];
    public ObservableCollection<ProblemRow> FileProblems { get; } = [];
    public ObservableCollection<ExtraSourceRow> ExtraSources { get; } = [];
    public ObservableCollection<LogRow> BuildLog { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];
    public ObservableCollection<string> RecentProjects { get; } = [];

    public MainViewModel()
    {
        _validateTimer = _dispatcher.CreateTimer();
        _validateTimer.Interval = TimeSpan.FromMilliseconds(300);
        _validateTimer.IsRepeating = false;
        _validateTimer.Tick += (_, _) => Validate();
        foreach (var r in Settings.RecentProjects) RecentProjects.Add(r);
        ExtraSources.CollectionChanged += (_, _) => Touch();
        PropertyChanged += OnAnyPropertyChanged;
        InitAdvanced();
    }

    // ---- project identity ------------------------------------------------------------------------------------

    [ObservableProperty] public partial string? ProjectPath { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    public bool HasProject => ProjectPath is not null || _isNew;
    private bool _isNew;
    public bool NoProject => !HasProject;
    public string ProjectDirectory => ProjectPath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(ProjectPath)!;
    public string WindowSubtitle => !HasProject ? "" : (Path.GetFileName(ProjectPath) ?? Loc.Get("Project_Untitled")) + (IsDirty ? " •" : "");

    // ---- App info --------------------------------------------------------------------------------------------
    [ObservableProperty] public partial string AppId { get; set; } = "";
    [ObservableProperty] public partial string AppName { get; set; } = "";
    [ObservableProperty] public partial string Publisher { get; set; } = "";
    [ObservableProperty] public partial string Version { get; set; } = "1.0.0";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial string Website { get; set; } = "";
    [ObservableProperty] public partial string Icon { get; set; } = "";
    [ObservableProperty] public partial string MainExecutable { get; set; } = "";

    // ---- Files -----------------------------------------------------------------------------------------------
    [ObservableProperty] public partial string Source { get; set; } = "";
    [ObservableProperty] public partial string IncludeText { get; set; } = "";
    [ObservableProperty] public partial string ExcludeText { get; set; } = "";
    [ObservableProperty] public partial string PreserveText { get; set; } = "";
    [ObservableProperty] public partial string EmptyDirectoriesText { get; set; } = "";
    [ObservableProperty] public partial string FilesSummary { get; set; } = "";
    [ObservableProperty] public partial bool IsCollecting { get; set; }
    [ObservableProperty] public partial long PayloadBytes { get; set; }
    [ObservableProperty] public partial int FileCount { get; set; }

    // ---- Install ---------------------------------------------------------------------------------------------
    [ObservableProperty] public partial int ScopeIndex { get; set; }         // user, machine, userOrMachine
    [ObservableProperty] public partial int DefaultScopeIndex { get; set; }  // user, machine
    [ObservableProperty] public partial string UserDir { get; set; } = "";
    [ObservableProperty] public partial string MachineDir { get; set; } = "";
    [ObservableProperty] public partial bool AllowCustomDir { get; set; } = true;
    [ObservableProperty] public partial int DesktopShortcutIndex { get; set; }
    [ObservableProperty] public partial int StartMenuShortcutIndex { get; set; }
    [ObservableProperty] public partial int LaunchAfterIndex { get; set; }
    [ObservableProperty] public partial bool AllowDowngrade { get; set; }
    [ObservableProperty] public partial double MinWindowsBuild { get; set; } = 17763;

    // ---- Appearance ------------------------------------------------------------------------------------------
    [ObservableProperty] public partial string UiTitle { get; set; } = "";
    [ObservableProperty] public partial string WelcomeText { get; set; } = "";
    [ObservableProperty] public partial string FinishText { get; set; } = "";
    [ObservableProperty] public partial int ThemeIndex { get; set; }         // system, light, dark
    [ObservableProperty] public partial int BackdropIndex { get; set; }      // mica, micaAlt, none
    [ObservableProperty] public partial bool UseSystemAccent { get; set; } = true;
    [ObservableProperty] public partial string AccentColor { get; set; } = "";
    [ObservableProperty] public partial string License { get; set; } = "";
    [ObservableProperty] public partial int LanguageIndex { get; set; }      // auto, en-US, zh-CN

    // ---- Output / signing ------------------------------------------------------------------------------------
    [ObservableProperty] public partial string OutputDirectory { get; set; } = "dist";
    [ObservableProperty] public partial string OutputFileName { get; set; } = "";
    [ObservableProperty] public partial int ArchitectureIndex { get; set; }  // x64, arm64
    [ObservableProperty] public partial int CompressionIndex { get; set; } = 1;  // fast, normal, max
    [ObservableProperty] public partial bool IncludeWinUI { get; set; } = true;
    [ObservableProperty] public partial bool SigningEnabled { get; set; }
    [ObservableProperty] public partial string CertificateThumbprint { get; set; } = "";
    [ObservableProperty] public partial string CertificateFile { get; set; } = "";
    [ObservableProperty] public partial string PasswordEnvironmentVariable { get; set; } = "";
    [ObservableProperty] public partial string TimestampUrl { get; set; } = "";
    [ObservableProperty] public partial string SignToolPath { get; set; } = "";

    // ---- Build state -----------------------------------------------------------------------------------------
    [ObservableProperty] public partial bool IsBuilding { get; set; }
    [ObservableProperty] public partial double BuildProgressValue { get; set; }
    [ObservableProperty] public partial bool BuildProgressIndeterminate { get; set; }
    [ObservableProperty] public partial string BuildStatus { get; set; } = "";
    [ObservableProperty] public partial BuildReport? LastReport { get; set; }
    [ObservableProperty] public partial int ErrorCount { get; set; }
    [ObservableProperty] public partial int WarningCount { get; set; }

    public bool NotBuilding => !IsBuilding;
    public bool HasLastReport => LastReport is not null;
    public bool LastBuildSucceeded => LastReport?.Success == true;
    public bool LastBuildFailed => LastReport is { Success: false };
    public string LastBuildTitle => LastReport is null ? "" : LastReport.Success ? Loc.Get("Build_SucceededTitle") : Loc.Get("Build_FailedTitle");
    public string LastBuildMessage => LastReport is null ? "" : LastReport.Success
        ? Loc.Format("Build_SucceededMessage", LastReport.OutputPath ?? "", PackageBuilder.Format(LastReport.Sizes.Total), LastReport.DurationSeconds.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture))
        : (LastReport.Error ?? "") + FormatDiagnostics(LastReport.Diagnostics);
    public string SizeStub => LastReport is { Success: true } r ? PackageBuilder.Format(r.Sizes.Stub) : "—";
    public string SizeWinUI => LastReport is { Success: true } r ? (r.IncludesWinUI ? PackageBuilder.Format(r.Sizes.WinUiFrontEnd) : Loc.Get("Build_NotIncluded")) : "—";
    public string SizePayload => LastReport is { Success: true } r ? $"{PackageBuilder.Format(r.Sizes.PayloadRaw)} → {PackageBuilder.Format(r.Sizes.PayloadCompressed)}" : "—";
    public string SizeOverhead => LastReport is { Success: true } r ? PackageBuilder.Format(r.Sizes.InstallerOverhead) : "—";
    public string SizeTotal => LastReport is { Success: true } r ? PackageBuilder.Format(r.Sizes.Total) : "—";
    public string LastBuildSummary => LastReport is null ? Loc.Get("Overview_NeverBuilt")
        : LastReport.Success ? Loc.Format("Overview_LastBuildOk", LastReport.FinishedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture), PackageBuilder.Format(LastReport.Sizes.Total))
        : Loc.Format("Overview_LastBuildFailed", LastReport.FinishedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));

    public bool CanBuild => HasProject && !IsBuilding && ErrorCount == 0;
    public string StatusText => ErrorCount > 0 ? Loc.Format("Status_Errors", ErrorCount) : WarningCount > 0 ? Loc.Format("Status_Warnings", WarningCount) : Loc.Get("Status_Ready");
    public string StatusGlyph => ErrorCount > 0 ? "" : WarningCount > 0 ? "" : "";
    public bool HasProblems => Problems.Count > 0;
    public bool HasNoProblems => Problems.Count == 0;
    public bool HasHistory => History.Count > 0;
    public bool HasNoHistory => History.Count == 0;
    public bool HasFiles => Files.Count > 0;
    public bool HasFileProblems => FileProblems.Count > 0;
    public bool ShowAccentColor => !UseSystemAccent;
    public string DisplayName => string.IsNullOrWhiteSpace(AppName) ? Loc.Get("Project_Untitled") : AppName;
    /// <summary>Publisher and version as the build will see them ($(Name) variables replaced).</summary>
    public string DisplayMeta
    {
        get
        {
            var app = HasProject ? ExpandedModel().App : new AppInfo { Publisher = Publisher, Version = Version };
            return string.Join("  ·  ", new[] { app.Publisher, string.IsNullOrWhiteSpace(app.Version) ? "" : Loc.Format("Overview_Version", app.Version) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }
    public string InstallSummary => $"{ScopeLabel(ScopeIndex)} · {(ScopeIndex == 1 ? MachineDir : UserDir)}";
    public string ShortcutSummary => string.Join(", ", new[]
    {
        StartMenuShortcutIndex != 0 ? Loc.Get("Overview_StartMenu") : null,
        DesktopShortcutIndex != 0 ? Loc.Get("Overview_Desktop") : null,
    }.Where(s => s is not null)) is { Length: > 0 } s ? s : Loc.Get("Overview_NoShortcuts");
    public string AppearanceSummary => $"{Loc.Get("Theme_" + ThemeIndex)} · {Loc.Get("Backdrop_" + BackdropIndex)}{(string.IsNullOrEmpty(License) ? "" : " · " + Loc.Get("Overview_License"))}";
    public string OutputSummary
    {
        get
        {
            var model = ExpandedModel();
            return $"{ProjectValidator.ExpandFileName(model.Output.FileName, model)} · {(ArchitectureIndex == 1 ? "ARM64" : "x64")}";
        }
    }
    public string RuntimeStatus
    {
        get
        {
            var assets = new RuntimeAssets();
            var x64 = assets.HasStub("x64") ? (assets.HasUi("x64") ? Loc.Get("Settings_RuntimeFull") : Loc.Get("Settings_RuntimeNoUi")) : Loc.Get("Settings_RuntimeMissing");
            return $"x64: {x64}" + Environment.NewLine + assets.Root;
        }
    }
    public string AppVersionText => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    [ObservableProperty] public partial Microsoft.UI.Xaml.Media.ImageSource? IconImage { get; set; }

    /// <summary>Decodes the icon with WIC and uses its largest frame (an .ico's first frame is often 16 px: blurry).</summary>
    private async void UpdateIconImage()
    {
        var path = string.IsNullOrEmpty(Icon) ? new RuntimeAssets().DefaultIcon : ProjectValidator.Resolve(ProjectDirectory, Icon);
        if (!File.Exists(path))
        {
            IconImage = null;
            return;
        }
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            uint best = 0, bestWidth = 0;
            for (uint i = 0; i < decoder.FrameCount; i++)
            {
                var frame = await decoder.GetFrameAsync(i);
                if (frame.PixelWidth > bestWidth) { best = i; bestWidth = frame.PixelWidth; }
            }
            var chosen = await decoder.GetFrameAsync(best);
            var bitmap = await chosen.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            var source = new Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            IconImage = source;
        }
        catch (Exception)
        {
            IconImage = null;  // unreadable icon: validation reports it
        }
    }

    /// <summary>ComboBox selection for the main program (the list comes from the collected files).</summary>
    public object? MainExecutableItem
    {
        get => ExecutableChoices.FirstOrDefault(e => string.Equals(e, MainExecutable, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is string s && !string.Equals(s, MainExecutable, StringComparison.Ordinal)) MainExecutable = s;
        }
    }
    private static string ScopeLabel(int index) => Loc.Get("Scope_" + index);

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == Severity.Error).Take(8).Select(d => $"• {d.Message} ({d.Code})").ToList();
        return errors.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, errors);
    }

    // ---- change tracking -------------------------------------------------------------------------------------

    private static readonly HashSet<string> NonProjectProperties =
    [
        nameof(ProjectPath), nameof(IsDirty), nameof(IsBuilding), nameof(BuildProgressValue), nameof(BuildProgressIndeterminate),
        nameof(BuildStatus), nameof(LastReport), nameof(ErrorCount), nameof(WarningCount), nameof(FilesSummary), nameof(IsCollecting),
        nameof(PayloadBytes), nameof(FileCount), nameof(BuilderThemeIndex), nameof(MainExecutableItem), nameof(IconImage),
        nameof(UpdateRelease), nameof(UpdateStatus), nameof(IsUpdating), nameof(CheckForUpdatesAutomatically),
    ];

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName ?? "";
        if (name == nameof(IsDirty) || name == nameof(ProjectPath)) OnPropertyChanged(nameof(WindowSubtitle));
        if (name == nameof(IsBuilding)) { OnPropertyChanged(nameof(NotBuilding)); OnPropertyChanged(nameof(CanBuild)); }
        if (name == nameof(ErrorCount) || name == nameof(WarningCount)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusGlyph)); OnPropertyChanged(nameof(CanBuild)); }
        if (name == nameof(LastReport))
        {
            foreach (var p in new[] { nameof(HasLastReport), nameof(LastBuildSucceeded), nameof(LastBuildFailed), nameof(LastBuildTitle), nameof(LastBuildMessage),
                                      nameof(SizeStub), nameof(SizeWinUI), nameof(SizePayload), nameof(SizeOverhead), nameof(SizeTotal), nameof(LastBuildSummary) })
            {
                OnPropertyChanged(p);
            }
        }
        if (name == nameof(UseSystemAccent)) OnPropertyChanged(nameof(ShowAccentColor));
        if (name == nameof(Icon)) UpdateIconImage();
        if (name == nameof(MainExecutable)) OnPropertyChanged(nameof(MainExecutableItem));
        if (name is nameof(AppName) or nameof(Publisher) or nameof(Version)) { OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(DisplayMeta)); }
        if (name is nameof(ScopeIndex) or nameof(UserDir) or nameof(MachineDir)) OnPropertyChanged(nameof(InstallSummary));
        if (name is nameof(StartMenuShortcutIndex) or nameof(DesktopShortcutIndex)) OnPropertyChanged(nameof(ShortcutSummary));
        if (name is nameof(ThemeIndex) or nameof(BackdropIndex) or nameof(License)) OnPropertyChanged(nameof(AppearanceSummary));
        if (name is nameof(OutputFileName) or nameof(ArchitectureIndex) or nameof(AppName) or nameof(Version) or nameof(AppId)) OnPropertyChanged(nameof(OutputSummary));
        if (name == nameof(Source) && !_loading) _ = RefreshFilesAsync();

        if (_loading || NonProjectProperties.Contains(name) || name.Length == 0) return;
        if (GetType().GetProperty(name) is { CanWrite: true }) Touch();
    }

    private void Touch()
    {
        if (_loading) return;
        IsDirty = true;
        _validateTimer.Stop();
        _validateTimer.Start();
    }

    // ---- model mapping ---------------------------------------------------------------------------------------

    private static readonly string[] Scopes = ["user", "machine", "userOrMachine"];
    private static readonly string[] DefaultScopes = ["user", "machine"];
    private static readonly string[] Offers = ["off", "on", "optionalOff", "optionalOn"];
    private static readonly string[] Themes = ["system", "light", "dark"];
    private static readonly string[] Backdrops = ["mica", "micaAlt", "none"];
    private static readonly string[] Languages = ["auto", .. FluentSetup.Builder.Core.Packaging.Localization.SupportedLanguages];
    private static readonly string[] Architectures = ["x64", "arm64"];
    private static readonly string[] Compressions = ["fast", "normal", "max"];

    private static int IndexOf(string[] values, string value, int fallback = 0)
    {
        var i = Array.IndexOf(values, value);
        return i < 0 ? fallback : i;
    }

    private static string Lines(IEnumerable<string>? items) => TextForms.Lines(items);

    private void LoadFrom(SetupProject p)
    {
        _loading = true;
        try
        {
            _model = p;
            AppId = p.App.Id; AppName = p.App.Name; Publisher = p.App.Publisher; Version = p.App.Version;
            Description = p.App.Description; Website = p.App.Website; Icon = p.App.Icon; MainExecutable = p.App.MainExecutable;
            Source = p.Files.Source; IncludeText = Lines(p.Files.Include); ExcludeText = Lines(p.Files.Exclude);
            PreserveText = Lines(p.Files.Preserve); EmptyDirectoriesText = Lines(p.Files.EmptyDirectories);
            ExtraSources.Clear();
            foreach (var e in p.Files.Extra)
            {
                var row = new ExtraSourceRow { Source = e.Source, Target = e.Target };
                row.PropertyChanged += (_, _) => Touch();
                ExtraSources.Add(row);
            }
            ScopeIndex = IndexOf(Scopes, p.Install.Scope); DefaultScopeIndex = p.Install.DefaultScope == "machine" ? 1 : 0;
            UserDir = p.Install.UserDir; MachineDir = p.Install.MachineDir; AllowCustomDir = p.Install.AllowCustomDir;
            DesktopShortcutIndex = IndexOf(Offers, p.Install.DesktopShortcut, 2); StartMenuShortcutIndex = IndexOf(Offers, p.Install.StartMenuShortcut, 1);
            LaunchAfterIndex = IndexOf(Offers, p.Install.LaunchAfterInstall, 3); AllowDowngrade = p.Install.AllowDowngrade; MinWindowsBuild = p.Install.MinWindowsBuild;
            UiTitle = p.Ui.Title; WelcomeText = p.Ui.WelcomeText; FinishText = p.Ui.FinishText; ThemeIndex = IndexOf(Themes, p.Ui.Theme);
            BackdropIndex = IndexOf(Backdrops, p.Ui.Backdrop); UseSystemAccent = p.Ui.UseSystemAccent; AccentColor = p.Ui.AccentColor;
            License = p.Ui.License; LanguageIndex = IndexOf(Languages, p.Ui.DefaultLanguage);
            OutputDirectory = p.Output.Directory; OutputFileName = p.Output.FileName; ArchitectureIndex = IndexOf(Architectures, p.Output.Architecture);
            CompressionIndex = IndexOf(Compressions, p.Output.Compression, 1); IncludeWinUI = p.Output.IncludeWinUI;
            SigningEnabled = p.Signing.Enabled; CertificateThumbprint = p.Signing.CertificateThumbprint; CertificateFile = p.Signing.CertificateFile;
            PasswordEnvironmentVariable = p.Signing.PasswordEnvironmentVariable; TimestampUrl = p.Signing.TimestampUrl; SignToolPath = p.Signing.SignToolPath;
            LoadAdvanced(p);
        }
        finally
        {
            _loading = false;
        }
        IsDirty = false;
        OnPropertyChanged(string.Empty);
        UpdateIconImage();
        Validate();
        LoadHistory();
        _ = RefreshFilesAsync();
    }

    /// <summary>The current UI state as a project (a fresh copy; unknown properties of the loaded file are kept).</summary>
    public SetupProject BuildModel()
    {
        var p = ProjectSerializer.Clone(_model);
        p.App.Id = AppId.Trim(); p.App.Name = AppName.Trim(); p.App.Publisher = Publisher.Trim(); p.App.Version = Version.Trim();
        p.App.Description = Description; p.App.Website = Website.Trim(); p.App.Icon = Icon.Trim(); p.App.MainExecutable = MainExecutable.Trim();
        p.Files.Source = Source.Trim(); p.Files.Include = TextForms.FromLines(IncludeText, p.Files.Include); p.Files.Exclude = TextForms.FromLines(ExcludeText, p.Files.Exclude);
        p.Files.Preserve = TextForms.FromLines(PreserveText, p.Files.Preserve); p.Files.EmptyDirectories = TextForms.FromLines(EmptyDirectoriesText, p.Files.EmptyDirectories);
        p.Files.Extra = ExtraSources.Where(e => !string.IsNullOrWhiteSpace(e.Source)).Select(e => new ExtraSource { Source = e.Source.Trim(), Target = e.Target.Trim() }).ToList();
        // Choices keep the stored value while their selection is unchanged (also values the lists do not know).
        var scope = PickFrom(Scopes, ScopeIndex, p.Install.Scope, 0);
        var defaultScope = PickFrom(DefaultScopes, DefaultScopeIndex, p.Install.DefaultScope, 0);
        if (scope != "userOrMachine" && scope != p.Install.Scope) defaultScope = scope;
        p.Install.Scope = scope; p.Install.DefaultScope = defaultScope;
        p.Install.UserDir = UserDir.Trim(); p.Install.MachineDir = MachineDir.Trim(); p.Install.AllowCustomDir = AllowCustomDir;
        p.Install.DesktopShortcut = PickFrom(Offers, DesktopShortcutIndex, p.Install.DesktopShortcut, 2);
        p.Install.StartMenuShortcut = PickFrom(Offers, StartMenuShortcutIndex, p.Install.StartMenuShortcut, 1);
        p.Install.LaunchAfterInstall = PickFrom(Offers, LaunchAfterIndex, p.Install.LaunchAfterInstall, 3); p.Install.AllowDowngrade = AllowDowngrade;
        p.Install.MinWindowsBuild = double.IsNaN(MinWindowsBuild) ? 0 : (int)MinWindowsBuild;
        p.Ui.Title = UiTitle; p.Ui.WelcomeText = WelcomeText; p.Ui.FinishText = FinishText; p.Ui.Theme = PickFrom(Themes, ThemeIndex, p.Ui.Theme, 0);
        p.Ui.Backdrop = PickFrom(Backdrops, BackdropIndex, p.Ui.Backdrop, 0); p.Ui.UseSystemAccent = UseSystemAccent; p.Ui.AccentColor = AccentColor.Trim();
        p.Ui.License = License.Trim(); p.Ui.DefaultLanguage = PickFrom(Languages, LanguageIndex, p.Ui.DefaultLanguage, 0);
        p.Output.Directory = OutputDirectory.Trim(); p.Output.FileName = OutputFileName.Trim(); p.Output.Architecture = PickFrom(Architectures, ArchitectureIndex, p.Output.Architecture, 0);
        p.Output.Compression = PickFrom(Compressions, CompressionIndex, p.Output.Compression, 1); p.Output.IncludeWinUI = IncludeWinUI;
        p.Signing.Enabled = SigningEnabled; p.Signing.CertificateThumbprint = CertificateThumbprint.Trim(); p.Signing.CertificateFile = CertificateFile.Trim();
        p.Signing.PasswordEnvironmentVariable = PasswordEnvironmentVariable.Trim(); p.Signing.TimestampUrl = TimestampUrl.Trim(); p.Signing.SignToolPath = SignToolPath.Trim();
        ApplyAdvanced(p);
        return p;
    }

    /// <summary>The project with $(Name) placeholders replaced: what a build uses (validation, files, preview).</summary>
    public SetupProject ExpandedModel(DiagnosticBag? diagnostics = null) => ProjectVariables.Expand(BuildModel(), null, diagnostics);

    // ---- validation ------------------------------------------------------------------------------------------

    private List<Diagnostic> _collectorDiagnostics = [];

    public void Validate()
    {
        if (!HasProject) return;
        // Validate what a build validates: the project with its $(Name) placeholders replaced.
        var variableBag = new DiagnosticBag();
        var bag = ProjectValidator.Validate(ExpandedModel(variableBag), ProjectDirectory);
        var all = bag.Items.Concat(variableBag.Items).Concat(_collectorDiagnostics).ToList();
        Errors.Set(all);
        UpdateRowErrors();
        OnPropertyChanged(nameof(OutputSummary));
        OnPropertyChanged(nameof(DisplayMeta));  // variables may have changed
        Problems.Clear();
        foreach (var d in all.Where(d => d.Severity != Severity.Info).OrderByDescending(d => d.Severity)) Problems.Add(ToRow(d));
        ErrorCount = all.Count(d => d.Severity == Severity.Error);
        WarningCount = all.Count(d => d.Severity == Severity.Warning);
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(HasNoProblems));
        OnPropertyChanged(nameof(CanBuild));
    }

    private static ProblemRow ToRow(Diagnostic d) => new(
        d.Severity.ToString(), d.Severity == Severity.Error ? "" : d.Severity == Severity.Warning ? "" : "",
        d.Code, d.Field, d.Message, PageForField(d.Field));

    private static readonly string[] UpgradeFields =
        ["install.allowDowngrade", "install.adoptExistingInstall", "install.legacyUninstallKeys", "install.closeProcesses", "install.closeCommand", "install.relaunchArguments", "install.cleanup"];

    /// <summary>True for <paramref name="prefix"/> itself and anything below it (".x", "[0]", "/file").</summary>
    private static bool IsFieldUnder(string field, string prefix) =>
        field == prefix || (field.StartsWith(prefix, StringComparison.Ordinal) && field[prefix.Length] is '.' or '[' or '/');

    public static string PageForField(string field) => field switch
    {
        _ when field.StartsWith("app.", StringComparison.Ordinal) => "app",
        _ when IsFieldUnder(field, "output.variants") => "files",
        _ when field.StartsWith("files", StringComparison.Ordinal) => "files",
        _ when UpgradeFields.Any(f => IsFieldUnder(field, f)) => "upgrade",
        _ when field.StartsWith("install.", StringComparison.Ordinal) => "install",
        _ when IsFieldUnder(field, "requirements") => "prerequisites",
        _ when IsFieldUnder(field, "options") || IsFieldUnder(field, "actions") => "options",
        _ when IsFieldUnder(field, "uninstall") => "uninstall",
        _ when IsFieldUnder(field, "variables") => "build",
        "ui.license" or "ui.defaultLanguage" => "app",
        _ when field.StartsWith("ui.", StringComparison.Ordinal) => "appearance",
        "output.fileName" => "app",
        _ when field.StartsWith("output.", StringComparison.Ordinal) || field.StartsWith("signing", StringComparison.Ordinal) => "build",
        _ => "overview",
    };

    // ---- files -----------------------------------------------------------------------------------------------

    [RelayCommand]
    public async Task RefreshFilesAsync()
    {
        if (!HasProject) return;
        _collectCts?.Cancel();
        var cts = _collectCts = new CancellationTokenSource();
        IsCollecting = true;
        var model = ExpandedModel();
        var dir = ProjectDirectory;
        try
        {
            var result = await Task.Run(() => FileCollector.Collect(model, dir, model.Files.Variants, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            Files.Clear();
            foreach (var f in result.Files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).Take(5000)) Files.Add(new FileRow(f.RelativePath, PackageBuilder.Format(f.Size), f.Preserve));
            FileCount = result.Files.Count;
            PayloadBytes = result.TotalSize;
            FilesSummary = result.Files.Count == 0 ? Loc.Get("Files_None") : Loc.Format("Files_Summary", result.Files.Count, PackageBuilder.Format(result.TotalSize))
                           + (result.Files.Count > Files.Count ? " " + Loc.Format("Files_Truncated", Files.Count) : "");
            var current = MainExecutable;
            var choices = result.Files.Where(f => f.RelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Select(f => f.RelativePath).ToList();
            // Keep a configured program visible even if it is not (yet) among the files; validation flags it.
            if (!string.IsNullOrWhiteSpace(current) && !choices.Any(e => string.Equals(e, current.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))) choices.Insert(0, current);
            if (!choices.SequenceEqual(ExecutableChoices, StringComparer.Ordinal))
            {
                ExecutableChoices.Clear();
                foreach (var exe in choices) ExecutableChoices.Add(exe);
            }
            if (!string.Equals(MainExecutable, current, StringComparison.Ordinal)) MainExecutable = current;
            OnPropertyChanged(nameof(MainExecutableItem));
            _collectorDiagnostics = result.Diagnostics.Items.ToList();
            FileProblems.Clear();
            foreach (var d in _collectorDiagnostics.Where(d => d.Severity != Severity.Info)) FileProblems.Add(ToRow(d));
            OnPropertyChanged(nameof(HasFiles));
            OnPropertyChanged(nameof(HasFileProblems));
            Validate();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_collectCts == cts) IsCollecting = false;
        }
    }

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        if (await Ui.PickFolderAsync() is { } folder) Source = MakeRelative(folder);
    }

    [RelayCommand]
    private async Task BrowseIconAsync()
    {
        if (await Ui.PickFileAsync(".ico", ".png") is { } file) Icon = MakeRelative(file);
    }

    [RelayCommand]
    private async Task BrowseLicenseAsync()
    {
        if (await Ui.PickFileAsync(".txt", ".md") is { } file) License = MakeRelative(file);
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        if (await Ui.PickFolderAsync() is { } folder) OutputDirectory = MakeRelative(folder);
    }

    [RelayCommand]
    private async Task BrowseCertificateAsync()
    {
        if (await Ui.PickFileAsync(".pfx", ".p12") is { } file) CertificateFile = MakeRelative(file);
    }

    [RelayCommand]
    private async Task AddExtraSourceAsync()
    {
        var path = await Ui.PickFolderAsync();
        if (path is null) return;
        var row = new ExtraSourceRow { Source = MakeRelative(path), Target = Path.GetFileName(path) };
        row.PropertyChanged += (_, _) => Touch();
        ExtraSources.Add(row);
        _ = RefreshFilesAsync();
    }

    [RelayCommand]
    private void RemoveExtraSource(ExtraSourceRow? row)
    {
        if (row is null) return;
        ExtraSources.Remove(row);
        _ = RefreshFilesAsync();
    }

    /// <summary>Paths inside the project folder are stored relative so the project can move with its files.</summary>
    private string MakeRelative(string path)
    {
        if (ProjectPath is null) return path;
        var rel = Path.GetRelativePath(ProjectDirectory, path);
        // Keep absolute paths for locations far away from the project (different drive or many levels up).
        var ups = 0;
        for (var r = rel; r.StartsWith("..", StringComparison.Ordinal); r = r.Length > 3 ? r[3..] : "") ups++;
        return Path.IsPathRooted(rel) || ups > 2 ? path : rel;
    }

    // ---- project lifecycle -----------------------------------------------------------------------------------

    /// <summary>Asks to save unsaved changes. Returns false if the user cancelled.</summary>
    public async Task<bool> ConfirmCloseProjectAsync()
    {
        if (!HasProject || !IsDirty) return true;
        var result = await Ui.ConfirmUnsavedAsync();
        if (result == ContentDialogResult.None) return false;
        if (result == ContentDialogResult.Primary) return await SaveCoreAsync(saveAs: false);
        return true;
    }

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (!await ConfirmCloseProjectAsync()) return;
        var folder = await Ui.PickFolderAsync();
        if (folder is null) return;
        var dialog = new Views.NewProjectDialog(folder);
        if (await Ui.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var projectPath = await Ui.PickSaveFileAsync("fluentsetup");
        if (projectPath is null) return;
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var project = new SetupProject
        {
            App = new AppInfo
            {
                Id = dialog.AppIdValue, Name = dialog.AppNameValue, Publisher = dialog.PublisherValue, Version = dialog.VersionValue,
                MainExecutable = dialog.MainExecutableValue,
            },
            Files = new FileSettings { Source = Path.GetRelativePath(projectDir, folder) },
            Output = new OutputSettings { Directory = Path.GetRelativePath(projectDir, Path.Combine(projectDir, "dist")) },
        };
        if (IsUnder(Path.Combine(projectDir, "dist"), folder))
        {
            project.Output.Directory = Path.GetRelativePath(projectDir, Path.Combine(Path.GetDirectoryName(folder) ?? projectDir, "dist"));
        }
        try
        {
            ProjectSerializer.Save(project, projectPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Ui.MessageAsync(Loc.Get("Error_SaveTitle"), ex.Message);
            return;
        }
        OpenPath(projectPath);
    }

    private static bool IsUnder(string path, string parent) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + "\\", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        if (!await ConfirmCloseProjectAsync()) return;
        if (await Ui.PickFileAsync(".json") is { } path) OpenPath(path);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!await ConfirmCloseProjectAsync()) return;
        if (!File.Exists(path))
        {
            Settings.RecentProjects.Remove(path);
            Settings.Save();
            RecentProjects.Remove(path);
            await Ui.MessageAsync(Loc.Get("Error_OpenTitle"), Loc.Format("Error_NotFound", path));
            return;
        }
        OpenPath(path);
    }

    public void OpenPath(string path)
    {
        try
        {
            var project = ProjectSerializer.Load(path);
            ProjectPath = Path.GetFullPath(path);
            _isNew = false;
            LoadFrom(project);
            Settings.AddRecent(ProjectPath);
            RecentProjects.Clear();
            foreach (var r in Settings.RecentProjects) RecentProjects.Add(r);
            LastReport = History.FirstOrDefault()?.Report;
            OnPropertyChanged(nameof(HasProject));
            OnPropertyChanged(nameof(NoProject));
            OnPropertyChanged(nameof(ProjectDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            _ = Ui.MessageAsync(Loc.Get("Error_OpenTitle"), ex.Message);
        }
    }

    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(saveAs: false);

    [RelayCommand]
    private Task SaveAsAsync() => SaveCoreAsync(saveAs: true);

    private async Task<bool> SaveCoreAsync(bool saveAs)
    {
        if (!HasProject) return false;
        var path = ProjectPath;
        if (saveAs || path is null)
        {
            path = await Ui.PickSaveFileAsync(Path.GetFileNameWithoutExtension(ProjectPath) ?? "fluentsetup");
            if (path is null) return false;
        }
        try
        {
            var model = BuildModel();
            ProjectSerializer.Save(model, path);
            _model = model;
            ProjectPath = Path.GetFullPath(path);
            IsDirty = false;
            Settings.AddRecent(ProjectPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Ui.MessageAsync(Loc.Get("Error_SaveTitle"), ex.Message);
            return false;
        }
    }

    // ---- build -----------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (!HasProject || IsBuilding) return;
        if (IsDirty && !await SaveCoreAsync(saveAs: false)) return;
        Validate();
        BuildLog.Clear();
        IsBuilding = true;
        BuildProgressIndeterminate = true;
        BuildProgressValue = 0;
        BuildStatus = Loc.Get("Build_Starting");
        _buildCts = new CancellationTokenSource();
        var log = new Action<string>(m => _dispatcher.TryEnqueue(() => BuildLog.Add(new LogRow(DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), m))));
        var lastStage = (BuildStage)(-1);
        var progress = new Progress<BuildProgress>(p =>
        {
            BuildStatus = p.Message;
            BuildProgressIndeterminate = p.Stage != BuildStage.Compressing;
            BuildProgressValue = p.Stage switch
            {
                BuildStage.Validating => 2, BuildStage.Collecting => 5, BuildStage.Compressing => 10 + p.Fraction * 75,
                BuildStage.Writing => 88, BuildStage.Signing => 92, BuildStage.Verifying => 96, _ => 100,
            };
            if (p.Stage != lastStage)
            {
                lastStage = p.Stage;
                log(p.Message);
            }
        });
        var sw = Stopwatch.StartNew();
        BuildReport report;
        try
        {
            report = await new PackageBuilder().BuildAsync(BuildModel(), ProjectPath!, new BuildOptions { Log = log }, progress, _buildCts.Token);
        }
        catch (OperationCanceledException)
        {
            report = new BuildReport
            {
                AppId = AppId, AppName = AppName, Version = Version, Success = false, Error = Loc.Get("Build_Cancelled"),
                StartedAt = DateTimeOffset.Now - sw.Elapsed, FinishedAt = DateTimeOffset.Now,
            };
        }
        finally
        {
            IsBuilding = false;
            _buildCts.Dispose();
            _buildCts = null;
        }
        foreach (var d in report.Diagnostics.Where(d => d.Severity == Severity.Error)) log($"{d.Code}: {d.Message}");
        BuildStatus = report.Success ? Loc.Get("Build_Done") : report.Error ?? "";
        LastReport = report;
        BuildHistoryStore.Append(report);
        LoadHistory();
    }

    [RelayCommand]
    private void CancelBuild() => _buildCts?.Cancel();

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (LastReport?.OutputPath is { } p && File.Exists(p)) Ui.OpenFolder(p, select: true);
        else Ui.OpenFolder(ProjectValidator.Resolve(ProjectDirectory, OutputDirectory));
    }

    [RelayCommand]
    private void CopyOutputPath()
    {
        if (LastReport?.OutputPath is { } p) Ui.CopyText(p);
    }

    // ---- preview ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task PreviewAsync(string? scenario)
    {
        if (!HasProject) return;
        try
        {
            PreviewLauncher.Launch(ExpandedModel(), ProjectDirectory, PayloadBytes, scenario ?? "install");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            await Ui.MessageAsync(Loc.Get("Preview_ErrorTitle"), ex.Message);
        }
    }

    // ---- history ---------------------------------------------------------------------------------------------

    private void LoadHistory()
    {
        History.Clear();
        // Builds record the expanded id (app.id may use a $(Name) placeholder).
        foreach (var r in BuildHistoryStore.Load(ExpandedModel().App.Id).Take(100)) History.Add(new HistoryRow { Report = r });
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasNoHistory));
    }

    [RelayCommand]
    private static void OpenHistoryFolder(HistoryRow? row)
    {
        if (row?.Report.OutputPath is { } p && File.Exists(p)) Ui.OpenFolder(p, select: true);
    }

    [RelayCommand]
    private static async Task InspectAsync(HistoryRow? row)
    {
        if (row?.Report.OutputPath is not { } p || !File.Exists(p)) return;
        PackageInfo info;
        try
        {
            info = await Task.Run(() => PackageInspector.Inspect(p, verifyStreams: true));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await Ui.MessageAsync(Loc.Get("Inspect_Title"), ex.Message);
            return;
        }
        var lines = new List<string>
        {
            p,
            Loc.Format("Inspect_Size", PackageBuilder.Format(info.FileSize)),
            Loc.Format("Inspect_Stub", PackageBuilder.Format(info.OverlayStart)),
            Loc.Format("Inspect_Frontend", info.FrontendFiles, PackageBuilder.Format(info.FrontendStoredBytes)),
            Loc.Format("Inspect_Payload", info.PayloadFiles, PackageBuilder.Format(info.PayloadRawBytes), PackageBuilder.Format(info.PayloadStoredBytes)),
            Loc.Format("Inspect_Signed", info.Signed ? Loc.Get("Common_Yes") : Loc.Get("Common_No")),
            info.Problems.Count == 0 ? Loc.Get("Inspect_Ok") : Loc.Get("Inspect_Problems") + " " + string.Join("; ", info.Problems),
        };
        await Ui.MessageAsync(Loc.Get("Inspect_Title"), string.Join(Environment.NewLine, lines));
    }

    // ---- settings --------------------------------------------------------------------------------------------

    public int BuilderThemeIndex
    {
        get => IndexOf(Themes, Settings.Theme);
        set
        {
            Settings.Theme = Themes[Math.Clamp(value, 0, 2)];
            Settings.Save();
            OnPropertyChanged();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ThemeChanged;

    [RelayCommand]
    private static void OpenSettingsFolder() => Ui.OpenFolder(AppSettings.Folder);

    // ---- updates of FluentSetup itself -----------------------------------------------------------------------

    [ObservableProperty] public partial FluentSetup.Builder.Core.Updates.FluentSetupRelease? UpdateRelease { get; set; }
    [ObservableProperty] public partial string UpdateStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsUpdating { get; set; }

    public bool IsUpdateAvailable => UpdateRelease is not null;
    public bool NotUpdating => !IsUpdating;
    public string UpdatePageUrl => UpdateRelease?.PageUrl ?? FluentSetup.Builder.Core.Updates.FluentSetupUpdates.ReleasesPage;

    partial void OnIsUpdatingChanged(bool value) => OnPropertyChanged(nameof(NotUpdating));
    public string UpdateMessage => UpdateRelease is { } r ? Loc.Format("Update_Available", r.Version, FluentSetup.Builder.Core.Updates.FluentSetupUpdates.CurrentVersion) : "";

    public bool CheckForUpdatesAutomatically
    {
        get => Settings.CheckForUpdates;
        set
        {
            if (Settings.CheckForUpdates == value) return;
            Settings.CheckForUpdates = value;
            Settings.Save();
            OnPropertyChanged();
        }
    }

    partial void OnUpdateReleaseChanged(FluentSetup.Builder.Core.Updates.FluentSetupRelease? value)
    {
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(UpdateMessage));
        OnPropertyChanged(nameof(UpdatePageUrl));
    }

    /// <summary>At start-up: at most once a day, and only if the user did not turn it off. Silent when nothing is found.</summary>
    public async Task StartupUpdateCheckAsync()
    {
        if (!Settings.CheckForUpdates || Settings.LastUpdateCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromDays(1)) return;
        await CheckCoreAsync(manual: false);
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => CheckCoreAsync(manual: true);

    private async Task CheckCoreAsync(bool manual)
    {
        if (IsUpdating) return;
        IsUpdating = true;
        if (manual) UpdateStatus = Loc.Get("Update_Checking");
        try
        {
            using var http = FluentSetup.Builder.Core.Updates.FluentSetupUpdates.CreateClient();
            var latest = await FluentSetup.Builder.Core.Updates.FluentSetupUpdates.GetLatestAsync(http, includePrerelease: false, CancellationToken.None);
            Settings.LastUpdateCheck = DateTimeOffset.Now;
            Settings.Save();
            var current = FluentSetup.Builder.Core.Updates.FluentSetupUpdates.CurrentVersion;
            UpdateRelease = latest is not null && FluentSetup.Builder.Core.Updates.FluentSetupUpdates.CompareVersions(latest.Version, current) > 0 ? latest : null;
            UpdateStatus = UpdateRelease is null ? Loc.Format("Update_UpToDate", current) : UpdateMessage;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // An automatic check stays quiet offline; a manual one says why.
            if (manual) UpdateStatus = Loc.Format("Update_CheckFailed", ex.Message);
        }
        finally
        {
            IsUpdating = false;
        }
    }

    [RelayCommand]
    private static void OpenReleaseNotes(string? url) => Ui.OpenUrl(string.IsNullOrEmpty(url) ? FluentSetup.Builder.Core.Updates.FluentSetupUpdates.ReleasesPage : url);

    /// <summary>Saves the project if needed, downloads and verifies the new Setup.exe, runs it with a progress window and closes.</summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (UpdateRelease is not { Setup: { } asset } release || IsUpdating) return;
        if (FluentSetup.Builder.Core.Updates.FluentSetupUpdates.InstalledFolder() is null)
        {
            // A copy that Setup.exe did not install (a build from source, a copied folder): show where to get it.
            UpdateStatus = Loc.Get("Update_NotInstalled");
            Ui.OpenUrl(release.PageUrl);
            return;
        }
        if (IsBuilding || !await ConfirmCloseProjectAsync()) return;
        IsUpdating = true;
        try
        {
            using var http = FluentSetup.Builder.Core.Updates.FluentSetupUpdates.CreateClient();
            var progress = new Progress<double>(f => UpdateStatus = Loc.Format("Update_Downloading", release.Version, (int)(f * 100)));
            var setup = await FluentSetup.Builder.Core.Updates.FluentSetupUpdates.DownloadVerifiedAsync(http, release, asset, progress, CancellationToken.None);
            IsDirty = false;  // the user saved or discarded the changes above
            FluentSetup.Builder.Core.Updates.FluentSetupUpdates.StartSetup(setup, relaunch: true);
            Ui.Window?.Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            UpdateStatus = Loc.Format("Update_Failed", ex.Message);
        }
        finally
        {
            IsUpdating = false;
        }
    }
}
