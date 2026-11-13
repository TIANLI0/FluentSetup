using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Services;

namespace FluentSetup.Builder.ViewModels;

/// <summary>A row with a list of rows inside (registry values, choices, config values): edits of those count as its own.</summary>
public abstract partial class ParentRow<TChild> : GatedRow, IRowContainer where TChild : ChildRow
{
    protected ParentRow() => Children.CollectionChanged += OnChildrenChanged;

    /// <summary>The nested rows.</summary>
    protected ObservableCollection<TChild> Children { get; } = [];

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var child in e.NewItems?.OfType<TChild>() ?? [])
        {
            child.Parent = this;
            child.Owner = Owner;
        }
        OnChildrenCountChanged();
        Owner?.OnRowChanged(this, "Children");
    }

    protected virtual void OnChildrenCountChanged()
    {
    }

    protected override void OnOwnerChanged()
    {
        foreach (var child in Children) child.Owner = Owner;
    }

    public void RemoveChild(ChildRow child)
    {
        if (child is TChild c) Children.Remove(c);
    }

    protected void AddChild(TChild child)
    {
        child.IsExpanded = true;
        Children.Add(child);
    }
}

/// <summary>A row that holds rows.</summary>
public interface IRowContainer
{
    void RemoveChild(ChildRow child);
}

/// <summary>A row inside another row; "remove" takes it out of its parent.</summary>
public abstract class ChildRow : EditorRow
{
    public IRowContainer? Parent { get; internal set; }
}

/// <summary>shortcuts[]: another shortcut (another program, arguments, the desktop or the Startup folder).</summary>
public sealed partial class ShortcutRow : GatedRow
{
    internal static readonly string[] Locations = ["startMenu", "desktop", "startup"];
    private ShortcutSettings _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Name { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string Target { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string ArgumentsText { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial string Icon { get; set; } = "";
    [ObservableProperty] public partial double IconIndex { get; set; }
    [ObservableProperty] public partial string WorkingDirectory { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle), nameof(IsStartMenu))] public partial int LocationIndex { get; set; }
    [ObservableProperty] public partial string Folder { get; set; } = "";
    [ObservableProperty] public partial string AppUserModelId { get; set; } = "";

    /// <summary>The Start menu folder only applies to Start menu shortcuts.</summary>
    public bool IsStartMenu => LocationIndex is 0 or -1;
    public string Title => Name.Trim().Length > 0 ? Name.Trim() : Loc.Get("Shortcuts_Untitled");

    public string Subtitle
    {
        get
        {
            var where = LocationIndex is >= 0 and < 3 ? Loc.Get("ShortcutLocation_" + LocationIndex) : _original.Location;
            var target = Target.Trim().Length > 0 ? Target.Trim() : Loc.Get("Shortcuts_MainProgram");
            var command = string.Join(' ', new[] { target }.Concat(TextForms.SplitLines(ArgumentsText)));
            return $"{where} · {command}";
        }
    }

    public static ShortcutRow From(ShortcutSettings s)
    {
        var row = new ShortcutRow
        {
            _original = s,
            Name = s.Name ?? "",
            Target = s.Target ?? "",
            ArgumentsText = TextForms.Lines(s.Arguments),
            Description = s.Description ?? "",
            Icon = s.Icon ?? "",
            IconIndex = s.IconIndex,
            WorkingDirectory = s.WorkingDirectory ?? "",
            Folder = s.Folder ?? "",
            IfOption = s.IfOption ?? "",
            AppUserModelId = s.AppUserModelId ?? "",
        };
        row.LocationIndex = TextForms.Find(Locations, s.Location);
        return row;
    }

    public ShortcutSettings ToModel()
    {
        var o = _original;
        var s = TextForms.Copy(o);
        s.Name = TextForms.Keep(Name, o.Name);
        s.Target = TextForms.Keep(Target, o.Target);
        s.Arguments = TextForms.FromLines(ArgumentsText, o.Arguments);
        s.Description = TextForms.Keep(Description, o.Description);
        s.Icon = TextForms.Keep(Icon, o.Icon);
        s.IconIndex = TextForms.ToInt(IconIndex, o.IconIndex);
        s.WorkingDirectory = TextForms.Keep(WorkingDirectory, o.WorkingDirectory);
        s.Location = TextForms.Pick(Locations, LocationIndex, o.Location);
        s.Folder = TextForms.Keep(Folder, o.Folder);
        s.IfOption = TextForms.Keep(IfOption, o.IfOption);
        s.AppUserModelId = TextForms.Keep(AppUserModelId, o.AppUserModelId);
        return s;
    }
}

/// <summary>associations[]: a file type the app opens.</summary>
public sealed partial class AssociationRow : GatedRow
{
    private FileAssociationSettings _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string ExtensionsText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle))] public partial string ProgId { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial string Icon { get; set; } = "";
    [ObservableProperty] public partial double IconIndex { get; set; }
    [ObservableProperty] public partial string Target { get; set; } = "";
    [ObservableProperty] public partial string ArgumentsText { get; set; } = "";
    [ObservableProperty] public partial string ContentType { get; set; } = "";

    public string Title => Description.Trim().Length > 0 ? Description.Trim() : ProgId.Trim().Length > 0 ? ProgId.Trim() : Loc.Get("Associations_Untitled");

    public string Subtitle
    {
        get
        {
            var extensions = string.Join(", ", TextForms.FromCsv(ExtensionsText, null));
            var progId = ProgId.Trim();
            return string.Join(" · ", new[] { extensions, progId }.Where(t => t.Length > 0));
        }
    }

    public static AssociationRow From(FileAssociationSettings a) => new()
    {
        _original = a,
        ExtensionsText = TextForms.Csv(a.Extensions),
        ProgId = a.ProgId ?? "",
        Description = a.Description ?? "",
        Icon = a.Icon ?? "",
        IconIndex = a.IconIndex,
        Target = a.Target ?? "",
        ArgumentsText = TextForms.Lines(a.Arguments),
        ContentType = a.ContentType ?? "",
        IfOption = a.IfOption ?? "",
    };

    public FileAssociationSettings ToModel()
    {
        var o = _original;
        var a = TextForms.Copy(o);
        a.Extensions = TextForms.FromCsv(ExtensionsText, o.Extensions);
        a.ProgId = TextForms.Keep(ProgId, o.ProgId);
        a.Description = TextForms.Keep(Description, o.Description);
        a.Icon = TextForms.Keep(Icon, o.Icon);
        a.IconIndex = TextForms.ToInt(IconIndex, o.IconIndex);
        a.Target = TextForms.Keep(Target, o.Target);
        a.Arguments = TextForms.FromLines(ArgumentsText, o.Arguments);
        a.ContentType = TextForms.Keep(ContentType, o.ContentType);
        a.IfOption = TextForms.Keep(IfOption, o.IfOption);
        return a;
    }
}

