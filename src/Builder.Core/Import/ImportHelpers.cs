using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;

namespace FluentSetup.Builder.Core.Import;

/// <summary>Recognises things both script formats do in the same way: prerequisite installers, system tools, app ids.</summary>
internal static partial class ImportHelpers
{
    [GeneratedRegex(@"^vc_?redist[._-]?(x64|x86|arm64)\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex VcRedistRegex();

    [GeneratedRegex(@"^MicrosoftEdgeWebview2(Setup|RuntimeInstaller[A-Za-z0-9]*)\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex WebView2Regex();

    [GeneratedRegex(@"^(windowsdesktop|dotnet|aspnetcore)-runtime-(\d+\.\d+\.\d+)-win-(x64|x86|arm64)\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetRegex();

    [GeneratedRegex(@"^ndp(4)(\d)(\d?)-.*\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex NetFrameworkRegex();

    [GeneratedRegex(@"^windowsappruntimeinstall-(x64|arm64)\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsAppRuntimeRegex();

    /// <summary>
    /// A known Microsoft prerequisite installer (VC++ Redistributable, WebView2, .NET, .NET Framework, Windows App
    /// Runtime) run by the script becomes a requirement whose official installer the builder bundles.
    /// </summary>
    public static bool TryPrerequisite(ImportContext ctx, string program, SourceRef at)
    {
        var name = Path.GetFileName(ctx.Resolve(program).Replace('/', '\\').Split('\\')[^1].Trim('"'));
        var r = ctx.Project.Requirements;
        const string how = " (the builder downloads Microsoft's installer when it builds the package; setup runs it only if the component is missing)";
        if (VcRedistRegex().Match(name) is { Success: true } vc)
        {
            var arch = vc.Groups[1].Value.ToLowerInvariant();
            r.VcRedist.Required = true;
            r.VcRedist.Auto = "bundle";
            r.VcRedist.Architecture = arch == ctx.Project.Output.Architecture ? "" : arch;
            ctx.Info(at, $"{name} → requirements.vcRedist{how}", "requirements.vcRedist");
            return true;
        }
        if (WebView2Regex().IsMatch(name))
        {
            r.WebView2.Required = true;
            r.WebView2.Auto = "bundle";
            ctx.Info(at, $"{name} → requirements.webView2{how}", "requirements.webView2");
            return true;
        }
        if (DotNetRegex().Match(name) is { Success: true } dn)
        {
            var runtime = dn.Groups[1].Value.ToLowerInvariant() switch { "windowsdesktop" => "desktop", "aspnetcore" => "aspnetcore", _ => "core" };
            if (!r.DotNet.Any(d => d.Runtime == runtime))
                r.DotNet.Add(new DotNetRequirement { Runtime = runtime, Version = dn.Groups[2].Value, Auto = "bundle" });
            ctx.Info(at, $"{name} → requirements.dotNet ({runtime} {dn.Groups[2].Value}){how}", "requirements.dotNet");
            return true;
        }
        if (NetFrameworkRegex().Match(name) is { Success: true } nf)
        {
            var version = $"4.{nf.Groups[2].Value}" + (nf.Groups[3].Value.Length > 0 ? "." + nf.Groups[3].Value : "");
            if (ExtensionValidator.NetFrameworkRelease(version) is not null)
            {
                r.NetFramework = version;
                r.NetFrameworkSetup = new PrerequisiteSource { Auto = "bundle", Arguments = ["/q", "/norestart"] };
                ctx.Info(at, $"{name} → requirements.netFramework {version}{how}", "requirements.netFramework");
                return true;
            }
        }
        if (WindowsAppRuntimeRegex().IsMatch(name))
        {
            ctx.Manual(at, $"{name} installs the Windows App Runtime: set requirements.windowsAppRuntime (required, the Windows App SDK version, auto \"bundle\").", "requirements.windowsAppRuntime");
            return true;
        }
        return false;
    }

    /// <summary>
    /// A system tool the script runs (taskkill, schtasks, sc, netsh, regsvr32 …): mapped where FluentSetup has a
    /// declarative equivalent, otherwise reported with that equivalent. False if <paramref name="program"/> is not one.
    /// </summary>
    public static bool TrySystemCommand(ImportContext ctx, string program, IReadOnlyList<string> args, bool uninstall, SourceRef at)
    {
        var exe = Path.GetFileName(ctx.Resolve(program).Replace('/', '\\').Split('\\')[^1].Trim('"')).ToLowerInvariant();
        if (exe.EndsWith(".exe", StringComparison.Ordinal)) exe = exe[..^4];
        var lower = args.Select(a => Canonical.Display(ctx.Resolve(a))).ToList();
        string? Value(string flag)
        {
            var i = lower.FindIndex(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < lower.Count ? lower[i + 1] : null;
        }
        var p = ctx.Project;
        switch (exe)
        {
            case "taskkill":
            {
                var image = Value("/im");
                if (image is not null && PathRules.IsValidComponent(image, out _) && image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (!p.Install.CloseProcesses.Contains(image, StringComparer.OrdinalIgnoreCase)) p.Install.CloseProcesses.Add(image);
                    ctx.Info(at, $"taskkill /im {image} → install.closeProcesses (closed politely, then ended, before installing and uninstalling)", "install.closeProcesses");
                }
                else ctx.Manual(at, $"taskkill {string.Join(' ', lower)}: list the program names in install.closeProcesses.", "install.closeProcesses");
                return true;
            }
            case "schtasks":
            {
                var task = Value("/tn");
                if (lower.Any(a => a.Equals("/delete", StringComparison.OrdinalIgnoreCase)) && task is { Length: > 0 })
                {
                    var list = uninstall ? p.Uninstall.RemoveScheduledTasks : p.Install.Cleanup.RemoveScheduledTasks;
                    if (!list.Contains(task, StringComparer.OrdinalIgnoreCase)) list.Add(task);
                    ctx.Info(at, $"schtasks /delete /tn \"{task}\" → {(uninstall ? "uninstall.removeScheduledTasks" : "install.cleanup.removeScheduledTasks")}", uninstall ? "uninstall.removeScheduledTasks" : "install.cleanup.removeScheduledTasks");
                    if (task.Contains('\\') && uninstall) ctx.Warn(at, $"Task \"{task}\" is in a folder; uninstall.removeScheduledTasks takes root-folder names (use uninstall.elevated.scheduledTasks for folders).", "uninstall.removeScheduledTasks");
                }
                else ctx.Manual(at, $"schtasks {string.Join(' ', lower)}: FluentSetup cannot run system tools. Let the app (or an action that runs one of its own programs) register the task.", "actions");
                return true;
            }
            case "sc":
                ctx.Manual(at, $"sc {string.Join(' ', lower)}: declare the Windows service in services[] (per-machine installs) instead.", "services");
                return true;
            case "netsh":
                ctx.Manual(at, $"netsh {string.Join(' ', lower)}: declare firewall rules in firewall[] (per-machine installs) instead.", "firewall");
                return true;
            case "regsvr32" or "regasm" or "regsvcs":
                ctx.Manual(at, $"{exe} {string.Join(' ', lower)}: COM registration is not supported; use registration-free COM or an action that runs one of the app's own programs.", "actions");
                return true;
            case "reg":
                ctx.Manual(at, $"reg {string.Join(' ', lower)}: declare registry values in registry[] (Software\\<Vendor>) instead.", "registry");
                return true;
            case "msiexec" or "cmd" or "powershell" or "pwsh" or "wscript" or "cscript" or "rundll32" or "explorer" or "net" or "icacls" or "takeown" or "ngen" or "dism" or "pnputil" or "bcdedit" or "wevtutil" or "logman":
                ctx.Manual(at, $"{exe} {string.Join(' ', lower)}: FluentSetup runs only programs of the app itself (no shell or system tools). " +
                               (exe == "ngen" ? "Use install.ngen." : exe == "logman" ? "Use uninstall.elevated.traceSessions." : "Do this in the app, or in an action that runs one of its own programs."), "actions");
                return true;
            default:
                return false;
        }
    }

    [GeneratedRegex(@"^(--?|/)(exit|quit|close|shutdown|stop|kill)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExitArgumentRegex();

    /// <summary>
    /// Runs of the app's own programs that are not install steps: the main program started without waiting (an in-app
    /// update starting the app again → install.relaunchArguments), any other program started without waiting (it keeps
    /// running; an action would wait for it), and the main program asked to exit before uninstalling
    /// (→ install.closeCommand). True if handled.
    /// </summary>
    public static bool TryAppLaunch(ImportContext ctx, bool isMain, bool uninstall, bool noWait, List<string> arguments, string what, SourceRef at)
    {
        var p = ctx.Project;
        if (uninstall && isMain && arguments.Count > 0 && ExitArgumentRegex().IsMatch(arguments[0]))
        {
            if (p.Install.CloseCommand is null)
            {
                p.Install.CloseCommand = new CommandSettings { Run = ctx.MainExecutable, Arguments = arguments };
                ctx.Info(at, $"{what} {string.Join(' ', arguments)} → install.closeCommand (asks a running copy to exit before files are replaced or removed, on updates too)", "install.closeCommand");
            }
            else ctx.Info(at, $"{what} {string.Join(' ', arguments)}: install.closeCommand is already set.", "install.closeCommand");
            return true;
        }
        if (!noWait || uninstall) return false;
        if (isMain)
        {
            p.Install.RelaunchArguments = arguments;
            ctx.Warn(at, $"{what} starts the app after installing → install.relaunchArguments [{string.Join(", ", arguments)}]: FluentSetup starts it again after quiet and passive installs run with /relaunch (Inno /RELAUNCH=1, NSIS /UPDATE). Make sure the app's updater passes it.", "install.relaunchArguments");
            return true;
        }
        ctx.Manual(at, $"{what} is started without waiting, so it probably keeps running; actions wait for their program to finish (and stop it after the timeout). Not imported: start it from the app, or use options[].startup.", "actions");
        return true;
    }

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonAlnumRegex();

    private static readonly string[] CompanySuffixes = ["Inc", "Ltd", "LLC", "GmbH", "Corp", "Corporation", "Co", "Limited", "AG", "SA", "BV", "Pty", "Srl", "Oy", "AB", "KK"];

    /// <summary>An app id from publisher and name: "Contoso Ltd" + "Contoso Notes" → "Contoso.Notes". Falls back to the other hints.</summary>
    public static string DeriveAppId(string publisher, string name, params string[] fallbacks)
    {
        static string Words(string s, bool company)
        {
            var words = NonAlnumRegex().Split(s).Where(w => w.Length > 0).ToList();
            if (company && words.Count > 1 && CompanySuffixes.Contains(words[^1], StringComparer.OrdinalIgnoreCase)) words.RemoveAt(words.Count - 1);
            return string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        }
        var pub = Words(Canonical.Strip(publisher), true);
        var app = Words(Canonical.Strip(name), false);
        foreach (var f in fallbacks)
        {
            if (app.Length > 0) break;
            app = Words(Path.GetFileNameWithoutExtension(f), false);
        }
        if (pub.Length > 0 && app.Length > pub.Length && app.StartsWith(pub, StringComparison.OrdinalIgnoreCase)) app = app[pub.Length..];
        if (app.Length == 0) app = "App";
        if (char.IsAsciiDigit(app[0])) app = "App" + app;
        var id = pub.Length > 0 && !pub.Equals(app, StringComparison.OrdinalIgnoreCase) ? $"{pub}.{app}" : app;
        if (id.Length > 64) id = id[..64].TrimEnd('.');
        return PathRules.IsValidAppId(id) ? id : "Imported.App";
    }

    [GeneratedRegex(@"^\d{1,10}(\.\d{1,10}){0,3}")]
    private static partial Regex VersionPrefixRegex();

    /// <summary>A version FluentSetup accepts: "v1.2" → "1.2", "1.2.3 beta" → "1.2.3"; null if there are no numbers.</summary>
    public static string? FixVersion(string version)
    {
        var v = version.Trim();
        if (ProjectValidator.IsValidVersion(v)) return v;
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        if (ProjectValidator.IsValidVersion(v)) return v;
        var m = VersionPrefixRegex().Match(v);
        return m.Success && ProjectValidator.IsValidVersion(m.Value) ? m.Value : null;
    }

    /// <summary>Sets app.version from script text, keeping a variable when the text is one and fixing what FluentSetup would reject.</summary>
    public static void SetVersion(ImportContext ctx, string raw, SourceRef at, string what)
    {
        var p = ctx.Project;
        var resolved = ctx.Resolve(raw).Trim();
        var single = raw.StartsWith("$(", StringComparison.Ordinal) && raw.EndsWith(')') && raw.IndexOf(')') == raw.Length - 1;
        if (ctx.HasUnknownVariable(raw) || ProjectValidator.IsValidVersion(resolved))
        {
            p.App.Version = raw;
            ctx.Info(at, $"{what} → app.version", "app.version");
            return;
        }
        var fixedVersion = FixVersion(resolved);
        if (single && fixedVersion is not null)
        {
            var name = raw[2..^1];
            ctx.Variables[name] = fixedVersion;
            p.App.Version = raw;
            ctx.Warn(at, $"{what} \"{resolved}\" is not a version FluentSetup accepts (1-4 numbers, optional -tag); variables.{name} is \"{fixedVersion}\".", "app.version");
            return;
        }
        p.App.Version = fixedVersion ?? "1.0.0";
        if (fixedVersion is null) ctx.Manual(at, $"{what} \"{resolved}\" is not a version; app.version is 1.0.0 for now.", "app.version");
        else ctx.Warn(at, $"{what} \"{resolved}\" → app.version \"{fixedVersion}\" (1-4 numbers, optional -tag).", "app.version");
    }

    /// <summary>Sets app.website if the text is an http(s) URL.</summary>
    public static void SetWebsite(ImportContext ctx, string raw, SourceRef at, string what)
    {
        var url = ctx.Resolve(raw).Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
        {
            ctx.Project.App.Website = raw.Trim();
            ctx.Info(at, $"{what} → app.website", "app.website");
        }
        else ctx.Warn(at, $"{what} \"{url}\" is not an http(s) URL; not imported.", "app.website");
    }
}
