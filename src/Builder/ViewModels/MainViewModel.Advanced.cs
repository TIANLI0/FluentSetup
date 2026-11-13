using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Validation;
using FluentSetup.Builder.Services;

namespace FluentSetup.Builder.ViewModels;

/// <summary>
/// Prerequisites, options and actions, upgrade and uninstall cleanup, variants, localization and variables. Every
/// value is loaded from the project and written back onto a copy of it, so unchanged values are saved exactly as read.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly string[] LicenseModes = ["firstInstall", "always"];
    /// <summary>"" (not required) and the .NET Framework versions the installer can detect.</summary>
    public static readonly string[] NetFrameworkVersions = ["", .. ExtensionValidator.KnownNetFrameworkVersions];

    // ---- Install -------------------------------------------------------------------------------------------------
    [ObservableProperty] public partial string StartMenuFolder { get; set; } = "";
    [ObservableProperty] public partial string AddToPathText { get; set; } = "";
    [ObservableProperty] public partial string NgenText { get; set; } = "";

    // ---- Upgrade -------------------------------------------------------------------------------------------------
    [ObservableProperty] public partial bool AdoptExistingInstall { get; set; }
    [ObservableProperty] public partial string LegacyUninstallKeysText { get; set; } = "";
    [ObservableProperty] public partial string CloseProcessesText { get; set; } = "";
    [ObservableProperty] public partial bool HasCloseCommand { get; set; }
    [ObservableProperty] public partial string CloseCommandRun { get; set; } = "";
    [ObservableProperty] public partial string CloseCommandArgumentsText { get; set; } = "";
    [ObservableProperty] public partial string RelaunchArgumentsText { get; set; } = "";
    [ObservableProperty] public partial string CleanupScheduledTasksText { get; set; } = "";
    [ObservableProperty] public partial string CleanupRunValuesText { get; set; } = "";
    [ObservableProperty] public partial string CleanupShortcutsText { get; set; } = "";
    [ObservableProperty] public partial string CleanupUninstallKeysText { get; set; } = "";
    [ObservableProperty] public partial string CleanupFilesText { get; set; } = "";

    // ---- Built-in updates (update) ------------------------------------------------------------------------------
    [ObservableProperty] public partial int UpdateSourceIndex { get; set; }  // none, github, feed
    [ObservableProperty] public partial string UpdateGitHub { get; set; } = "";
    [ObservableProperty] public partial string UpdateFeed { get; set; } = "";
    [ObservableProperty] public partial string UpdateAsset { get; set; } = "";
    [ObservableProperty] public partial bool UpdatePrerelease { get; set; }
    [ObservableProperty] public partial string UpdateChecksums { get; set; } = "";
    [ObservableProperty] public partial string UpdateSigner { get; set; } = "";
    public bool UpdateIsOn => UpdateSourceIndex != 0;
    public bool UpdateIsGitHub => UpdateSourceIndex == 1;
    public bool UpdateIsFeed => UpdateSourceIndex == 2;

    partial void OnUpdateSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(UpdateIsOn));
        OnPropertyChanged(nameof(UpdateIsGitHub));
        OnPropertyChanged(nameof(UpdateIsFeed));
    }

    // ---- Uninstall -----------------------------------------------------------------------------------------------
    [ObservableProperty] public partial string UninstallDataText { get; set; } = "";
    [ObservableProperty] public partial string UninstallScheduledTasksText { get; set; } = "";
    [ObservableProperty] public partial string UninstallRunValuesText { get; set; } = "";
    [ObservableProperty] public partial string UninstallShortcutsText { get; set; } = "";
    [ObservableProperty] public partial string UninstallRegistryKeysText { get; set; } = "";
    [ObservableProperty] public partial string UserDataPathsText { get; set; } = "";
    [ObservableProperty] public partial bool UserDataDefault { get; set; }
    [ObservableProperty] public partial string ElevatedIfExistsText { get; set; } = "";
    [ObservableProperty] public partial string ElevatedTraceSessionsText { get; set; } = "";
    [ObservableProperty] public partial string ElevatedScheduledTasksText { get; set; } = "";
    [ObservableProperty] public partial string ElevatedDataText { get; set; } = "";
    [ObservableProperty] public partial string ElevatedRegistryKeysText { get; set; } = "";
    public ObservableCollection<ServiceAccessRow> ServiceAccessRows { get; } = [];

    // ---- Prerequisites -------------------------------------------------------------------------------------------
    [ObservableProperty] public partial int NetFrameworkIndex { get; set; }
    [ObservableProperty] public partial bool HasNetFrameworkSetup { get; set; }
    [ObservableProperty] public partial bool WebView2Required { get; set; }
    [ObservableProperty] public partial string WebView2MinVersion { get; set; } = "";
    [ObservableProperty] public partial bool WindowsAppRuntimeRequired { get; set; }
    [ObservableProperty] public partial string WindowsAppRuntimeVersion { get; set; } = "";
    public PrerequisiteSourceEditor NetFrameworkSource { get; } = new();
    public PrerequisiteSourceEditor WebView2Source { get; } = new();
    public PrerequisiteSourceEditor WindowsAppRuntimeSource { get; } = new();
    public ObservableCollection<DotNetRow> DotNetRows { get; } = [];
    public IReadOnlyList<string> NetFrameworkChoices { get; } = [Loc.Get("Prereq_NotRequired"), .. ExtensionValidator.KnownNetFrameworkVersions];

    // ---- Options and actions -------------------------------------------------------------------------------------
    public ObservableCollection<OptionRow> OptionRows { get; } = [];
    public ObservableCollection<ActionRow> ActionRows { get; } = [];
    /// <summary>"(none)" followed by the option ids, for actions[].ifOption.</summary>
    public ObservableCollection<string> IfOptionChoices { get; } = [];

    // ---- Files: variants -----------------------------------------------------------------------------------------
    public ObservableCollection<VariantRow> VariantRows { get; } = [];
    [ObservableProperty] public partial string OutputVariantsText { get; set; } = "";

    // ---- Appearance: license and languages -----------------------------------------------------------------------
    [ObservableProperty] public partial int LicenseModeIndex { get; set; }
    [ObservableProperty] public partial string LicensesText { get; set; } = "";
    [ObservableProperty] public partial string LocalizationFolder { get; set; } = "";
    [ObservableProperty] public partial string StringsText { get; set; } = "";

    // ---- Variables -----------------------------------------------------------------------------------------------
    public ObservableCollection<VariableRow> VariableRows { get; } = [];

    public bool HasNoDotNet => DotNetRows.Count == 0;
    public bool HasNoOptions => OptionRows.Count == 0;
    public bool HasNoActions => ActionRows.Count == 0;
    public bool HasVariants => VariantRows.Count > 0;
    public bool HasNoServiceAccess => ServiceAccessRows.Count == 0;
    public bool HasNoVariables => VariableRows.Count == 0;

    private static IEnumerable<T> Each<T>(IEnumerable<T>? items) => items ?? [];

    /// <summary>The selection, or the stored (possibly unknown) value while the selection is still the one it loads as.</summary>
    private static string PickFrom(string[] values, int index, string? original, int fallback) =>
        index == TextForms.IndexOf(values, original, fallback) && original is not null ? original : values[Math.Clamp(index, 0, values.Length - 1)];

    /// <summary>Called from the constructor: list edits mark the project dirty.</summary>
    private void InitAdvanced()
    {
        foreach (var editor in new[] { NetFrameworkSource, WebView2Source, WindowsAppRuntimeSource }) editor.Owner = this;
        Watch(DotNetRows, nameof(HasNoDotNet));
        Watch(OptionRows, nameof(HasNoOptions), SyncIfOptionChoices);
        Watch(ActionRows, nameof(HasNoActions));
        Watch(VariantRows, nameof(HasVariants), () => { if (!_loading) _ = RefreshFilesAsync(); });
        Watch(ServiceAccessRows, nameof(HasNoServiceAccess));
        Watch(VariableRows, nameof(HasNoVariables));
        InitIntegration();
        SyncIfOptionChoices();
    }

    private void Watch(INotifyCollectionChanged list, string countProperty, Action? also = null) =>
        list.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(countProperty);
            also?.Invoke();
            Touch();
        };

    private static void Fill<T>(ObservableCollection<T> rows, IEnumerable<T> items, MainViewModel owner) where T : EditorRow
    {
        rows.Clear();
        foreach (var row in items)
        {
            row.Owner = owner;
            rows.Add(row);
        }
    }

    private void LoadAdvanced(SetupProject p)
    {
        var inst = p.Install;
        StartMenuFolder = inst.StartMenuFolder ?? "";
        AddToPathText = TextForms.Lines(inst.AddToPath);
        NgenText = TextForms.Lines(inst.Ngen);
        AdoptExistingInstall = inst.AdoptExistingInstall;
        LegacyUninstallKeysText = TextForms.Lines(inst.LegacyUninstallKeys);
        CloseProcessesText = TextForms.Lines(inst.CloseProcesses);
        HasCloseCommand = inst.CloseCommand is not null;
        CloseCommandRun = inst.CloseCommand?.Run ?? "";
        CloseCommandArgumentsText = TextForms.Lines(inst.CloseCommand?.Arguments);
        RelaunchArgumentsText = TextForms.Lines(inst.RelaunchArguments);
        var c = inst.Cleanup ?? new InstallCleanupSettings();
        CleanupScheduledTasksText = TextForms.Lines(c.RemoveScheduledTasks);
        CleanupRunValuesText = TextForms.Lines(c.RemoveRunValues);
        CleanupShortcutsText = TextForms.Lines(c.RemoveShortcuts);
        CleanupUninstallKeysText = TextForms.Lines(c.RemoveUninstallKeys);
        CleanupFilesText = TextForms.Lines(c.RemoveFiles);

        var u = p.Uninstall ?? new UninstallSettings();
        UninstallDataText = TextForms.Lines(u.RemoveData);
        UninstallScheduledTasksText = TextForms.Lines(u.RemoveScheduledTasks);
        UninstallRunValuesText = TextForms.Lines(u.RemoveRunValues);
        UninstallShortcutsText = TextForms.Lines(u.RemoveShortcuts);
        UninstallRegistryKeysText = TextForms.Lines(u.RemoveRegistryKeys);
        UserDataPathsText = TextForms.Lines(u.UserData?.Paths);
        UserDataDefault = u.UserData?.Default ?? false;
        var e = u.Elevated ?? new ElevatedCleanupSettings();
        ElevatedIfExistsText = TextForms.Lines(e.IfExists);
        ElevatedTraceSessionsText = TextForms.Lines(e.TraceSessions);
        ElevatedScheduledTasksText = TextForms.Lines(e.ScheduledTasks);
        ElevatedDataText = TextForms.Lines(e.RemoveData);
        ElevatedRegistryKeysText = TextForms.Lines(e.RemoveRegistryKeys);
        Fill(ServiceAccessRows, Each(e.ServiceAccess).Select(ServiceAccessRow.From), this);

        var r = p.Requirements ?? new RequirementSettings();
        NetFrameworkIndex = TextForms.Find(NetFrameworkVersions, r.NetFramework);
        HasNetFrameworkSetup = r.NetFrameworkSetup is not null;
        NetFrameworkSource.LoadSource(r.NetFrameworkSetup ?? new PrerequisiteSource());
        var wv = r.WebView2 ?? new WebView2Settings();
        WebView2Required = wv.Required;
        WebView2MinVersion = wv.MinVersion ?? "";
        WebView2Source.LoadSource(wv);
        var war = r.WindowsAppRuntime ?? new WindowsAppRuntimeRequirement();
        WindowsAppRuntimeRequired = war.Required;
        WindowsAppRuntimeVersion = war.Version ?? "";
        WindowsAppRuntimeSource.LoadSource(war);
        Fill(DotNetRows, Each(r.DotNet).Select(DotNetRow.From), this);

        Fill(OptionRows, Each(p.Options).Select(OptionRow.From), this);
        Fill(ActionRows, Each(p.Actions).Select(ActionRow.From), this);
        SyncIfOptionChoices();

        Fill(VariantRows, Each(p.Files.Variants).Select(VariantRow.From), this);
        OutputVariantsText = TextForms.Csv(p.Output.Variants);

        LicenseModeIndex = TextForms.Find(LicenseModes, p.Ui.LicenseMode);
        LicensesText = TextForms.Localized(p.Ui.Licenses);
        LocalizationFolder = p.Ui.Localization ?? "";
        StringsText = TextForms.Strings(p.Ui.Strings);

        Fill(VariableRows, Each(p.Variables).Select(kv => new VariableRow { Name = kv.Key, Value = kv.Value ?? "" }), this);

        var upd = p.Update;
        UpdateSourceIndex = !string.IsNullOrWhiteSpace(upd.GitHub) ? 1 : !string.IsNullOrWhiteSpace(upd.Feed) ? 2 : 0;
        UpdateGitHub = upd.GitHub ?? "";
        UpdateFeed = upd.Feed ?? "";
        UpdateAsset = upd.Asset ?? "";
        UpdatePrerelease = upd.Prerelease;
        UpdateChecksums = upd.Checksums ?? "";
        UpdateSigner = upd.Signer ?? "";

        LoadIntegration(p);
    }

    /// <summary>Writes the editors onto <paramref name="p"/>, a copy of the loaded project.</summary>
    private void ApplyAdvanced(SetupProject p)
    {
        var inst = p.Install;
        inst.StartMenuFolder = TextForms.Keep(StartMenuFolder, inst.StartMenuFolder);
        inst.AddToPath = TextForms.FromLines(AddToPathText, inst.AddToPath);
        inst.Ngen = TextForms.FromLines(NgenText, inst.Ngen);
        inst.AdoptExistingInstall = AdoptExistingInstall;
        inst.LegacyUninstallKeys = TextForms.FromLines(LegacyUninstallKeysText, inst.LegacyUninstallKeys);
        inst.CloseProcesses = TextForms.FromLines(CloseProcessesText, inst.CloseProcesses);
        inst.CloseCommand = HasCloseCommand
            ? new CommandSettings
            {
                Run = TextForms.Keep(CloseCommandRun, inst.CloseCommand?.Run),
                Arguments = TextForms.FromLines(CloseCommandArgumentsText, inst.CloseCommand?.Arguments),
            }
            : null;
        inst.RelaunchArguments = TextForms.FromLines(RelaunchArgumentsText, inst.RelaunchArguments);
        var c = inst.Cleanup ??= new InstallCleanupSettings();
        c.RemoveScheduledTasks = TextForms.FromLines(CleanupScheduledTasksText, c.RemoveScheduledTasks);
        c.RemoveRunValues = TextForms.FromLines(CleanupRunValuesText, c.RemoveRunValues);
        c.RemoveShortcuts = TextForms.FromLines(CleanupShortcutsText, c.RemoveShortcuts);
        c.RemoveUninstallKeys = TextForms.FromLines(CleanupUninstallKeysText, c.RemoveUninstallKeys);
        c.RemoveFiles = TextForms.FromLines(CleanupFilesText, c.RemoveFiles);

        var u = p.Uninstall ??= new UninstallSettings();
        u.RemoveData = TextForms.FromLines(UninstallDataText, u.RemoveData);
        u.RemoveScheduledTasks = TextForms.FromLines(UninstallScheduledTasksText, u.RemoveScheduledTasks);
        u.RemoveRunValues = TextForms.FromLines(UninstallRunValuesText, u.RemoveRunValues);
        u.RemoveShortcuts = TextForms.FromLines(UninstallShortcutsText, u.RemoveShortcuts);
        u.RemoveRegistryKeys = TextForms.FromLines(UninstallRegistryKeysText, u.RemoveRegistryKeys);
        var ud = u.UserData ??= new UserDataSettings();
        ud.Paths = TextForms.FromLines(UserDataPathsText, ud.Paths);
        ud.Default = UserDataDefault;
        var e = u.Elevated ??= new ElevatedCleanupSettings();
        e.IfExists = TextForms.FromLines(ElevatedIfExistsText, e.IfExists);
        e.TraceSessions = TextForms.FromLines(ElevatedTraceSessionsText, e.TraceSessions);
        e.ScheduledTasks = TextForms.FromLines(ElevatedScheduledTasksText, e.ScheduledTasks);
        e.RemoveData = TextForms.FromLines(ElevatedDataText, e.RemoveData);
        e.RemoveRegistryKeys = TextForms.FromLines(ElevatedRegistryKeysText, e.RemoveRegistryKeys);
        e.ServiceAccess = ServiceAccessRows.Select(s => s.ToModel()).ToList();

        var r = p.Requirements ??= new RequirementSettings();
        r.NetFramework = TextForms.Pick(NetFrameworkVersions, NetFrameworkIndex, r.NetFramework);
        if (HasNetFrameworkSetup)
        {
            var setup = r.NetFrameworkSetup ??= new PrerequisiteSource();
            NetFrameworkSource.ApplySource(setup);
        }
        else
        {
            r.NetFrameworkSetup = null;
        }
        var wv = r.WebView2 ??= new WebView2Settings();
        wv.Required = WebView2Required;
        wv.MinVersion = TextForms.Keep(WebView2MinVersion, wv.MinVersion);
        WebView2Source.ApplySource(wv);
        var war = r.WindowsAppRuntime ??= new WindowsAppRuntimeRequirement();
        war.Required = WindowsAppRuntimeRequired;
        war.Version = TextForms.Keep(WindowsAppRuntimeVersion, war.Version);
        WindowsAppRuntimeSource.ApplySource(war);
        r.DotNet = DotNetRows.Select(d => d.ToModel()).ToList();

        p.Options = OptionRows.Select(o => o.ToModel()).ToList();
        p.Actions = ActionRows.Select(a => a.ToModel()).ToList();

        p.Files.Variants = VariantRows.Select(v => v.ToModel()).ToList();
        p.Output.Variants = TextForms.FromCsv(OutputVariantsText, p.Output.Variants);

        p.Ui.LicenseMode = TextForms.Pick(LicenseModes, LicenseModeIndex, p.Ui.LicenseMode);
        p.Ui.Licenses = TextForms.FromLocalized(LicensesText, p.Ui.Licenses);
        p.Ui.Localization = TextForms.Keep(LocalizationFolder, p.Ui.Localization);
        p.Ui.Strings = TextForms.FromStrings(StringsText, p.Ui.Strings);

        var variables = new Dictionary<string, string>();
        foreach (var v in VariableRows.Where(v => v.Name.Trim().Length > 0)) variables[v.Name.Trim()] = v.Value;
        p.Variables = variables;

        // Only the settings of the chosen source are written; the editors keep the others while switching.
        var upd = p.Update;
        bool github = UpdateSourceIndex == 1, feed = UpdateSourceIndex == 2;
        upd.GitHub = github ? TextForms.Keep(UpdateGitHub, upd.GitHub) : "";
        upd.Feed = feed ? TextForms.Keep(UpdateFeed, upd.Feed) : "";
        upd.Asset = github ? TextForms.Keep(UpdateAsset, upd.Asset) : "";
        upd.Prerelease = github && UpdatePrerelease;
        upd.Checksums = github ? TextForms.Keep(UpdateChecksums, upd.Checksums) : "";
        upd.Signer = github || feed ? TextForms.Keep(UpdateSigner, upd.Signer) : "";

        ApplyIntegration(p);
    }

    /// <summary>Shows each list item's own validation errors on its card.</summary>
    private void UpdateRowErrors()
    {
        static void Set<T>(IList<T> rows, string prefix, FieldErrors errors) where T : EditorRow
        {
            for (var i = 0; i < rows.Count; i++) rows[i].Error = errors.Under($"{prefix}[{i}]");
        }
        Set(DotNetRows, "requirements.dotNet", Errors);
        Set(OptionRows, "options", Errors);
        Set(ActionRows, "actions", Errors);
        Set(VariantRows, "files.variants", Errors);
        Set(ServiceAccessRows, "uninstall.elevated.serviceAccess", Errors);
        foreach (var v in VariableRows) v.Error = v.Name.Trim().Length == 0 ? "" : Errors.Under("variables." + v.Name.Trim());
        UpdateIntegrationRowErrors();
    }

    // ---- list editing --------------------------------------------------------------------------------------------

    /// <summary>A row's data changed (view-only properties are filtered out by the row).</summary>
    internal void OnRowChanged(EditorRow row, string property)
    {
        if (_loading) return;
        if (row is VariantRow variant)
        {
            // Only one variant can be the default.
            if (property == nameof(VariantRow.Default) && variant.Default)
            {
                foreach (var other in VariantRows.Where(v => v != variant && v.Default)) other.Default = false;
            }
            if (property == nameof(VariantRow.Source)) _ = RefreshFilesAsync();
        }
        Touch();
    }

    /// <summary>Keeps actions that depend on a renamed option pointing at it.</summary>
    internal void OnOptionRenamed(string oldId, string newId)
    {
        if (_loading) return;
        var from = oldId.Trim();
        var to = newId.Trim();
        if (from.Length > 0 && to.Length > 0 && from != to && !OptionRows.Any(o => o.Id.Trim() == from))
        {
            foreach (var a in GatedRows().Where(a => a.IfOption == from).ToList()) a.IfOption = to;
        }
        SyncIfOptionChoices();
    }

    private void SyncIfOptionChoices()
    {
        var wanted = new List<string> { Loc.Get("Actions_NoOption") };
        wanted.AddRange(OptionRows.Select(o => o.Id.Trim()).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal));
        if (wanted.SequenceEqual(IfOptionChoices, StringComparer.Ordinal)) return;
        IfOptionChoices.Clear();
        foreach (var w in wanted) IfOptionChoices.Add(w);
        foreach (var a in GatedRows()) a.RefreshIfOptionItem();
    }

    internal object? IfOptionItemFor(string id) =>
        IfOptionChoices.Count == 0 ? null : id.Length == 0 ? IfOptionChoices[0] : IfOptionChoices.Skip(1).FirstOrDefault(c => c == id);

    internal string IfOptionIdFor(string item) => IfOptionChoices.Count > 0 && item == IfOptionChoices[0] ? "" : item;

    internal void RemoveRow(EditorRow row)
    {
        switch (row)
        {
            case DotNetRow d: DotNetRows.Remove(d); break;
            case OptionRow o: OptionRows.Remove(o); break;
            case ActionRow a: ActionRows.Remove(a); break;
            case VariantRow v: VariantRows.Remove(v); break;
            case ServiceAccessRow s: ServiceAccessRows.Remove(s); break;
            case VariableRow v: VariableRows.Remove(v); break;
            default: RemoveIntegrationRow(row); break;
        }
        Validate();
    }

    internal void MoveRow(EditorRow row, int delta)
    {
        switch (row)
        {
            case DotNetRow d: Move(DotNetRows, d, delta); break;
            case OptionRow o: Move(OptionRows, o, delta); break;
            case ActionRow a: Move(ActionRows, a, delta); break;
            case VariantRow v: Move(VariantRows, v, delta); break;
            default: MoveIntegrationRow(row, delta); break;
        }
        Validate();
    }

    private static void Move<T>(ObservableCollection<T> rows, T row, int delta)
    {
        var from = rows.IndexOf(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= rows.Count) return;
        rows.Move(from, to);
    }

    private T AddRow<T>(ObservableCollection<T> rows, T row) where T : EditorRow
    {
        row.IsExpanded = true;
        row.Owner = this;
        rows.Add(row);
        Validate();
        return row;
    }

    private static string UniqueId(string stem, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken.Select(t => t.Trim()), StringComparer.Ordinal);
        for (var i = 1; ; i++)
        {
            if (!set.Contains(stem + i)) return stem + i;
        }
    }

    [RelayCommand] private void AddDotNet() => AddRow(DotNetRows, DotNetRow.From(new DotNetRequirement()));
    [RelayCommand] private void AddOption() => AddRow(OptionRows, OptionRow.From(new UserOptionSettings { Id = UniqueId("option", OptionRows.Select(o => o.Id)) }));
    [RelayCommand] private void AddAction() => AddRow(ActionRows, ActionRow.From(new ActionSettings()));
    [RelayCommand] private void AddServiceAccess() => AddRow(ServiceAccessRows, ServiceAccessRow.From(new ServiceAccessSettings()));
    [RelayCommand] private void AddVariable() => AddRow(VariableRows, new VariableRow());

    [RelayCommand]
    private async Task AddVariantAsync()
    {
        var folder = await Ui.PickFolderAsync();
        if (folder is null) return;
        AddRow(VariantRows, VariantRow.From(new VariantSettings
        {
            Id = UniqueId("variant", VariantRows.Select(v => v.Id)),
            Label = new() { ["en-US"] = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) },
            Default = VariantRows.Count == 0,
            Source = MakeRelative(folder),
        }));
    }

    internal async Task BrowseVariantSourceAsync(VariantRow row)
    {
        if (await Ui.PickFolderAsync() is { } folder) row.Source = MakeRelative(folder);
    }

    [RelayCommand]
    private async Task BrowseLocalizationAsync()
    {
        if (await Ui.PickFolderAsync() is { } folder) LocalizationFolder = MakeRelative(folder);
    }
}