/// <summary>protocols[]: a URL scheme the app handles.</summary>
public sealed partial class ProtocolRow : GatedRow
{
    private ProtocolSettings _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Scheme { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial string Icon { get; set; } = "";
    [ObservableProperty] public partial double IconIndex { get; set; }
    [ObservableProperty] public partial string Target { get; set; } = "";
    [ObservableProperty] public partial string ArgumentsText { get; set; } = "";

    public string Title => Scheme.Trim().Length > 0 ? Scheme.Trim() + "://" : Loc.Get("Protocols_Untitled");
    public string Subtitle => Description.Trim();

    public static ProtocolRow From(ProtocolSettings p) => new()
    {
        _original = p,
        Scheme = p.Scheme ?? "",
        Description = p.Description ?? "",
        Icon = p.Icon ?? "",
        IconIndex = p.IconIndex,
        Target = p.Target ?? "",
        ArgumentsText = TextForms.Lines(p.Arguments),
        IfOption = p.IfOption ?? "",
    };

    public ProtocolSettings ToModel()
    {
        var o = _original;
        var p = TextForms.Copy(o);
        p.Scheme = TextForms.Keep(Scheme, o.Scheme);
        p.Description = TextForms.Keep(Description, o.Description);
        p.Icon = TextForms.Keep(Icon, o.Icon);
        p.IconIndex = TextForms.ToInt(IconIndex, o.IconIndex);
        p.Target = TextForms.Keep(Target, o.Target);
        p.Arguments = TextForms.FromLines(ArgumentsText, o.Arguments);
        p.IfOption = TextForms.Keep(IfOption, o.IfOption);
        return p;
    }
}

/// <summary>registry[]: a key below Software\&lt;Vendor&gt; with values.</summary>
public sealed partial class RegistryKeyRow : ParentRow<RegistryValueRow>
{
    internal static readonly string[] UninstallModes = ["values", "key", "keep"];
    private RegistryEntrySettings _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Key { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial int UninstallIndex { get; set; }

    public ObservableCollection<RegistryValueRow> Values => Children;
    public bool HasNoValues => Children.Count == 0;
    public string Title => Key.Trim().Length > 0 ? Key.Trim() : Loc.Get("Registry_Untitled");

    public string Subtitle
    {
        get
        {
            var uninstall = UninstallIndex is >= 0 and < 3 ? Loc.Get("RegistryUninstall_" + UninstallIndex) : _original.Uninstall;
            return $"{Loc.Format("Registry_ValueCount", Children.Count)} · {uninstall}";
        }
    }

    protected override void OnChildrenCountChanged()
    {
        OnPropertyChanged(nameof(HasNoValues));
        OnPropertyChanged(nameof(Subtitle));
    }

    public static RegistryKeyRow From(RegistryEntrySettings r)
    {
        var row = new RegistryKeyRow { _original = r, Key = r.Key ?? "", IfOption = r.IfOption ?? "" };
        row.UninstallIndex = TextForms.Find(UninstallModes, r.Uninstall);
        foreach (var v in r.Values ?? []) row.Children.Add(RegistryValueRow.From(v));
        return row;
    }

    public RegistryEntrySettings ToModel()
    {
        var o = _original;
        var r = TextForms.Copy(o);
        r.Key = TextForms.Keep(Key, o.Key);
        r.Values = Children.Select(v => v.ToModel()).ToList();
        r.Uninstall = TextForms.Pick(UninstallModes, UninstallIndex, o.Uninstall);
        r.IfOption = TextForms.Keep(IfOption, o.IfOption);
        return r;
    }

    [RelayCommand] private void AddValue() => AddChild(new RegistryValueRow());
}

/// <summary>registry[].values[]: one value (name, type, data).</summary>
public sealed partial class RegistryValueRow : ChildRow
{
    internal static readonly string[] Types = ["string", "expandString", "dword", "qword", "multiString"];
    private RegistryValueSettings _original = new();

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMultiString))] public partial int TypeIndex { get; set; }
    [ObservableProperty] public partial string Data { get; set; } = "";

    /// <summary>multiString data has one string per line.</summary>
    public bool IsMultiString => TypeIndex == 4;

    public static RegistryValueRow From(RegistryValueSettings v)
    {
        var row = new RegistryValueRow { _original = v, Name = v.Name ?? "", Data = v.Data ?? "" };
        row.TypeIndex = TextForms.Find(Types, v.Type);
        return row;
    }

    public RegistryValueSettings ToModel() => new()
    {
        Name = TextForms.Keep(Name, _original.Name),
        Type = TextForms.Pick(Types, TypeIndex, _original.Type),
        // Lines of a multiString are separated by "\n" whatever the text box uses.
        Data = TextForms.Same(Data, _original.Data ?? "")
            ? _original.Data!
            : Data.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'),
    };
}

