using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using FluentSetup.Builder.Core.Validation;

namespace FluentSetup.Builder.Services;

/// <summary>
/// Launches the real WinUI installer front end in preview mode ("FluentSetup.UI.exe --preview hello.json").
/// The hello document has the same shape as the bootstrapper's (src/Bootstrapper/controller.cpp, Controller::Hello);
/// no engine is attached, so nothing is installed.
/// </summary>
public static class PreviewLauncher
{
    public static string UiExe(string arch) => Path.Combine(AppContext.BaseDirectory, "runtime", arch, "ui", "FluentSetup.UI.exe");

    public static void Launch(SetupProject p, string projectDir, long payloadBytes, string scenario)
    {
        var assets = new RuntimeAssets();
        var arch = p.Output.Architecture is "arm64" ? "arm64" : "x64";
        var exe = UiExe(arch);
        if (!File.Exists(exe)) throw new FileNotFoundException(Loc.Get("Preview_MissingRuntime"), exe);

        var temp = Path.Combine(Path.GetTempPath(), "FluentSetup-preview-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);

        var iconPath = Path.Combine(temp, "app.ico");
        byte[] icon = !string.IsNullOrEmpty(p.App.Icon) && File.Exists(ProjectValidator.Resolve(projectDir, p.App.Icon))
            ? IconConverter.LoadAsIco(ProjectValidator.Resolve(projectDir, p.App.Icon))
            : File.ReadAllBytes(assets.DefaultIcon);
        File.WriteAllBytes(iconPath, icon);

        var language = Loc.InstallerLanguage(p.Ui.DefaultLanguage);
        var strings = LoadStrings(assets.LocalizationFile("en-US"));
        if (language != "en-US") foreach (var (k, v) in LoadStrings(assets.LocalizationFile(language))) strings[k] = v?.DeepClone();
        if (p.Ui.Strings.TryGetValue(language, out var overrides)) foreach (var (k, v) in overrides) strings[k] = v;

        var license = "";
        if (!string.IsNullOrEmpty(p.Ui.License))
        {
            var lic = ProjectValidator.Resolve(projectDir, p.Ui.License);
            if (File.Exists(lic)) license = File.ReadAllText(lic);
        }

        static string Expand(string template, SetupProject p) => template
            .Replace("{LocalAppData}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.Ordinal)
            .Replace("{UserProgramFiles}", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), StringComparison.Ordinal)
            .Replace("{AppData}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.Ordinal)
            .Replace("{ProgramFiles}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.Ordinal)
            .Replace("{ProgramData}", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.Ordinal)
            .Replace("{AppName}", p.App.Name, StringComparison.Ordinal)
            .Replace("{Publisher}", string.IsNullOrEmpty(p.App.Publisher) ? p.App.Name : p.App.Publisher, StringComparison.Ordinal)
            .Replace("{AppId}", p.App.Id, StringComparison.Ordinal);

        static JsonObject Offer(string offer) => new()
        {
            ["visible"] = offer is "optionalOff" or "optionalOn",
            ["value"] = offer is "on" or "optionalOn",
        };

        var machine = p.Install.Scope == "machine" || (p.Install.Scope == "userOrMachine" && p.Install.DefaultScope == "machine");
        var userDir = Expand(p.Install.UserDir, p);
        var machineDir = Expand(p.Install.MachineDir, p);
        var uninstall = scenario == "uninstall";
        var hello = new JsonObject
        {
            ["t"] = "hello",
            ["proto"] = 1,
            ["mode"] = uninstall ? "uninstall" : "install",
            ["language"] = language,
            ["elevated"] = false,
            ["strings"] = strings,
            ["app"] = new JsonObject { ["name"] = p.App.Name, ["publisher"] = p.App.Publisher, ["version"] = p.App.Version, ["iconPath"] = iconPath },
            ["ui"] = new JsonObject
            {
                ["theme"] = p.Ui.Theme, ["backdrop"] = p.Ui.Backdrop, ["useSystemAccent"] = p.Ui.UseSystemAccent, ["accentColor"] = p.Ui.AccentColor,
                ["title"] = p.Ui.Title, ["welcomeText"] = p.Ui.WelcomeText, ["finishText"] = p.Ui.FinishText,
                ["licenseText"] = uninstall || scenario == "upgrade" ? "" : license,
            },
            ["options"] = new JsonObject
            {
                ["installDir"] = machine ? machineDir : userDir,
                ["scope"] = machine ? "machine" : "user",
                ["canChangeDir"] = p.Install.AllowCustomDir && scenario == "install",
                ["canChangeScope"] = p.Install.Scope == "userOrMachine" && scenario == "install",
                ["userDir"] = userDir,
                ["machineDir"] = machineDir,
                ["dirLeaf"] = p.App.Name,
                ["dirOk"] = true,
                ["desktopShortcut"] = Offer(p.Install.DesktopShortcut),
                ["startMenuShortcut"] = Offer(p.Install.StartMenuShortcut),
                ["launchAfter"] = Offer(p.Install.LaunchAfterInstall),
            },
            ["existing"] = new JsonObject
            {
                ["relation"] = scenario == "upgrade" ? "upgrade" : "none",
                ["version"] = scenario == "upgrade" ? PreviousVersion(p.App.Version) : "",
                ["blocked"] = false,
            },
            ["requiredBytes"] = payloadBytes,
            ["freeBytes"] = 0,
        };
        var helloPath = Path.Combine(temp, "hello.json");
        File.WriteAllText(helloPath, hello.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--preview", helloPath }, WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false })?.Dispose();
    }

    private static JsonObject LoadStrings(string path)
    {
        if (!File.Exists(path)) return [];
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
    }

    private static string PreviousVersion(string version)
    {
        var (a, b, c, _) = PeResources.NumericVersion(version);
        if (c > 0) return $"{a}.{b}.{c - 1}";
        if (b > 0) return $"{a}.{b - 1}.0";
        return a > 0 ? $"{a - 1}.0.0" : "0.9.0";
    }
}
