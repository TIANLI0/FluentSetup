using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Prerequisites;
using FluentSetup.Builder.Core.Validation;
using FluentSetup.Builder.Services;

namespace FluentSetup.Builder.ViewModels;

public sealed partial class FieldErrors
{
    // App info, install, appearance, signing (1.2)
    public string AppUserModelId => Get("app.appUserModelId");
    public string AppToastActivatorClsid => Get("app.toastActivatorClsid");
    public string InstallAppPaths => Get("install.appPaths");
    public string InstallFilesInUse => Under("install.restartManager", "install.lockedFiles");
    public string UiBanner => Under("ui.banner");
    public string SignUninstaller => Get("signing.signUninstaller");

    // Prerequisites
    public string VcRedist => Under("requirements.vcRedist");
    public string CustomRequirements => Get("requirements.custom");

    // Lists (the items' own errors are shown on their cards)
    public string Shortcuts => Get("shortcuts");
    public string Associations => Get("associations");
    public string Protocols => Get("protocols");
    public string Registry => Get("registry");
    public string EnvironmentVariables => Get("environment");
    public string Services => Get("services");
    public string Firewall => Get("firewall");
    public string Inputs => Get("inputs");
    public string ConfigFiles => Get("configFiles");
}

/// <summary>
/// The 1.2 settings: notifications identity, App Paths, files in use, banner, uninstaller signing, Visual C++ and custom
/// prerequisites, shortcuts, file types, URL schemes, registry, environment, services, firewall, inputs and config files.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly string[] RestartManagerModes = ["auto", "off"];
    private static readonly string[] LockedFilesModes = ["fail", "reboot"];
    private static readonly string[] VcArchitectures = ["", "x64", "x86", "arm64"];

    // ---- App info ------------------------------------------------------------------------------------------------
    [ObservableProperty] public partial string AppUserModelId { get; set; } = "";
    [ObservableProperty] public partial string ToastActivatorClsid { get; set; } = "";

    // ---- Install -------------------------------------------------------------------------------------------------
    [ObservableProperty] public partial bool InstallAppPaths { get; set; }
    [ObservableProperty] public partial int RestartManagerIndex { get; set; }
    [ObservableProperty] public partial int LockedFilesIndex { get; set; }

    // ---- Appearance ----------------------------------------------------------------------------------------------
    [ObservableProperty] public partial string Banner { get; set; } = "";
    [ObservableProperty] public partial Microsoft.UI.Xaml.Media.ImageSource? BannerImage { get; set; }
    [ObservableProperty] public partial string BannerInfo { get; set; } = "";

    // ---- Signing -------------------------------------------------------------------------------------------------
    [ObservableProperty] public partial bool SignUninstaller { get; set; } = true;

    // ---- Prerequisites -------------------------------------------------------------------------------------------
    [ObservableProperty] public partial bool VcRedistRequired { get; set; }
    [ObservableProperty] public partial string VcRedistMinVersion { get; set; } = "";
    [ObservableProperty] public partial int VcRedistArchitectureIndex { get; set; }
    public PrerequisiteSourceEditor VcRedistSource { get; } = new();
    public ObservableCollection<CustomRequirementRow> CustomRows { get; } = [];

    // ---- Version lists from Microsoft (shared by every version box) ---------------------------------------------
    public VersionCatalogViewModel WebView2Versions { get; } = new(VersionProduct.WebView2);
    public VersionCatalogViewModel WindowsAppRuntimeVersions { get; } = new(VersionProduct.WindowsAppRuntime);
    public VersionCatalogViewModel DotNetVersions { get; } = new(VersionProduct.DotNet);
    public VersionCatalogViewModel VcRedistVersions { get; } = new(VersionProduct.VcRedist);

    // ---- Shortcuts and file types, system, inputs and config files -----------------------------------------------
    public ObservableCollection<ShortcutRow> ShortcutRows { get; } = [];
    public ObservableCollection<AssociationRow> AssociationRows { get; } = [];
    public ObservableCollection<ProtocolRow> ProtocolRows { get; } = [];
    public ObservableCollection<RegistryKeyRow> RegistryRows { get; } = [];
    public ObservableCollection<EnvironmentRow> EnvironmentRows { get; } = [];
    public ObservableCollection<ServiceRow> ServiceRows { get; } = [];
    public ObservableCollection<FirewallRow> FirewallRows { get; } = [];
    public ObservableCollection<InputRow> InputRows { get; } = [];
    public ObservableCollection<ConfigFileRow> ConfigFileRows { get; } = [];

    /// <summary>.ico, .exe and .dll files among the collected files (icon suggestions).</summary>
    public ObservableCollection<string> IconChoices { get; } = [];

    public bool HasNoCustom => CustomRows.Count == 0;
    public bool HasNoShortcuts => ShortcutRows.Count == 0;
    public bool HasNoAssociations => AssociationRows.Count == 0;
    public bool HasNoProtocols => ProtocolRows.Count == 0;
    public bool HasNoRegistry => RegistryRows.Count == 0;
    public bool HasNoEnvironment => EnvironmentRows.Count == 0;
    public bool HasNoServices => ServiceRows.Count == 0;
    public bool HasNoFirewall => FirewallRows.Count == 0;
    public bool HasNoInputs => InputRows.Count == 0;
    public bool HasNoConfigFiles => ConfigFileRows.Count == 0;

    /// <summary>Services and firewall rules need a per-machine installation.</summary>
    public bool IsUserScope => ScopeIndex == 0;
    /// <summary>The user chooses: services and firewall rules only apply to per-machine installations.</summary>
    public bool IsEitherScope => ScopeIndex == 2;
    /// <summary>Replacing files at the next restart needs administrator rights.</summary>
    public bool LockedFilesNeedMachine => LockedFilesIndex == 1 && ScopeIndex != 1;

    partial void OnScopeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsUserScope));
        OnPropertyChanged(nameof(IsEitherScope));
        OnPropertyChanged(nameof(LockedFilesNeedMachine));
    }

    partial void OnLockedFilesIndexChanged(int value) => OnPropertyChanged(nameof(LockedFilesNeedMachine));

    partial void OnBannerChanged(string value)
    {
        if (!_loading) UpdateBannerImage();
    }

    /// <summary>Called from <see cref="InitAdvanced"/>.</summary>
    private void InitIntegration()
    {
        VcRedistSource.Owner = this;
        Watch(CustomRows, nameof(HasNoCustom));
        Watch(ShortcutRows, nameof(HasNoShortcuts));
        Watch(AssociationRows, nameof(HasNoAssociations));
        Watch(ProtocolRows, nameof(HasNoProtocols));
        Watch(RegistryRows, nameof(HasNoRegistry));
        Watch(EnvironmentRows, nameof(HasNoEnvironment));
        Watch(ServiceRows, nameof(HasNoServices));
        Watch(FirewallRows, nameof(HasNoFirewall));
        Watch(InputRows, nameof(HasNoInputs));
        Watch(ConfigFileRows, nameof(HasNoConfigFiles));
    }

    /// <summary>Called from <see cref="LoadAdvanced"/>.</summary>
    private void LoadIntegration(SetupProject p)
    {
        AppUserModelId = p.App.AppUserModelId ?? "";
        ToastActivatorClsid = p.App.ToastActivatorClsid ?? "";
        InstallAppPaths = p.Install.AppPaths;
        RestartManagerIndex = TextForms.Find(RestartManagerModes, p.Install.RestartManager);
        LockedFilesIndex = TextForms.Find(LockedFilesModes, p.Install.LockedFiles);
        Banner = p.Ui.Banner ?? "";
        SignUninstaller = p.Signing.SignUninstaller;

        var r = p.Requirements ?? new RequirementSettings();
        var vc = r.VcRedist ?? new VcRedistRequirement();
        VcRedistRequired = vc.Required;
        VcRedistMinVersion = vc.MinVersion ?? "";
        VcRedistArchitectureIndex = TextForms.Find(VcArchitectures, vc.Architecture ?? "");
        VcRedistSource.LoadSource(vc);
        Fill(CustomRows, Each(r.Custom).Select(CustomRequirementRow.From), this);

        Fill(ShortcutRows, Each(p.Shortcuts).Select(ShortcutRow.From), this);
        Fill(AssociationRows, Each(p.Associations).Select(AssociationRow.From), this);
        Fill(ProtocolRows, Each(p.Protocols).Select(ProtocolRow.From), this);
        Fill(RegistryRows, Each(p.Registry).Select(RegistryKeyRow.From), this);
        Fill(EnvironmentRows, Each(p.Environment).Select(EnvironmentRow.From), this);
        Fill(ServiceRows, Each(p.Services).Select(ServiceRow.From), this);
        Fill(FirewallRows, Each(p.Firewall).Select(FirewallRow.From), this);
        Fill(InputRows, Each(p.Inputs).Select(InputRow.From), this);
        Fill(ConfigFileRows, Each(p.ConfigFiles).Select(ConfigFileRow.From), this);
        UpdateBannerImage();
    }

    /// <summary>Called from <see cref="ApplyAdvanced"/>: writes the editors onto <paramref name="p"/>, a copy of the loaded project.</summary>
    private void ApplyIntegration(SetupProject p)
    {
        p.App.AppUserModelId = TextForms.Keep(AppUserModelId, p.App.AppUserModelId);
        p.App.ToastActivatorClsid = TextForms.Keep(ToastActivatorClsid, p.App.ToastActivatorClsid);
        p.Install.AppPaths = InstallAppPaths;
        p.Install.RestartManager = TextForms.Pick(RestartManagerModes, RestartManagerIndex, p.Install.RestartManager);
        p.Install.LockedFiles = TextForms.Pick(LockedFilesModes, LockedFilesIndex, p.Install.LockedFiles);
        p.Ui.Banner = TextForms.Keep(Banner, p.Ui.Banner);
        p.Signing.SignUninstaller = SignUninstaller;

        var r = p.Requirements ??= new RequirementSettings();
        var vc = r.VcRedist ??= new VcRedistRequirement();
        vc.Required = VcRedistRequired;
        vc.MinVersion = TextForms.Keep(VcRedistMinVersion, vc.MinVersion);
        vc.Architecture = TextForms.Pick(VcArchitectures, VcRedistArchitectureIndex, vc.Architecture);
        VcRedistSource.ApplySource(vc);
        r.Custom = CustomRows.Select(c => c.ToModel()).ToList();

        p.Shortcuts = ShortcutRows.Select(s => s.ToModel()).ToList();
        p.Associations = AssociationRows.Select(a => a.ToModel()).ToList();
        p.Protocols = ProtocolRows.Select(x => x.ToModel()).ToList();
        p.Registry = RegistryRows.Select(x => x.ToModel()).ToList();
        p.Environment = EnvironmentRows.Select(x => x.ToModel()).ToList();
        p.Services = ServiceRows.Select(x => x.ToModel()).ToList();
        p.Firewall = FirewallRows.Select(x => x.ToModel()).ToList();
        p.Inputs = InputRows.Select(x => x.ToModel()).ToList();
        p.ConfigFiles = ConfigFileRows.Select(x => x.ToModel()).ToList();
    }

    /// <summary>Every row with an ifOption (kept pointing at renamed options, refreshed when the options change).</summary>
    private IEnumerable<GatedRow> GatedRows() =>
        ActionRows.Cast<GatedRow>().Concat(ShortcutRows).Concat(AssociationRows).Concat(ProtocolRows).Concat(RegistryRows)
                  .Concat(EnvironmentRows).Concat(FirewallRows).Concat(ConfigFileRows);

    private void UpdateIntegrationRowErrors()
    {
        void Set<T>(IList<T> rows, string prefix) where T : EditorRow
        {
            for (var i = 0; i < rows.Count; i++) rows[i].Error = Errors.Under($"{prefix}[{i}]");
        }
        Set(CustomRows, "requirements.custom");
        Set(ShortcutRows, "shortcuts");
        Set(AssociationRows, "associations");
        Set(ProtocolRows, "protocols");
        Set(RegistryRows, "registry");
        Set(EnvironmentRows, "environment");
        Set(ServiceRows, "services");
        Set(FirewallRows, "firewall");
        Set(InputRows, "inputs");
        Set(ConfigFileRows, "configFiles");
    }

    private void RemoveIntegrationRow(EditorRow row)
    {
        switch (row)
        {
            case ChildRow child: child.Parent?.RemoveChild(child); break;
            case CustomRequirementRow c: CustomRows.Remove(c); break;
            case ShortcutRow s: ShortcutRows.Remove(s); break;
            case AssociationRow a: AssociationRows.Remove(a); break;
            case ProtocolRow x: ProtocolRows.Remove(x); break;
            case RegistryKeyRow x: RegistryRows.Remove(x); break;
            case EnvironmentRow x: EnvironmentRows.Remove(x); break;
            case ServiceRow x: ServiceRows.Remove(x); break;
            case FirewallRow x: FirewallRows.Remove(x); break;
            case InputRow x: InputRows.Remove(x); break;
            case ConfigFileRow x: ConfigFileRows.Remove(x); break;
        }
    }

    private void MoveIntegrationRow(EditorRow row, int delta)
    {
        switch (row)
        {
            case CustomRequirementRow c: Move(CustomRows, c, delta); break;
            case ShortcutRow s: Move(ShortcutRows, s, delta); break;
            case AssociationRow a: Move(AssociationRows, a, delta); break;
            case ProtocolRow x: Move(ProtocolRows, x, delta); break;
            case RegistryKeyRow x: Move(RegistryRows, x, delta); break;
            case EnvironmentRow x: Move(EnvironmentRows, x, delta); break;
            case ServiceRow x: Move(ServiceRows, x, delta); break;
            case FirewallRow x: Move(FirewallRows, x, delta); break;
            case InputRow x: Move(InputRows, x, delta); break;
            case ConfigFileRow x: Move(ConfigFileRows, x, delta); break;
        }
    }

    /// <summary>Icon suggestions from the collected files.</summary>
    private void SyncIconChoices(IEnumerable<string> files)
    {
        var icons = files.Where(f => f.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Take(500).ToList();
        if (icons.SequenceEqual(IconChoices, StringComparer.Ordinal)) return;
        IconChoices.Clear();
        foreach (var f in icons) IconChoices.Add(f);
    }

    /// <summary>"Software\Publisher\App" as a starting point for a new registry key.</summary>
    private string SuggestedRegistryKey()
    {
        var publisher = Publisher.Trim();
        var app = AppName.Trim();
        return publisher.Length > 0 && app.Length > 0 && !publisher.Contains('$', StringComparison.Ordinal) && !app.Contains('$', StringComparison.Ordinal)
            ? $@"Software\{publisher}\{app}"
            : "";
    }

    [RelayCommand] private void AddShortcut() => AddRow(ShortcutRows, ShortcutRow.From(new ShortcutSettings()));
    [RelayCommand] private void AddAssociation() => AddRow(AssociationRows, AssociationRow.From(new FileAssociationSettings()));
    [RelayCommand] private void AddProtocol() => AddRow(ProtocolRows, ProtocolRow.From(new ProtocolSettings()));
    [RelayCommand] private void AddEnvironment() => AddRow(EnvironmentRows, EnvironmentRow.From(new EnvironmentVariableSettings()));
    [RelayCommand] private void AddService() => AddRow(ServiceRows, ServiceRow.From(new ServiceSettings()));
    [RelayCommand] private void AddFirewall() => AddRow(FirewallRows, FirewallRow.From(new FirewallRuleSettings { Name = AppName.Trim() }));

    [RelayCommand]
    private void AddRegistry() => AddRow(RegistryRows, RegistryKeyRow.From(new RegistryEntrySettings { Key = SuggestedRegistryKey(), Values = [new RegistryValueSettings()] }));

    [RelayCommand]
    private void AddInput() => AddRow(InputRows, InputRow.From(new InputSettings { Id = UniqueId("input", InputRows.Select(i => i.Id)) }));

    [RelayCommand]
    private void AddConfigFile() => AddRow(ConfigFileRows, ConfigFileRow.From(new ConfigFileSettings { Path = "config.json", Values = [new ConfigValueSettings()] }));

    [RelayCommand]
    private void AddCustomRequirement() => AddRow(CustomRows, CustomRequirementRow.From(new CustomRequirement
    {
        Id = UniqueId("prerequisite", CustomRows.Select(c => c.Id)),
        Detect = new RequirementDetection { Registry = new RegistryVersionCondition() },
    }));

    /// <summary>A new CLSID for the toast activator ("{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}").</summary>
    [RelayCommand]
    private void GenerateToastActivatorClsid() => ToastActivatorClsid = Guid.NewGuid().ToString("B").ToUpperInvariant();

    [RelayCommand]
    private async Task BrowseBannerAsync()
    {
        if (await Ui.PickFileAsync(".png", ".jpg", ".jpeg", ".bmp") is { } file) Banner = MakeRelative(file);
    }

    [RelayCommand]
    private void ClearBanner() => Banner = "";

    /// <summary>Shows the banner and its size (pixels and bytes) under its card.</summary>
    private async void UpdateBannerImage()
    {
        var banner = Banner.Trim();
        if (banner.Length == 0)
        {
            BannerImage = null;
            BannerInfo = Loc.Get("Banner_None");
            return;
        }
        var path = ProjectValidator.Resolve(ProjectDirectory, ExpandedModel().Ui.Banner);
        if (!File.Exists(path))
        {
            BannerImage = null;
            BannerInfo = Loc.Format("Banner_Missing", banner);
            return;
        }
        try
        {
            var bytes = new FileInfo(path).Length;
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var frame = await decoder.GetFrameAsync(0);
            var bitmap = await frame.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            var source = new Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            if (Banner.Trim() != banner) return;  // changed meanwhile
            BannerImage = source;
            BannerInfo = Loc.Format("Banner_Info", decoder.PixelWidth, decoder.PixelHeight, PackageBuilder.Format(bytes));
        }
        catch (Exception)
        {
            BannerImage = null;
            BannerInfo = Loc.Get("Banner_Unreadable");  // validation says why
        }
    }
}