/// <summary>environment[]: an environment variable.</summary>
public sealed partial class EnvironmentRow : GatedRow
{
    private EnvironmentVariableSettings _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Name { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string Value { get; set; } = "";
    [ObservableProperty] public partial bool Expand { get; set; }

    public string Title => Name.Trim().Length > 0 ? Name.Trim() : Loc.Get("Environment_Untitled");
    public string Subtitle => Value;

    public static EnvironmentRow From(EnvironmentVariableSettings e) => new()
    {
        _original = e, Name = e.Name ?? "", Value = e.Value ?? "", Expand = e.Expand, IfOption = e.IfOption ?? "",
    };

    public EnvironmentVariableSettings ToModel()
    {
        var o = _original;
        var e = TextForms.Copy(o);
        e.Name = TextForms.Keep(Name, o.Name);
        e.Value = Value == (o.Value ?? "") ? o.Value! : Value;  // values may start or end with spaces on purpose
        e.Expand = Expand;
        e.IfOption = TextForms.Keep(IfOption, o.IfOption);
        return e;
    }
}

/// <summary>services[]: a Windows service run from one of the app's programs.</summary>
public sealed partial class ServiceRow : EditorRow
{
    internal static readonly string[] StartTypes = ["auto", "delayedAuto", "manual", "disabled"];
    internal static readonly string[] Accounts = ["localSystem", "localService", "networkService"];
    private ServiceSettings _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Name { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string DisplayName { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string Run { get; set; } = "";
    [ObservableProperty] public partial string ArgumentsText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial int StartTypeIndex { get; set; }
    [ObservableProperty] public partial int AccountIndex { get; set; }
    [ObservableProperty] public partial bool Start { get; set; } = true;
    [ObservableProperty] public partial string DependenciesText { get; set; } = "";
    [ObservableProperty] public partial bool RestartOnFailure { get; set; } = true;

    public string Title => DisplayName.Trim().Length > 0 ? DisplayName.Trim() : Name.Trim().Length > 0 ? Name.Trim() : Loc.Get("Services_Untitled");

    public string Subtitle
    {
        get
        {
            var start = StartTypeIndex is >= 0 and < 4 ? Loc.Get("ServiceStart_" + StartTypeIndex) : _original.StartType;
            return Run.Trim().Length > 0 ? $"{Run.Trim()} · {start}" : start;
        }
    }

    public static ServiceRow From(ServiceSettings s)
    {
        var row = new ServiceRow
        {
            _original = s,
            Name = s.Name ?? "",
            DisplayName = s.DisplayName ?? "",
            Description = s.Description ?? "",
            Run = s.Run ?? "",
            ArgumentsText = TextForms.Lines(s.Arguments),
            Start = s.Start,
            DependenciesText = TextForms.Csv(s.Dependencies),
            RestartOnFailure = s.RestartOnFailure,
        };
        row.StartTypeIndex = TextForms.Find(StartTypes, s.StartType);
        row.AccountIndex = TextForms.Find(Accounts, s.Account);
        return row;
    }

    public ServiceSettings ToModel()
    {
        var o = _original;
        var s = TextForms.Copy(o);
        s.Name = TextForms.Keep(Name, o.Name);
        s.DisplayName = TextForms.Keep(DisplayName, o.DisplayName);
        s.Description = TextForms.Keep(Description, o.Description);
        s.Run = TextForms.Keep(Run, o.Run);
        s.Arguments = TextForms.FromLines(ArgumentsText, o.Arguments);
        s.StartType = TextForms.Pick(StartTypes, StartTypeIndex, o.StartType);
        s.Account = TextForms.Pick(Accounts, AccountIndex, o.Account);
        s.Start = Start;
        s.Dependencies = TextForms.FromCsv(DependenciesText, o.Dependencies);
        s.RestartOnFailure = RestartOnFailure;
        return s;
    }
}

/// <summary>firewall[]: an allow rule for one of the app's programs.</summary>
public sealed partial class FirewallRow : GatedRow
{
    internal static readonly string[] Directions = ["in", "out"];
    internal static readonly string[] Protocols = ["any", "tcp", "udp"];
    private FirewallRuleSettings _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Name { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string Program { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial int DirectionIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle), nameof(HasPorts))] public partial int ProtocolIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string LocalPorts { get; set; } = "";
    [ObservableProperty] public partial bool ProfileDomain { get; set; }
    [ObservableProperty] public partial bool ProfilePrivate { get; set; }
    [ObservableProperty] public partial bool ProfilePublic { get; set; }

    /// <summary>Ports apply to TCP and UDP rules.</summary>
    public bool HasPorts => ProtocolIndex is 1 or 2;
    public string Title => Name.Trim().Length > 0 ? Name.Trim() : Loc.Get("Firewall_Untitled");

    public string Subtitle
    {
        get
        {
            var direction = DirectionIndex is 0 or 1 ? Loc.Get("FirewallDirection_" + DirectionIndex) : _original.Direction;
            var protocol = ProtocolIndex is >= 0 and < 3 ? Loc.Get("FirewallProtocol_" + ProtocolIndex) : _original.Protocol;
            var program = Program.Trim().Length > 0 ? Program.Trim() : Loc.Get("Shortcuts_MainProgram");
            var ports = HasPorts && LocalPorts.Trim().Length > 0 ? " " + LocalPorts.Trim() : "";
            return $"{direction} · {protocol}{ports} · {program}";
        }
    }

    public static FirewallRow From(FirewallRuleSettings f)
    {
        var profiles = f.Profiles ?? [];
        var row = new FirewallRow
        {
            _original = f,
            Name = f.Name ?? "",
            Program = f.Program ?? "",
            LocalPorts = f.LocalPorts ?? "",
            ProfileDomain = profiles.Contains("domain"),
            ProfilePrivate = profiles.Contains("private"),
            ProfilePublic = profiles.Contains("public"),
            IfOption = f.IfOption ?? "",
        };
        row.DirectionIndex = TextForms.Find(Directions, f.Direction);
        row.ProtocolIndex = TextForms.Find(Protocols, f.Protocol);
        return row;
    }

    public FirewallRuleSettings ToModel()
    {
        var o = _original;
        var f = TextForms.Copy(o);
        f.Name = TextForms.Keep(Name, o.Name);
        f.Program = TextForms.Keep(Program, o.Program);
        f.Direction = TextForms.Pick(Directions, DirectionIndex, o.Direction);
        f.Protocol = TextForms.Pick(Protocols, ProtocolIndex, o.Protocol);
        f.LocalPorts = TextForms.Keep(LocalPorts, o.LocalPorts);
        var original = o.Profiles ?? [];
        var unchanged = ProfileDomain == original.Contains("domain") && ProfilePrivate == original.Contains("private") && ProfilePublic == original.Contains("public");
        f.Profiles = unchanged
            ? [.. original]
            : new[] { (ProfileDomain, "domain"), (ProfilePrivate, "private"), (ProfilePublic, "public") }.Where(p => p.Item1).Select(p => p.Item2).ToList();
        f.IfOption = TextForms.Keep(IfOption, o.IfOption);
        return f;
    }
}

/// <summary>inputs[]: a value the user enters on the installer's Settings page.</summary>
public sealed partial class InputRow : ParentRow<InputChoiceRow>
{
    internal static readonly string[] Types = ["text", "password", "choice"];
    private InputSettings _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Id { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial string LabelText { get; set; } = "";
    [ObservableProperty] public partial string DescriptionText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle), nameof(IsChoice), nameof(CanRemember))] public partial int TypeIndex { get; set; }
    [ObservableProperty] public partial string Default { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle))] public partial bool Required { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PatternError))] public partial string Pattern { get; set; } = "";
    [ObservableProperty] public partial bool Remember { get; set; } = true;

    public ObservableCollection<InputChoiceRow> Choices => Children;
    public bool IsChoice => TypeIndex == 2;
    /// <summary>Passwords are never remembered.</summary>
    public bool CanRemember => TypeIndex != 1;
    public string Title => Id.Trim().Length > 0 ? Id.Trim() : Loc.Get("Inputs_Untitled");

    public string Subtitle
    {
        get
        {
            var type = TypeIndex is >= 0 and < 3 ? Loc.Get("InputType_" + TypeIndex) : _original.Type;
            var label = TextForms.FirstText(LabelText);
            var parts = new[] { label, type, Required ? Loc.Get("Inputs_Required") : "" }.Where(p => p.Length > 0);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The pattern as .NET reads it (setup uses the same ECMAScript subset).</summary>
    public string PatternError
    {
        get
        {
            if (Pattern.Length == 0) return "";
            try
            {
                _ = new Regex(Pattern, RegexOptions.ECMAScript, TimeSpan.FromSeconds(1));
                return "";
            }
            catch (ArgumentException ex)
            {
                return Loc.Format("Inputs_PatternInvalid", ex.Message);
            }
        }
    }

    public static InputRow From(InputSettings i)
    {
        var row = new InputRow
        {
            _original = i,
            Id = i.Id ?? "",
            LabelText = TextForms.Localized(i.Label),
            DescriptionText = TextForms.Localized(i.Description),
            Default = i.Default ?? "",
            Required = i.Required,
            Pattern = i.Pattern ?? "",
            Remember = i.Remember,
        };
        row.TypeIndex = TextForms.Find(Types, i.Type);
        foreach (var c in i.Choices ?? []) row.Children.Add(InputChoiceRow.From(c));
        return row;
    }

    public InputSettings ToModel()
    {
        var o = _original;
        var i = TextForms.Copy(o);
        i.Id = TextForms.Keep(Id, o.Id);
        i.Label = TextForms.FromLocalized(LabelText, o.Label);
        i.Description = TextForms.FromLocalized(DescriptionText, o.Description);
        i.Type = TextForms.Pick(Types, TypeIndex, o.Type);
        i.Default = Default == (o.Default ?? "") ? o.Default! : Default;
        i.Required = Required;
        i.Pattern = Pattern == (o.Pattern ?? "") ? o.Pattern! : Pattern;
        i.Remember = Remember;
        i.Choices = Children.Select(c => c.ToModel()).ToList();
        return i;
    }

    [RelayCommand] private void AddChoice() => AddChild(new InputChoiceRow());
}

/// <summary>inputs[].choices[]: a value of a choice input.</summary>
public sealed partial class InputChoiceRow : ChildRow
{
    private InputChoiceSettings _original = new();

    [ObservableProperty] public partial string Value { get; set; } = "";
    [ObservableProperty] public partial string LabelText { get; set; } = "";

    public static InputChoiceRow From(InputChoiceSettings c) => new() { _original = c, Value = c.Value ?? "", LabelText = TextForms.Localized(c.Label) };

    public InputChoiceSettings ToModel() => new()
    {
        Value = TextForms.Keep(Value, _original.Value),
        Label = TextForms.FromLocalized(LabelText, _original.Label),
    };
}

/// <summary>configFiles[]: a JSON or INI file setup writes values into.</summary>
public sealed partial class ConfigFileRow : ParentRow<ConfigValueRow>
{
    internal static readonly string[] Formats = ["json", "ini"];
    private ConfigFileSettings _original = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Path { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Subtitle), nameof(IsIni), nameof(IsJson))] public partial int FormatIndex { get; set; }

    public ObservableCollection<ConfigValueRow> Values => Children;
    public bool HasNoValues => Children.Count == 0;
    public bool IsIni => FormatIndex == 1;
    public bool IsJson => FormatIndex != 1;
    public string Title => Path.Trim().Length > 0 ? Path.Trim() : Loc.Get("ConfigFiles_Untitled");

    public string Subtitle
    {
        get
        {
            var format = FormatIndex is 0 or 1 ? Formats[FormatIndex].ToUpperInvariant() : _original.Format;
            return $"{format} · {Loc.Format("Registry_ValueCount", Children.Count)}";
        }
    }

    partial void OnFormatIndexChanged(int value)
    {
        foreach (var v in Children) v.RefreshFormat();
    }

    protected override void OnChildrenCountChanged()
    {
        OnPropertyChanged(nameof(HasNoValues));
        OnPropertyChanged(nameof(Subtitle));
    }

    public static ConfigFileRow From(ConfigFileSettings c)
    {
        var row = new ConfigFileRow { _original = c, Path = c.Path ?? "", IfOption = c.IfOption ?? "" };
        row.FormatIndex = TextForms.Find(Formats, c.Format);
        foreach (var v in c.Values ?? []) row.Children.Add(ConfigValueRow.From(v));
        return row;
    }

    public ConfigFileSettings ToModel()
    {
        var o = _original;
        var c = TextForms.Copy(o);
        c.Path = TextForms.Keep(Path, o.Path);
        c.Format = TextForms.Pick(Formats, FormatIndex, o.Format);
        c.Values = Children.Select(v => v.ToModel()).ToList();
        c.IfOption = TextForms.Keep(IfOption, o.IfOption);
        return c;
    }

    [RelayCommand] private void AddValue() => AddChild(new ConfigValueRow());
}

