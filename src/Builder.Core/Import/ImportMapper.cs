using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Core.Import;

/// <summary>
/// Turns what a reader collected (files, registry writes, shortcuts, option candidates) into project sections. Shared
/// by Inno Setup and NSIS, so both get the same rules for file sets, file types, URL protocols, autostart, App Paths,
/// environment variables and app registry keys.
/// </summary>
internal static partial class ImportMapper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private static readonly string[] SharedTrees = ["Microsoft", "Classes", "Policies", "Wow6432Node", "RegisteredApplications", "Clients", "FluentSetup"];
    private static readonly string[] ContextMenuClasses = ["*", "Directory", "Folder", "Drive", "AllFilesystemObjects", "SystemFileAssociations", "DesktopBackground", "LibraryFolder"];
    private static readonly string[] ComClasses = ["CLSID", "Interface", "TypeLib", "AppID", "Wow6432Node"];
    private static readonly string[] BlockedExtensions = [".exe", ".com", ".bat", ".cmd", ".lnk", ".pif", ".scr", ".msi", ".msp", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".reg", ".url", ".dll", ".sys", ".cpl", ".hta", ".inf"];
    private static readonly string[] BlockedSchemes = ["http", "https", "file", "ftp", "mailto", "news", "nntp", "telnet", "ldap", "res", "about", "javascript", "vbscript", "data", "shell", "search", "search-ms", "microsoft-edge", "callto", "tel", "sms"];
    private static readonly string[] SystemEnvironmentNames =
    [
        "PATH", "PATHEXT", "SystemRoot", "windir", "TEMP", "TMP", "ComSpec", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "OS", "NUMBER_OF_PROCESSORS", "PSModulePath", "USERNAME", "USERDOMAIN",
        "HOMEDRIVE", "HOMEPATH", "PUBLIC", "ALLUSERSPROFILE", "SystemDrive", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "DriverData",
    ];

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z0-9]+)+$")]
    private static partial Regex ProgIdRegex();

    [GeneratedRegex(@"^\.[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex ExtensionRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]{1,31}$")]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.()-]{0,127}$")]
    private static partial Regex EnvironmentNameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex AppUserModelIdRegex();

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex ClsidRegex();

    public static void Finish(ImportContext ctx)
    {
        MapShortcuts(ctx);
        MapRegistry(ctx);
        MapFiles(ctx);
        MapOptions(ctx);
        NormalizeFileName(ctx);
        StripUnsupported(ctx);
        MapVariables(ctx);
    }

    /// <summary>
    /// Last safety net: text that still holds a constant FluentSetup cannot fill in ({code:…}, $R0 …) loses it, so the
    /// project never contains the importer's markers (which could also look like $(variables)).
    /// </summary>
    private static void StripUnsupported(ImportContext ctx)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(ctx.Project, ProjectSerializer.Options)!;
        var changed = false;
        void Walk(System.Text.Json.Nodes.JsonNode n, string path)
        {
            switch (n)
            {
                case System.Text.Json.Nodes.JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                        if (obj[key] is { } child) { if (Replace(child, path.Length == 0 ? key : $"{path}.{key}") is { } r) obj[key] = r; else Walk(child, path.Length == 0 ? key : $"{path}.{key}"); }
                    break;
                case System.Text.Json.Nodes.JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                        if (arr[i] is { } child) { if (Replace(child, $"{path}[{i}]") is { } r) arr[i] = r; else Walk(child, $"{path}[{i}]"); }
                    break;
            }
        }
        System.Text.Json.Nodes.JsonNode? Replace(System.Text.Json.Nodes.JsonNode n, string path)
        {
            if (n is not System.Text.Json.Nodes.JsonValue v || !v.TryGetValue<string>(out var s) || !Canonical.IsUnsupported(s)) return null;
            var cleaned = UnsupportedMarkerRegex().Replace(s, "").Trim();
            ctx.Warn(ctx.Script, $"{path}: {Canonical.Display(s).Trim()} uses {Canonical.FirstUnsupported(s)}, which FluentSetup cannot fill in; it is \"{cleaned}\" now.", path);
            changed = true;
            return System.Text.Json.Nodes.JsonValue.Create(cleaned);
        }
        Walk(node, "");
        if (changed) ctx.Project = System.Text.Json.JsonSerializer.Deserialize<SetupProject>(node, ProjectSerializer.Options)!;
    }

    [GeneratedRegex(@"\{\?[^}]*\}")]
    private static partial Regex UnsupportedMarkerRegex();

    // ---- shortcuts ---------------------------------------------------------------------------------------------

    private static void MapShortcuts(ImportContext ctx)
    {
        var p = ctx.Project;
        var appName = ctx.Resolve(p.App.Name);
        var main = ctx.MainExecutable;
        ShortcutEntry? mainStart = null, mainDesktop = null;
        var extra = new List<(ShortcutEntry S, string Target)>();
        foreach (var s in ctx.Shortcuts)
        {
            var name = Canonical.Display(ctx.Resolve(s.Name));
            if (s.Target.StartsWith("{Uninstaller}", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Info(s.At, $"Shortcut \"{name}\" to the uninstaller is not created: the app is removed in Settings > Apps (uninstall.exe stays in the install folder).");
                continue;
            }
            if (s.Location == "quickLaunch")
            {
                ctx.Info(s.At, $"Quick Launch shortcut \"{name}\" is not created (Quick Launch does not exist on Windows 10 and 11).");
                continue;
            }
            if (s.Location == "other")
            {
                ctx.Manual(s.At, $"Shortcut \"{name}\" goes to {Canonical.Display(s.Unsupported)}: FluentSetup creates shortcuts in the Start menu, on the desktop and in Startup only.", "shortcuts");
                continue;
            }
            if (!ctx.TryInstallRelative(s.Target, out var target) || target.Length == 0)
            {
                ctx.Manual(s.At, $"Shortcut \"{name}\" opens {Canonical.Display(s.Target)}, which is not one of the app's files (web links and system programs cannot be shortcut targets). " +
                                 "Not imported: add a link inside the app, or a shortcut to one of its files.", "shortcuts");
                continue;
            }
            var isMain = main.Length > 0 && ImportContext.SamePath(target, main) && s.Arguments.Trim().Length == 0 && name.Equals(appName, StringComparison.OrdinalIgnoreCase);
            if (isMain && s.Location == "startMenu" && mainStart is null) { mainStart = s; continue; }
            if (isMain && s.Location == "desktop" && mainDesktop is null) { mainDesktop = s; continue; }
            extra.Add((s, target));
        }

        // Start menu folder: the main shortcut's, else the folder most other Start menu shortcuts use, else the program group.
        string folder;
        if (mainStart is not null) folder = mainStart.Folder;
        else
        {
            var startMenu = extra.Where(e => e.S.Location == "startMenu").Select(e => e.S.Folder).ToList();
            folder = startMenu.Count > 0 ? startMenu.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First().Key : "";
        }
        p.Install.StartMenuFolder = StartMenuFolder(ctx, folder, mainStart?.At ?? ctx.Script, "install.startMenuFolder");

        if (mainStart is not null)
        {
            p.Install.StartMenuShortcut = Offer(ctx, mainStart.IfOption, out var how);
            ctx.Info(mainStart.At, $"Start menu shortcut \"{ctx.Resolve(mainStart.Name)}\"{(folder.Length > 0 ? $" in folder \"{ctx.Resolve(folder)}\"" : "")} → install.startMenuShortcut \"{p.Install.StartMenuShortcut}\"{how}" +
                                   (folder.Length > 0 ? ", install.startMenuFolder" : ""), "install.startMenuShortcut");
            MainShortcutDetails(ctx, mainStart);
        }
        if (mainDesktop is not null)
        {
            p.Install.DesktopShortcut = Offer(ctx, mainDesktop.IfOption, out var how);
            ctx.Info(mainDesktop.At, $"Desktop shortcut \"{ctx.Resolve(mainDesktop.Name)}\" → install.desktopShortcut \"{p.Install.DesktopShortcut}\"{how}", "install.desktopShortcut");
            if (mainStart is null) MainShortcutDetails(ctx, mainDesktop);
        }

        foreach (var (s, target) in extra)
        {
            if (p.Shortcuts.Count >= 16)
            {
                ctx.Manual(s.At, $"Shortcut \"{ctx.Resolve(s.Name)}\" is not imported: a project has at most 16 extra shortcuts.", "shortcuts");
                continue;
            }
            var name = s.Name;
            var resolvedName = Canonical.Display(ctx.Resolve(name));
            if (!PathRules.IsValidComponent(resolvedName, out _) || resolvedName.Length > 128)
            {
                var clean = string.Concat(resolvedName.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
                if (clean.Length > 128) clean = clean[..128].Trim();
                if (clean.Length == 0) clean = Path.GetFileNameWithoutExtension(target);
                ctx.Warn(s.At, $"Shortcut name \"{resolvedName}\" is not a valid file name; renamed to \"{clean}\".", "shortcuts");
                name = ImportContext.Literal(clean);
            }
            var sc = new ShortcutSettings
            {
                Name = name,
                Target = main.Length > 0 && ImportContext.SamePath(target, main) ? "" : target,
                Description = s.Description,
                Location = s.Location,
                IconIndex = s.IconIndex,
            };
            if (s.Arguments.Trim().Length > 0)
            {
                if (TryArguments(ScriptText.SplitCommandLine(s.Arguments), out var args)) sc.Arguments = args;
                else ctx.Manual(s.At, $"Shortcut \"{resolvedName}\": the arguments \"{Canonical.Display(s.Arguments)}\" use folders or constants FluentSetup cannot fill in; the shortcut is created without them.", "shortcuts");
            }
            if (s.Icon.Length > 0)
            {
                if (ctx.TryInstallRelative(s.Icon, out var icon) && icon.Length > 0) sc.Icon = ImportContext.SamePath(icon, target) ? "" : icon;
                else ctx.Info(s.At, $"Shortcut \"{resolvedName}\": icon {Canonical.Display(s.Icon)} is not one of the app's files; the target's icon is used.");
            }
            if (s.WorkingDirectory.Length > 0)
            {
                if (ctx.TryInstallRelative(s.WorkingDirectory, out var wd))
                {
                    var targetDir = target.Contains('/') ? target[..target.LastIndexOf('/')] : "";
                    sc.WorkingDirectory = ImportContext.SamePath(wd, targetDir) ? "" : wd;
                }
                else ctx.Info(s.At, $"Shortcut \"{resolvedName}\": working folder {Canonical.Display(s.WorkingDirectory)} is outside the install folder; the target's folder is used.");
            }
            if (s.Location == "startMenu")
            {
                var resolvedFolder = ctx.Resolve(s.Folder);
                var lastLevel = resolvedFolder.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                if (lastLevel.Equals(ctx.Resolve(p.Install.StartMenuFolder), StringComparison.OrdinalIgnoreCase)) sc.Folder = "";
                else if (resolvedFolder.Length == 0)
                    ctx.Warn(s.At, $"Shortcut \"{resolvedName}\" was directly in the Start menu's Programs; FluentSetup puts it into install.startMenuFolder \"{ctx.Resolve(p.Install.StartMenuFolder)}\".", "shortcuts");
                else sc.Folder = StartMenuFolder(ctx, s.Folder, s.At, "shortcuts");
            }
            if (s.AppUserModelId.Length > 0)
            {
                if (AppUserModelIdRegex().IsMatch(ctx.Resolve(s.AppUserModelId))) sc.AppUserModelId = s.AppUserModelId;
                else ctx.Warn(s.At, $"AppUserModelID \"{ctx.Resolve(s.AppUserModelId)}\" of shortcut \"{resolvedName}\" is not imported (letters, digits, '.', '-', '_' only).");
            }
            sc.IfOption = ctx.UseOption(s.IfOption);
            p.Shortcuts.Add(sc);
            ctx.Info(s.At, $"Shortcut \"{resolvedName}\" ({LocationName(s.Location)}) → shortcuts[{p.Shortcuts.Count - 1}]{(sc.IfOption.Length > 0 ? $", only with option \"{sc.IfOption}\"" : "")}", "shortcuts");
        }
    }

    private static string LocationName(string location) => location switch { "desktop" => "desktop", "startup" => "Startup folder", _ => "Start menu" };

    /// <summary>The offer for one of the app's own shortcuts: "on", or a checkbox that follows the task/section's default.</summary>
    private static string Offer(ImportContext ctx, string optionId, out string how)
    {
        how = "";
        if (optionId.Length == 0) return "on";
        var o = ctx.FindOption(optionId);
        if (o is null) return "on";
        o.AbsorbedInto = o.AbsorbedInto.Length == 0 ? "a shortcut checkbox" : o.AbsorbedInto;
        if (o.Required) return "on";
        how = $" (checkbox from {o.Origin})";
        return o.Default ? "optionalOn" : "optionalOff";
    }

    private static void MainShortcutDetails(ImportContext ctx, ShortcutEntry s)
    {
        var p = ctx.Project;
        if (s.AppUserModelId.Length > 0)
        {
            if (AppUserModelIdRegex().IsMatch(ctx.Resolve(s.AppUserModelId))) { p.App.AppUserModelId = s.AppUserModelId; ctx.Info(s.At, "AppUserModelID → app.appUserModelId", "app.appUserModelId"); }
            else ctx.Warn(s.At, $"AppUserModelID \"{ctx.Resolve(s.AppUserModelId)}\" is not imported (letters, digits, '.', '-', '_' only).", "app.appUserModelId");
        }
        if (s.ToastActivatorClsid.Length > 0)
        {
            var clsid = ctx.Resolve(s.ToastActivatorClsid).Trim();
            if (!clsid.StartsWith('{')) clsid = "{" + clsid + "}";
            if (ClsidRegex().IsMatch(clsid)) { p.App.ToastActivatorClsid = clsid; ctx.Info(s.At, "Toast activator CLSID → app.toastActivatorClsid", "app.toastActivatorClsid"); }
            else ctx.Warn(s.At, $"Toast activator CLSID \"{clsid}\" is not a CLSID; not imported.", "app.toastActivatorClsid");
        }
        if (s.Description.Length > 0 && p.App.Description.Length == 0)
        {
            p.App.Description = s.Description;
            ctx.Info(s.At, "Shortcut comment → app.description (FluentSetup uses it as the shortcut comment)", "app.description");
        }
        if (s.Icon.Length > 0 && !(ctx.TryInstallRelative(s.Icon, out var icon) && ImportContext.SamePath(icon, ctx.MainExecutable)))
            ctx.Info(s.At, $"The app's shortcuts use the main executable's own icon, not {Canonical.Display(s.Icon)}.");
    }

    /// <summary>A Start menu folder as FluentSetup takes it: one folder name of up to 64 characters.</summary>
    private static string StartMenuFolder(ImportContext ctx, string folder, SourceRef at, string field)
    {
        if (folder.Length == 0) return "";
        var resolved = ctx.Resolve(folder);
        if (Canonical.IsUnsupported(folder))
        {
            ctx.Manual(at, $"Start menu folder {Canonical.Display(folder)} is chosen at install time by the script; FluentSetup uses a fixed folder (set install.startMenuFolder).", field);
            return "";
        }
        var parts = resolved.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var result = folder;
        if (parts.Length > 1)
        {
            result = ImportContext.Literal(parts[^1]);
            ctx.Warn(at, $"Start menu folder \"{resolved}\" has {parts.Length} levels; FluentSetup uses one folder: \"{parts[^1]}\".", field);
            resolved = parts[^1];
        }
        if (!PathRules.IsValidComponent(resolved, out _) || resolved.Length > 64)
        {
            ctx.Warn(at, $"Start menu folder \"{resolved}\" is not a valid folder name of up to 64 characters; shortcuts go directly into Programs.", field);
            return "";
        }
        return result;
    }

    private static bool TryArguments(IEnumerable<string> args, out List<string> result)
    {
        result = [];
        foreach (var a in args)
        {
            if (Canonical.AsValue(a) is not { } v) return false;
            result.Add(v);
        }
        return true;
    }

    /// <summary>Splits a canonical command line into the program (relative to the install folder) and its arguments.</summary>
    public static bool TryProgram(ImportContext ctx, string commandLine, out string program, out List<string> arguments, out string display)
    {
        program = "";
        arguments = [];
        var parts = ScriptText.SplitCommandLine(commandLine.Trim());
        display = parts.Count > 0 ? Canonical.Display(parts[0]) : "";
        if (parts.Count == 0 || !ctx.TryInstallRelative(parts[0], out program) || program.Length == 0) return false;
        return TryArguments(parts.Skip(1), out arguments);
    }

    // ---- registry ----------------------------------------------------------------------------------------------

    private static bool KeyStartsWith(string key, string prefix, out string rest)
    {
        rest = "";
        if (key.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (!key.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        rest = key[(prefix.Length + 1)..];
        return true;
    }

    private static string RootName(ImportContext ctx, string root) => root == "HKA" ? (ctx.Project.Install.Scope == "user" ? "HKCU" : ctx.Project.Install.Scope == "machine" ? "HKLM" : "HKCU/HKLM") : root;

    /// <summary>Null if the root fits the install scope, else what differs.</summary>
    private static string? RootMismatch(ImportContext ctx, string root) => (ctx.Project.Install.Scope, root) switch
    {
        (_, "HKA") => null,
        ("user", "HKCU") or ("machine", "HKLM") => null,
        ("user", "HKLM") => "a per-user install writes below HKCU, not HKLM",
        ("machine", "HKCU") => "a per-machine install writes below HKLM, not the installing user's HKCU",
        ("userOrMachine", "HKCU" or "HKLM") => $"the project writes below HKCU or HKLM depending on the scope chosen, not always {root}",
        _ => $"{root} is not supported (only the install scope's HKCU or HKLM)",
    };

    private static void MapRegistry(ImportContext ctx)
    {
        var classes = new List<(RegistryWrite W, string Path)>();
        var vendor = new List<RegistryWrite>();
        foreach (var w in ctx.Registry.Where(w => !w.Consumed))
        {
            var key = ctx.Resolve(w.Key);
            if (KeyStartsWith(key, @"Software\Classes", out var rest)) classes.Add((w, rest));
            else if (KeyStartsWith(key, AppPathsKey, out rest)) AppPath(ctx, w, rest);
            else if (key.Equals(RunKey, StringComparison.OrdinalIgnoreCase)) RunValue(ctx, w);
            else if (KeyStartsWith(key, RunOnceKey, out _))
                ctx.Manual(w.At, $"RunOnce value \"{w.Name}\" is not imported: FluentSetup does not run programs at the next sign-in. Use an action (actions[]) instead.", "actions");
            else if (KeyStartsWith(key, UninstallKey, out _))
            {
                if (w.Name is not null) ctx.Info(w.At, $"Uninstall entry value \"{w.Name}\" is not needed: FluentSetup writes the app's uninstall entry itself (from app.*).");
            }
            else if ((key.Equals("Environment", StringComparison.OrdinalIgnoreCase) && w.Root is "HKCU" or "HKA") ||
                     (key.Equals(MachineEnvironmentKey, StringComparison.OrdinalIgnoreCase) && w.Root is "HKLM" or "HKA")) EnvironmentValue(ctx, w);
            else vendor.Add(w);
        }
        MapClasses(ctx, classes);
        MapVendorKeys(ctx, vendor);
    }

    private static void AppPath(ImportContext ctx, RegistryWrite w, string rest)
    {
        var exe = rest.Split('\\')[0];
        var main = ctx.MainExecutable;
        if (main.Length > 0 && exe.Equals(Path.GetFileName(main), StringComparison.OrdinalIgnoreCase))
        {
            ctx.Project.Install.AppPaths = true;
            ctx.Info(w.At, $"App Paths entry for {exe} → install.appPaths (FluentSetup writes the path and the Path value itself)", "install.appPaths");
        }
        else ctx.Manual(w.At, $"App Paths entry for {(exe.Length > 0 ? exe : "(no program)")} is not imported: install.appPaths registers only app.mainExecutable ({(main.Length > 0 ? main : "not set")}).", "install.appPaths");
    }

    private static void RunValue(ImportContext ctx, RegistryWrite w)
    {
        if (w.Name is null) return;  // the Run key itself
        var p = ctx.Project;
        if (w.Name.Length == 0 || !TryProgram(ctx, w.Data, out var program, out var args, out var display) ||
            ctx.MainExecutable.Length == 0 || !ImportContext.SamePath(program, ctx.MainExecutable))
        {
            ctx.Manual(w.At, $"Run value \"{w.Name}\" starts {Canonical.Display(w.Data)}: an option's startup value can only start app.mainExecutable. Not imported; " +
                             "start it from the app, or use an action (actions[]) that registers it.", "options");
            return;
        }
        var o = w.IfOption.Length > 0 ? ctx.FindOption(w.IfOption) : null;
        if (o is null)
        {
            o = new OptionCandidate
            {
                Id = ctx.NewOptionId("startup"), At = w.At, Default = true, Origin = $"Run value \"{w.Name}\"",
                Label = { ["en-US"] = $"Start {p.App.Name} when I sign in" },
            };
            ctx.Options.Add(o);
            ctx.Warn(w.At, $"Run value \"{w.Name}\" was always written; it is now the option \"{o.Id}\" (checked by default), so users can turn autostart off.", "options");
        }
        if (o.Startup is not null)
        {
            ctx.Manual(w.At, $"Run value \"{w.Name}\": option \"{o.Id}\" already starts the app as \"{o.Startup.Name}\"; one Run value per option.", "options");
            return;
        }
        o.Startup = new RunValueSettings { Name = w.Name, Arguments = args };
        o.Used = true;
        ctx.Info(w.At, $"Run value \"{w.Name}\" → options[\"{o.Id}\"].startup", "options");
        if (w.Root == "HKLM" || (w.Root == "HKA" && p.Install.Scope != "user"))
            ctx.Warn(w.At, $"Run value \"{w.Name}\" was written for all users (HKLM); a startup option writes HKCU\\{RunKey} of the user who installs.", "options");
        if (w.Conditional) ctx.Warn(w.At, $"Run value \"{w.Name}\" depended on a script condition; the option has no such condition.", "options");
    }

    private static void EnvironmentValue(ImportContext ctx, RegistryWrite w)
    {
        if (w.Name is null) return;
        var p = ctx.Project;
        var name = ctx.Resolve(w.Name);
        var mismatch = RootMismatch(ctx, w.Root);
        if (name.Equals("Path", StringComparison.OrdinalIgnoreCase))
        {
            var added = new List<string>();
            foreach (var item in ctx.Resolve(w.Data).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!ctx.TryInstallRelative(item, out var rel)) continue;
                if (!p.Install.AddToPath.Contains(rel, StringComparer.OrdinalIgnoreCase)) p.Install.AddToPath.Add(rel);
                added.Add(rel.Length == 0 ? "the install folder" : rel);
            }
            if (added.Count == 0) ctx.Manual(w.At, $"PATH value {Canonical.Display(w.Data)} adds no folder of the app; not imported.", "install.addToPath");
            else ctx.Info(w.At, $"PATH entry ({string.Join(", ", added)}) → install.addToPath (added on install, removed on uninstall)", "install.addToPath");
            if (added.Count > 0 && w.IfOption.Length > 0 && ctx.FindOption(w.IfOption) is { } o)
            {
                o.Unexpressed.Add("PATH");
                ctx.Warn(w.At, $"The PATH entry depended on {o.Origin}; install.addToPath has no condition, so it is always added.", "install.addToPath");
            }
            if (mismatch is not null) ctx.Warn(w.At, $"PATH: {mismatch}.", "install.addToPath");
            return;
        }
        if (!EnvironmentNameRegex().IsMatch(name) || SystemEnvironmentNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            name.StartsWith("PROCESSOR_", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Manual(w.At, $"Environment variable \"{name}\" is not imported: FluentSetup does not change Windows' own variables.", "environment");
            return;
        }
        if (Canonical.AsValue(w.Data) is not { } value)
        {
            ctx.Manual(w.At, $"Environment variable \"{name}\" = {Canonical.Display(w.Data)} uses a folder or constant FluentSetup cannot fill in; not imported.", "environment");
            return;
        }
        if (p.Environment.Count >= 16) { ctx.Manual(w.At, $"Environment variable \"{name}\" is not imported: at most 16.", "environment"); return; }
        p.Environment.Add(new EnvironmentVariableSettings { Name = w.Name, Value = value, Expand = w.Type == "expandString", IfOption = ctx.UseOption(w.IfOption) });
        ctx.Info(w.At, $"Environment variable \"{name}\" → environment[{p.Environment.Count - 1}] (removed on uninstall while unchanged)", "environment");
        if (mismatch is not null) ctx.Warn(w.At, $"Environment variable \"{name}\": {mismatch}.", "environment");
    }

    private sealed class ClassKey
    {
        public string Name = "";
        public SourceRef At;
        public string Description = "";
        public string? Icon;
        public string? Command;
        public bool UrlProtocol;
        public string IfOption = "";
        public bool Conditional;
        public string Root = "HKA";
        public bool Referenced;
    }

    private static void MapClasses(ImportContext ctx, List<(RegistryWrite W, string Path)> writes)
    {
        var p = ctx.Project;
        var extensionTargets = new List<(string Extension, string ProgId, RegistryWrite W)>();
        var contentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keys = new Dictionary<string, ClassKey>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keyOnly = new List<(SourceRef At, string Path)>();
        foreach (var (w, path) in writes)
        {
            var slash = path.IndexOf('\\');
            var name = slash < 0 ? path : path[..slash];
            var sub = slash < 0 ? "" : path[(slash + 1)..];
            if (name.Length == 0) continue;
            if (w.Name is null && !ContextMenuClasses.Contains(name, StringComparer.OrdinalIgnoreCase) && !ComClasses.Contains(name, StringComparer.OrdinalIgnoreCase)) keyOnly.Add((w.At, path));
            if (name.StartsWith('.'))
            {
                if (w.Name is null) continue;
                if (sub.Length == 0 && w.Name.Length == 0) extensionTargets.Add((name, ctx.Resolve(w.Data).Trim(), w));
                else if (sub.Equals("OpenWithProgids", StringComparison.OrdinalIgnoreCase) && w.Name.Length > 0) extensionTargets.Add((name, ctx.Resolve(w.Name).Trim(), w));
                else if (sub.Length == 0 && w.Name.Equals("Content Type", StringComparison.OrdinalIgnoreCase)) contentTypes[name] = ctx.Resolve(w.Data);
                else if (sub.Length == 0 && w.Name.Equals("PerceivedType", StringComparison.OrdinalIgnoreCase)) ctx.Info(w.At, $"PerceivedType of {name} is not written (Windows knows common types).");
                else ctx.Manual(w.At, $"Registry value Software\\Classes\\{path}\\{w.Name} is not imported (FluentSetup writes a file type's keys itself; ShellNew and other extras are not supported).", "associations");
                continue;
            }
            if (ContextMenuClasses.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (reported.Add(name)) ctx.Manual(w.At, $"Explorer context-menu entries (Software\\Classes\\{name}\\…) are not supported by FluentSetup; not imported.", "associations");
                continue;
            }
            if (ComClasses.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (reported.Add(name)) ctx.Manual(w.At, $"COM registration (Software\\Classes\\{name}\\…) is not supported; register COM objects from the app (e.g. regsvr32-free activation manifests) or an action.", "registry");
                continue;
            }
            if (name.Equals("Applications", StringComparison.OrdinalIgnoreCase))
            {
                if (reported.Add(name)) ctx.Info(w.At, "Software\\Classes\\Applications\\… is not needed: FluentSetup registers the app's file types for \"Open with\" and Default apps itself.");
                continue;
            }
            if (!keys.TryGetValue(name, out var k))
            {
                k = new ClassKey { Name = name, At = w.At, Root = w.Root };
                keys[name] = k;
            }
            if (k.IfOption.Length == 0 && w.IfOption.Length > 0) k.IfOption = w.IfOption;
            k.Conditional |= w.Conditional;
            if (w.Name is null) continue;
            if (sub.Length == 0 && w.Name.Length == 0) k.Description = w.Data;
            else if (sub.Length == 0 && w.Name.Equals("URL Protocol", StringComparison.OrdinalIgnoreCase)) k.UrlProtocol = true;
            else if (sub.Equals("DefaultIcon", StringComparison.OrdinalIgnoreCase) && w.Name.Length == 0) k.Icon = w.Data;
            else if (sub.Equals(@"shell\open\command", StringComparison.OrdinalIgnoreCase) && w.Name.Length == 0) { k.Command = w.Data; k.At = w.At; }
            else if (sub.Length == 0 || sub.Equals("shell", StringComparison.OrdinalIgnoreCase) || sub.Equals(@"shell\open", StringComparison.OrdinalIgnoreCase) || sub.Equals("CurVer", StringComparison.OrdinalIgnoreCase))
                ctx.Info(w.At, $"Software\\Classes\\{path}{(w.Name.Length > 0 ? "\\" + w.Name : "")} is not written (FluentSetup writes the file type's name, icon and open command).");
            else if (sub.StartsWith(@"shell\", StringComparison.OrdinalIgnoreCase))
                ctx.Manual(w.At, $"Verb Software\\Classes\\{path} is not imported: FluentSetup registers the \"open\" verb only.", "associations");
            else ctx.Manual(w.At, $"Registry value Software\\Classes\\{path}\\{w.Name} is not imported.", "associations");
        }

        if (keyOnly.Count > 0)
            ctx.Info(keyOnly[0].At, $"Keys without values below Software\\Classes ({string.Join(", ", keyOnly.Select(k => $"{k.Path} (line {k.At.Line})").Take(10))}): FluentSetup creates and removes the keys of the file types and protocols it registers.");
        foreach (var group in extensionTargets.GroupBy(e => e.ProgId, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (!keys.TryGetValue(group.Key, out var k))
            {
                ctx.Manual(first.W.At, $"{string.Join(", ", group.Select(g => g.Extension).Distinct(StringComparer.OrdinalIgnoreCase))} point to the file type \"{group.Key}\", which the script does not define (another program's type?). Not imported.", "associations");
                continue;
            }
            k.Referenced = true;
            if (k.Command is null)
            {
                ctx.Manual(k.At, $"File type \"{group.Key}\" has no shell\\open\\command; not imported.", "associations");
                continue;
            }
            if (!TryProgram(ctx, k.Command, out var program, out var args, out var display))
            {
                ctx.Manual(k.At, $"File type \"{group.Key}\" opens {Canonical.Display(k.Command)}: the program must be one of the app's files. Not imported.", "associations");
                continue;
            }
            var extensions = new List<string>();
            foreach (var (ext, _, w) in group)
            {
                var e = ctx.Resolve(ext);
                if (extensions.Contains(e, StringComparer.OrdinalIgnoreCase)) continue;
                if (!ExtensionRegex().IsMatch(e) || BlockedExtensions.Contains(e, StringComparer.OrdinalIgnoreCase))
                    ctx.Manual(w.At, $"Extension \"{e}\" is not imported: program and shell types are refused and extensions use 1-32 letters, digits, '-' or '_'.", "associations");
                else if (extensions.Count >= 16) ctx.Manual(w.At, $"Extension \"{e}\" is not imported: at most 16 per file type.", "associations");
                else extensions.Add(e);
            }
            if (extensions.Count == 0) continue;
            if (p.Associations.Count >= 16) { ctx.Manual(k.At, $"File type \"{group.Key}\" is not imported: at most 16 file types.", "associations"); continue; }
            var progId = SanitizeProgId(group.Key, extensions[0]);
            if (!progId.Equals(group.Key, StringComparison.Ordinal))
                ctx.Warn(k.At, $"ProgID \"{group.Key}\" is renamed to \"{progId}\" (FluentSetup needs Vendor.App.Type, letters and digits, max 39 characters). Files opened through the old ProgID keep working until it is removed.", "associations");
            var a = new FileAssociationSettings
            {
                Extensions = extensions,
                ProgId = progId,
                Description = k.Description,
                Target = ctx.MainExecutable.Length > 0 && ImportContext.SamePath(program, ctx.MainExecutable) ? "" : program,
                Arguments = args,
                ContentType = extensions.Select(e => contentTypes.GetValueOrDefault(e)).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "",
                IfOption = ctx.UseOption(k.IfOption.Length > 0 ? k.IfOption : first.W.IfOption),
            };
            SetIcon(ctx, k, program, (i, n) => { a.Icon = i; a.IconIndex = n; });
            p.Associations.Add(a);
            ctx.Info(k.At, $"File type {string.Join(", ", extensions)} (ProgID \"{progId}\") → associations[{p.Associations.Count - 1}]{(a.IfOption.Length > 0 ? $", only with option \"{a.IfOption}\"" : "")}", "associations");
            ClassNotes(ctx, k, "file type");
        }

        foreach (var k in keys.Values.Where(k => !k.Referenced))
        {
            if (!k.UrlProtocol)
            {
                ctx.Manual(k.At, $"Software\\Classes\\{k.Name} is not imported: no extension points to it and it is not a URL protocol.", "associations");
                continue;
            }
            var scheme = ctx.Resolve(k.Name).ToLowerInvariant();
            if (!SchemeRegex().IsMatch(scheme) || BlockedSchemes.Contains(scheme) || scheme.StartsWith("ms-", StringComparison.Ordinal))
            {
                ctx.Manual(k.At, $"URL protocol \"{scheme}\" is not imported: schemes of Windows and browsers are refused, others use 2-32 lowercase letters, digits, '+', '.' or '-'.", "protocols");
                continue;
            }
            if (k.Command is null || !TryProgram(ctx, k.Command, out var program, out var args, out _))
            {
                ctx.Manual(k.At, $"URL protocol \"{scheme}\" opens {(k.Command is null ? "nothing (no shell\\open\\command)" : Canonical.Display(k.Command))}: the program must be one of the app's files. Not imported.", "protocols");
                continue;
            }
            if (p.Protocols.Count >= 16) { ctx.Manual(k.At, $"URL protocol \"{scheme}\" is not imported: at most 16.", "protocols"); continue; }
            var description = k.Description;
            if (ctx.Resolve(description).StartsWith("URL:", StringComparison.OrdinalIgnoreCase)) description = description[4..].TrimStart();
            var pr = new ProtocolSettings
            {
                Scheme = scheme,
                Description = description,
                Target = ctx.MainExecutable.Length > 0 && ImportContext.SamePath(program, ctx.MainExecutable) ? "" : program,
                Arguments = args,
                IfOption = ctx.UseOption(k.IfOption),
            };
            SetIcon(ctx, k, program, (i, n) => { pr.Icon = i; pr.IconIndex = n; });
            p.Protocols.Add(pr);
            ctx.Info(k.At, $"URL protocol \"{scheme}\" → protocols[{p.Protocols.Count - 1}]{(pr.IfOption.Length > 0 ? $", only with option \"{pr.IfOption}\"" : "")}", "protocols");
            ClassNotes(ctx, k, "URL protocol");
        }
    }

    private static void ClassNotes(ImportContext ctx, ClassKey k, string what)
    {
        if (RootMismatch(ctx, k.Root) is { } m) ctx.Warn(k.At, $"{what} \"{k.Name}\": {m}.");
        if (k.Conditional) ctx.Warn(k.At, $"{what} \"{k.Name}\" depended on a script condition; it is registered whenever its option (if any) is selected.");
    }

    private static void SetIcon(ImportContext ctx, ClassKey k, string program, Action<string, int> set)
    {
        if (k.Icon is null) return;
        var text = ctx.Resolve(k.Icon).Trim();
        var index = 0;
        var comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            index = n;
            text = text[..comma].Trim();
        }
        if (ctx.TryInstallRelative(text.Trim('"'), out var icon) && icon.Length > 0)
            set(ImportContext.SamePath(icon, program) ? "" : icon, index);
        else ctx.Info(k.At, $"Icon {Canonical.Display(k.Icon)} of \"{k.Name}\" is not one of the app's files; the program's icon is used.");
    }

    /// <summary>A ProgID that passes FluentSetup's rule (^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z0-9]+)+$, max 39).</summary>
    public static string SanitizeProgId(string progId, string extension)
    {
        if (ProgIdRegex().IsMatch(progId) && progId.Length <= 39) return progId;
        var parts = Regex.Split(progId, "[^A-Za-z0-9]+").Where(s => s.Length > 0).ToList();
        while (parts.Count > 0 && !char.IsAsciiLetter(parts[0][0])) parts[0] = parts[0].TrimStart("0123456789".ToCharArray()) is { Length: > 0 } t ? t : "";
        parts.RemoveAll(s => s.Length == 0);
        if (parts.Count == 0) parts.Add("App");
        if (parts.Count == 1) parts.Add(Regex.Replace(extension.TrimStart('.'), "[^A-Za-z0-9]", "") is { Length: > 0 } e ? e : "File");
        var id = string.Join('.', parts);
        while (id.Length > 39 && parts.Count > 2) { parts.RemoveAt(1); id = string.Join('.', parts); }
        if (id.Length > 39) id = id[..39].TrimEnd('.');
        return ProgIdRegex().IsMatch(id) ? id : "App.File";
    }

    private static bool IsVendorKey(string key)
    {
        var parts = key.Split('\\');
        return key.Length <= 250 && parts.Length >= 2 && parts[0].Equals("Software", StringComparison.OrdinalIgnoreCase) &&
               parts.All(x => x.Length is > 0 and <= 128 && x.All(c => c >= 0x20)) && !SharedTrees.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
    }

    private static void MapVendorKeys(ImportContext ctx, List<RegistryWrite> writes)
    {
        var p = ctx.Project;
        foreach (var byKey in writes.GroupBy(w => (Root: w.Root, Key: ctx.Resolve(w.Key).ToUpperInvariant())))
        {
            var w0 = byKey.First();
            var key = ctx.Resolve(w0.Key);
            var rootName = RootName(ctx, w0.Root);
            if (!IsVendorKey(key))
            {
                ctx.Manual(w0.At, $"Registry key {rootName}\\{key} is not imported: FluentSetup writes app keys below Software\\<Vendor> only (not Microsoft, Classes, Policies or other shared trees).", "registry");
                continue;
            }
            var uninstall = byKey.Any(w => w.Uninstall == RegistryUninstall.Key) ? "key" : byKey.Any(w => w.Uninstall == RegistryUninstall.Values) ? "values" : "keep";
            var mismatch = RootMismatch(ctx, w0.Root);
            if (!byKey.Any(w => w.Name is not null))
            {
                if (uninstall == "key") RemoveKeyOnUninstall(ctx, w0, key);
                else ctx.Info(w0.At, $"Registry key {rootName}\\{key} has no values; FluentSetup creates keys only together with values. Skipped.");
                continue;
            }
            if (mismatch is not null)
            {
                ctx.Manual(w0.At, $"Registry values in {w0.Root}\\{key} are not imported: {mismatch}. Let the app write them, or change the install scope.", "registry");
                if (uninstall == "key") RemoveKeyOnUninstall(ctx, w0, key);
                continue;
            }
            foreach (var byOption in byKey.Where(w => w.Name is not null).GroupBy(w => w.IfOption, StringComparer.OrdinalIgnoreCase))
            {
                var first = byOption.First();
                if (p.Registry.Count >= 32) { ctx.Manual(first.At, $"Registry key {key} is not imported: at most 32 registry keys.", "registry"); break; }
                var entry = new RegistryEntrySettings { Key = first.Key, Uninstall = uninstall, IfOption = ctx.UseOption(byOption.Key) };
                foreach (var w in byOption)
                {
                    if (RegistryValue(ctx, w, key) is not { } v) continue;
                    entry.Values.RemoveAll(x => x.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase));
                    if (entry.Values.Count >= 32) { ctx.Manual(w.At, $"Registry value \"{w.Name}\" in {key} is not imported: at most 32 values per key.", "registry"); continue; }
                    entry.Values.Add(v);
                    if (w.Conditional) ctx.Warn(w.At, $"Registry value \"{w.Name}\" in {key} depended on a script condition; it is always written{(entry.IfOption.Length > 0 ? " with its option" : "")}.", "registry");
                }
                if (entry.Values.Count == 0) continue;
                p.Registry.Add(entry);
                ctx.Info(first.At, $"Registry key {rootName}\\{key} ({entry.Values.Count} value{(entry.Values.Count == 1 ? "" : "s")}, uninstall: {uninstall}) → registry[{p.Registry.Count - 1}]" +
                                   (entry.IfOption.Length > 0 ? $", only with option \"{entry.IfOption}\"" : ""), "registry");
            }
            if (uninstall == "keep")
                ctx.Info(w0.At, $"Registry key {key}: the script did not delete it on uninstall, so registry uninstall is \"keep\" (use \"values\" or \"key\" to clean up).", "registry");
        }
    }

    private static void RemoveKeyOnUninstall(ImportContext ctx, RegistryWrite w, string key)
    {
        var p = ctx.Project;
        var scope = p.Install.Scope;
        if (w.Root == "HKCU" || (w.Root == "HKA" && scope != "machine"))
        {
            var full = @"HKCU\" + key;
            if (!p.Uninstall.RemoveRegistryKeys.Contains(full, StringComparer.OrdinalIgnoreCase)) p.Uninstall.RemoveRegistryKeys.Add(full);
            ctx.Info(w.At, $"{full} is deleted on uninstall → uninstall.removeRegistryKeys", "uninstall.removeRegistryKeys");
            if (w.Root == "HKA" && scope == "userOrMachine") ctx.Warn(w.At, $"{key}: per-machine installs do not delete HKLM\\{key} (add it to uninstall.elevated.removeRegistryKeys if needed).", "uninstall.removeRegistryKeys");
        }
        else if (w.Root == "HKLM" || w.Root == "HKA")
        {
            var full = @"HKLM\" + key;
            if (!p.Uninstall.Elevated.RemoveRegistryKeys.Contains(full, StringComparer.OrdinalIgnoreCase)) p.Uninstall.Elevated.RemoveRegistryKeys.Add(full);
            ctx.Info(w.At, $"{full} is deleted on uninstall → uninstall.elevated.removeRegistryKeys", "uninstall.elevated.removeRegistryKeys");
        }
        else ctx.Manual(w.At, $"{w.Root}\\{key} is deleted on uninstall by the script; FluentSetup removes HKCU and HKLM keys only.", "uninstall.removeRegistryKeys");
    }

    [GeneratedRegex(@"^(0x[0-9A-Fa-f]{1,16}|[0-9]{1,20})$")]
    private static partial Regex NumberRegex();

    private static RegistryValueSettings? RegistryValue(ImportContext ctx, RegistryWrite w, string key)
    {
        var type = w.Type;
        if (type is not ("string" or "expandString" or "dword" or "qword" or "multiString"))
        {
            ctx.Manual(w.At, $"Registry value \"{w.Name}\" in {key} has type {type}, which FluentSetup does not write (string, expandString, dword, qword, multiString). Not imported.", "registry");
            return null;
        }
        if (Canonical.AsValue(w.Data) is not { } data)
        {
            ctx.Manual(w.At, $"Registry value \"{w.Name}\" in {key} = {Canonical.Display(w.Data)} uses a folder or constant FluentSetup cannot fill in ({{InstallDir}}, {{AppVersion}} … only). Not imported.", "registry");
            return null;
        }
        if (type is "dword" or "qword")
        {
            var resolved = ctx.Resolve(data).Trim();
            if (resolved.StartsWith('$')) data = resolved = "0x" + resolved[1..];  // Inno Pascal hex: $1F
            if (!ctx.HasUnknownVariable(data) && !NumberRegex().IsMatch(resolved))
            {
                ctx.Manual(w.At, $"Registry value \"{w.Name}\" in {key}: \"{resolved}\" is not a number. Not imported.", "registry");
                return null;
            }
        }
        return new RegistryValueSettings { Name = w.Name ?? "", Type = type, Data = data };
    }

    // ---- files -------------------------------------------------------------------------------------------------

    private static string? RootOf(FileEntry e)
    {
        var dir = Path.GetDirectoryName(e.Source);
        if (dir is null) return null;
        if (e.DestDir.Length == 0) return dir;
        var segments = e.DestDir.Split('/');
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (!Path.GetFileName(dir).Equals(segments[i], StringComparison.OrdinalIgnoreCase)) return null;
            dir = Path.GetDirectoryName(dir);
            if (dir is null) return null;
        }
        return dir;
    }

    private static bool IsWildcard(string pattern) => pattern.IndexOfAny(['*', '?']) >= 0;

    private static void MapFiles(ImportContext ctx)
    {
        var f = ctx.Project.Files;
        var include = new List<string>();
        var exclude = new List<string>();
        var preserve = new List<string>();
        var explicitFiles = new List<string>();
        var wildcards = false;
        foreach (var (at, dir) in ctx.EmptyDirectories)
        {
            if (!f.EmptyDirectories.Contains(dir, StringComparer.OrdinalIgnoreCase)) f.EmptyDirectories.Add(dir);
            ctx.Info(at, $"Folder \"{dir}\" → files.emptyDirectories", "files.emptyDirectories");
        }
        if (ctx.Files.Count == 0)
        {
            ctx.Manual(ctx.Script, "The script installs no files the importer could map: set files.source to the folder with the app's files.", "files.source");
            return;
        }

        var weights = new Dictionary<string, (int Weight, int Order)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ctx.Files)
        {
            if (RootOf(e) is not { } r) continue;
            // Wildcards weigh more than single files, and what goes into the install folder itself most: that is the build output.
            var w = (IsWildcard(Path.GetFileName(e.Source)) ? (e.Recurse ? 100 : 10) : 1) * (e.DestDir.Length == 0 ? 10 : 1);
            weights[r] = weights.TryGetValue(r, out var old) ? (old.Weight + w, old.Order) : (w, weights.Count);
        }
        var root = weights.Count > 0 ? weights.OrderByDescending(x => x.Value.Weight).ThenBy(x => x.Value.Order).First().Key : Path.GetDirectoryName(ctx.Files[0].Source)!;
        f.Source = ctx.WithVariables(ctx.ProjectPath(root), ctx.Files.Where(e => string.Equals(RootOf(e), root, StringComparison.OrdinalIgnoreCase)).SelectMany(e => e.Variables));
        ctx.Info(ctx.Files[0].At, $"Files: \"{f.Source}\" (the folder most file entries come from) → files.source", "files.source");

        foreach (var e in ctx.Files)
        {
            var dir = Path.GetDirectoryName(e.Source)!;
            var pattern = Path.GetFileName(e.Source);
            if (pattern is "*.*") pattern = "*";
            var wildcard = IsWildcard(pattern);
            wildcards |= wildcard;
            var display = ctx.WithVariables(ctx.ProjectPath(e.Source), e.Variables);
            if (e.DestName.Length > 0 && !e.DestName.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Manual(e.At, $"{display} is installed as \"{e.DestName}\": FluentSetup installs files under their own names. Rename it in the build output, then add it. Not imported.", "files");
                continue;
            }
            var dest = e.DestDir.Length == 0 ? "" : e.DestDir + "/";
            if (string.Equals(RootOf(e), root, StringComparison.OrdinalIgnoreCase))
            {
                var glob = e.Recurse ? $"{dest}**/{pattern}" : e.DestDir.Length == 0 ? "./" + pattern : dest + pattern;
                if (!include.Contains(glob, StringComparer.OrdinalIgnoreCase)) include.Add(glob);
                if (!wildcard && !e.Recurse) explicitFiles.Add(dest + pattern);
                if (e.Preserve && !preserve.Contains(glob, StringComparer.OrdinalIgnoreCase)) preserve.Add(glob);
                foreach (var x in e.Excludes) foreach (var g in ExcludeGlobs(e.DestDir, x)) if (!exclude.Contains(g, StringComparer.OrdinalIgnoreCase)) exclude.Add(g);
                ctx.Info(e.At, $"{display} → files.include \"{glob}\"{(e.Preserve ? " and files.preserve" : "")}", "files.include");
                continue;
            }
            if (!wildcard)
            {
                f.Extra.Add(new ExtraSource { Source = display, Target = e.DestDir });
                explicitFiles.Add(dest + pattern);
                if (e.Preserve) preserve.Add(e.DestDir.Length == 0 ? "./" + pattern : dest + pattern);
                ctx.Info(e.At, $"{display} → files.extra (target \"{e.DestDir}\"){(e.Preserve ? " and files.preserve" : "")}", "files.extra");
                if (e.Recurse) ctx.Warn(e.At, $"{display}: copies of \"{pattern}\" in subfolders are not picked up (files.extra adds this one file).", "files.extra");
                continue;
            }
            var exists = Directory.Exists(dir);
            if (pattern == "*" && (e.Recurse || !exists))
            {
                var folder = ctx.WithVariables(ctx.ProjectPath(dir), e.Variables);
                f.Extra.Add(new ExtraSource { Source = folder, Target = e.DestDir });
                if (e.Preserve) preserve.Add($"{dest}**/*");
                ctx.Info(e.At, $"{display} → files.extra folder \"{folder}\" (target \"{e.DestDir}\")", "files.extra");
                if (!e.Recurse) ctx.Warn(e.At, $"{ctx.ProjectPath(dir)}: files.extra installs subfolders too; the script installed only the files directly in it.", "files.extra");
                if (e.Excludes.Count > 0) ctx.Warn(e.At, $"{ctx.ProjectPath(dir)}: files.extra folders are installed without filters; the exclusions ({string.Join(", ", e.Excludes)}) do not apply.", "files.extra");
                continue;
            }
            if (exists)
            {
                var matches = Directory.EnumerateFiles(dir, pattern, e.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                                       .Order(StringComparer.OrdinalIgnoreCase).Take(33).ToList();
                if (matches.Count is > 0 and <= 32)
                {
                    foreach (var m in matches)
                    {
                        var sub = Path.GetRelativePath(dir, Path.GetDirectoryName(m)!).Replace('\\', '/');
                        var target = sub == "." ? e.DestDir : (dest + sub);
                        f.Extra.Add(new ExtraSource { Source = ctx.WithVariables(ctx.ProjectPath(m), e.Variables), Target = target });
                        if (e.Preserve) preserve.Add((target.Length == 0 ? "./" : target + "/") + Path.GetFileName(m));
                    }
                    ctx.Warn(e.At, $"{display} → {matches.Count} files.extra entries (the files that match now; files added to that folder later are not picked up).", "files.extra");
                    continue;
                }
            }
            ctx.Manual(e.At, $"{display} (only \"{pattern}\" from a folder other than files.source) cannot be expressed with files.extra. Move those files into files.source or list them in files.extra. Not imported.", "files.extra");
        }

        if (include.Contains("**/*")) include = ["**/*"];
        f.Include = include.Count > 0 ? include : ["**/*"];
        var defaults = new FileSettings().Exclude;
        var kept = defaults.Where(d => !explicitFiles.Any(x => new Glob(d).IsMatch(x))).ToList();
        f.Exclude = [.. kept, .. exclude.Where(x => !kept.Contains(x, StringComparer.OrdinalIgnoreCase))];
        f.Preserve = preserve.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wildcards && kept.Count > 0)
            ctx.Info(ctx.Script, $"files.exclude keeps FluentSetup's default exclusions ({string.Join(", ", kept)}): debug symbols and similar build leftovers are not installed even where the script's wildcards matched them.", "files.exclude");
        if (ctx.MainExecutable.Length > 0 && !Covered(f, ctx.MainExecutable))
            ctx.Warn(ctx.Script, $"app.mainExecutable \"{ctx.MainExecutable}\" is not among the files mapped from the script; check files.include/extra.", "app.mainExecutable");
    }

    /// <summary>Glob(s) for an Inno Excludes / NSIS /x pattern of an entry installed into <paramref name="destDir"/>.</summary>
    private static IEnumerable<string> ExcludeGlobs(string destDir, string pattern)
    {
        var x = pattern.Trim().Replace('\\', '/');
        if (x.Length == 0) yield break;
        var prefix = destDir.Length == 0 ? "" : destDir + "/";
        string glob;
        if (x.StartsWith('/')) glob = (prefix.Length == 0 ? "./" : prefix) + x.TrimStart('/');
        else glob = prefix.Length == 0 ? (x.Contains('/') ? "**/" + x : x) : prefix + "**/" + x;
        glob = glob.TrimEnd('/');
        if (glob.Length == 0 || glob.Contains("..", StringComparison.Ordinal)) yield break;
        yield return glob;
        if (!IsWildcard(x)) yield return glob + "/";  // also a folder of that name
    }

    private static bool Covered(FileSettings f, string relative)
    {
        if (f.Include.Any(i => new Glob(i).IsMatch(relative)) && !f.Exclude.Any(x => new Glob(x).IsMatch(relative))) return true;
        foreach (var e in f.Extra)
        {
            var target = e.Target.Length == 0 ? "" : e.Target.Replace('\\', '/').TrimEnd('/') + "/";
            if (ImportContext.SamePath(target + Path.GetFileName(e.Source), relative)) return true;
            if (!Path.HasExtension(e.Source) && relative.StartsWith(target, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---- options, variables, names -----------------------------------------------------------------------------

    private static void MapOptions(ImportContext ctx)
    {
        var p = ctx.Project;
        foreach (var o in ctx.Options)
        {
            if (!o.Used)
            {
                if (o.AbsorbedInto.Length > 0) continue;
                if (o.Unexpressed.Count > 0)
                    ctx.Warn(o.At, $"{Capitalize(o.Origin)} controls {string.Join(", ", o.Unexpressed.Distinct())} in the script, which a FluentSetup option cannot control (always installed). Not imported as an option.", "options");
                else ctx.Info(o.At, $"{Capitalize(o.Origin)} controls nothing the project can express; not imported as an option.", "options");
                continue;
            }
            if (p.Options.Count >= 16) { ctx.Manual(o.At, $"{Capitalize(o.Origin)} is not imported: at most 16 options.", "options"); continue; }
            var label = o.Label.Count > 0 && o.Label.Values.All(v => v.Trim().Length > 0) ? o.Label : new Dictionary<string, string> { ["en-US"] = o.Id };
            p.Options.Add(new UserOptionSettings { Id = o.Id, Label = label, Default = o.Default || o.Required, Required = o.Required, Startup = o.Startup, QuietDefault = o.Required ? null : o.QuietDefault });
            ctx.Info(o.At, $"{Capitalize(o.Origin)} → options[\"{o.Id}\"]{(o.Required ? " (required)" : o.Default ? " (checked)" : " (unchecked)")}", "options");
            if (o.Unexpressed.Count > 0)
                ctx.Warn(o.At, $"{Capitalize(o.Origin)} also controls {string.Join(", ", o.Unexpressed.Distinct())}, which an option cannot control: always installed.", "options");
        }
        // An option referenced by an action or entry that was dropped would leave a dangling ifOption.
        var ids = p.Options.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var a in p.Actions.Where(a => a.IfOption.Length > 0 && !ids.Contains(a.IfOption))) a.IfOption = "";
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>output.fileName: the variables holding the app's version and name become {Version} and {AppName}.</summary>
    private static void NormalizeFileName(ImportContext ctx)
    {
        var p = ctx.Project;
        var name = p.Output.FileName;
        if (IsSingleVariable(p.App.Version)) name = name.Replace(p.App.Version, "{Version}", StringComparison.OrdinalIgnoreCase);
        if (IsSingleVariable(p.App.Name)) name = name.Replace(p.App.Name, "{AppName}", StringComparison.OrdinalIgnoreCase);
        p.Output.FileName = name;
    }

    private static bool IsSingleVariable(string s) => s.StartsWith("$(", StringComparison.Ordinal) && s.EndsWith(')') && s.IndexOf(')') == s.Length - 1;

    private static void MapVariables(ImportContext ctx)
    {
        var p = ctx.Project;
        p.Variables = [];
        var json = ProjectSerializer.Serialize(p);
        foreach (var name in ImportContext.ReferencedVariables(json).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var known = ctx.Variables.TryGetValue(name, out var value);
            p.Variables[name] = value ?? (name.Contains("ver", StringComparison.OrdinalIgnoreCase) ? "1.0.0" : "");
            if (!known) ctx.Warn(ctx.Script, $"Variable $({name}) has no value in the script; set it in \"variables\" or with -D {name}=….", "variables");
            else if (ctx.VariableNotes.TryGetValue(name, out var n))
            {
                var message = value is null ? $"{n.Message} The project starts with \"{p.Variables[name]}\": set variables.{name} or build with -D {name}=…." : n.Message;
                if (n.Severity == ImportSeverity.Manual || value is null) ctx.Manual(n.At, message, "variables");
                else if (n.Severity == ImportSeverity.Warning) ctx.Warn(n.At, message, "variables");
                else ctx.Info(n.At, message, "variables");
            }
        }
    }
}