/// <summary>configFiles[].values[]: a key (JSON path or INI section + key) and its value.</summary>
public sealed partial class ConfigValueRow : ChildRow
{
    internal static readonly string[] Types = ["string", "number", "bool"];
    private ConfigValueSettings _original = new();

    [ObservableProperty] public partial string Key { get; set; } = "";
    [ObservableProperty] public partial string Section { get; set; } = "";
    [ObservableProperty] public partial string Value { get; set; } = "";
    [ObservableProperty] public partial int TypeIndex { get; set; }

    public bool IsIni => (Parent as ConfigFileRow)?.IsIni == true;
    public bool IsJson => !IsIni;

    internal void RefreshFormat()
    {
        OnPropertyChanged(nameof(IsIni));
        OnPropertyChanged(nameof(IsJson));
    }

    public static ConfigValueRow From(ConfigValueSettings v)
    {
        var row = new ConfigValueRow { _original = v, Key = v.Key ?? "", Section = v.Section ?? "", Value = v.Value ?? "" };
        row.TypeIndex = TextForms.Find(Types, v.Type);
        return row;
    }

    public ConfigValueSettings ToModel() => new()
    {
        Key = TextForms.Keep(Key, _original.Key),
        Section = TextForms.Keep(Section, _original.Section),
        Value = Value == (_original.Value ?? "") ? _original.Value! : Value,
        Type = TextForms.Pick(Types, TypeIndex, _original.Type),
    };
}

/// <summary>requirements.custom[]: another prerequisite, found through a registry value or a file.</summary>
public sealed partial class CustomRequirementRow : PrerequisiteSourceEditor
{
    internal static readonly string[] Hives = ["HKLM", "HKCU"];
    private CustomRequirement _original = new();

    public CustomRequirementRow() : base(supportsOfficial: false)
    {
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Id { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title))] public partial string Name { get; set; } = "";
    /// <summary>0 = registry value, 1 = file.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRegistry), nameof(IsFile))] public partial int DetectIndex { get; set; }
    [ObservableProperty] public partial int RegistryHiveIndex { get; set; }
    [ObservableProperty] public partial string RegistryKey { get; set; } = "";
    [ObservableProperty] public partial string RegistryValue { get; set; } = "";
    [ObservableProperty] public partial string RegistryVersion { get; set; } = "";
    [ObservableProperty] public partial string FilePath { get; set; } = "";
    [ObservableProperty] public partial string FileVersion { get; set; } = "";

    public bool IsRegistry => DetectIndex != 1;
    public bool IsFile => DetectIndex == 1;
    public string Title => Name.Trim().Length > 0 ? Name.Trim() : Id.Trim().Length > 0 ? Id.Trim() : Loc.Get("Custom_Untitled");

    public static CustomRequirementRow From(CustomRequirement c)
    {
        var detect = c.Detect ?? new RequirementDetection();
        var registry = detect.Registry ?? new RegistryVersionCondition();
        var row = new CustomRequirementRow
        {
            _original = c,
            Id = c.Id ?? "",
            Name = c.Name ?? "",
            DetectIndex = detect.File is not null && detect.Registry is null ? 1 : 0,
            RegistryKey = registry.Key ?? "",
            RegistryValue = registry.Value ?? "",
            RegistryVersion = registry.Version ?? "",
            FilePath = detect.File?.Path ?? "",
            FileVersion = detect.File?.Version ?? "",
        };
        row.RegistryHiveIndex = TextForms.Find(Hives, registry.Hive);
        row.LoadSource(c);
        return row;
    }

    public CustomRequirement ToModel()
    {
        var o = _original;
        var c = TextForms.Copy(o);
        c.Id = TextForms.Keep(Id, o.Id);
        c.Name = TextForms.Keep(Name, o.Name);
        var registry = o.Detect?.Registry;
        var file = o.Detect?.File;
        c.Detect = DetectIndex == 1
            ? new RequirementDetection { File = new FileDetection { Path = TextForms.Keep(FilePath, file?.Path), Version = TextForms.Keep(FileVersion, file?.Version) } }
            : new RequirementDetection
            {
                Registry = new RegistryVersionCondition
                {
                    Hive = TextForms.Pick(Hives, RegistryHiveIndex, registry?.Hive ?? "HKLM"),
                    Key = TextForms.Keep(RegistryKey, registry?.Key),
                    Value = TextForms.Keep(RegistryValue, registry?.Value),
                    Version = TextForms.Keep(RegistryVersion, registry?.Version),
                },
            };
        ApplySource(c);
        return c;
    }
}
