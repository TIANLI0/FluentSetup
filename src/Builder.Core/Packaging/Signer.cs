using System.Diagnostics;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Validation;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>Authenticode signing through the Windows SDK's signtool.exe.</summary>
public static class Signer
{
    public static string? FindSignTool(string? configured)
    {
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
        var roots = new List<string>();
        foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            var pf = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(pf)) roots.Add(Path.Combine(pf, "Windows Kits", "10", "bin"));
        }
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots");
            if (key?.GetValue("KitsRoot10") is string kits) roots.Insert(0, Path.Combine(kits, "bin"));
        }
        catch (System.Security.SecurityException) { }
        var hostArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        foreach (var root in roots.Where(Directory.Exists))
        {
            var candidate = Directory.EnumerateDirectories(root, "10.*")
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version())
                .Select(d => Path.Combine(d, hostArch, "signtool.exe"))
                .FirstOrDefault(File.Exists);
            if (candidate is not null) return candidate;
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(';').Select(p => Path.Combine(p.Trim(), "signtool.exe")).FirstOrDefault(File.Exists);
    }

    public static void Sign(SigningSettings settings, string projectDir, string file, string description, Action<string> log)
    {
        var tool = FindSignTool(string.IsNullOrEmpty(settings.SignToolPath) ? null : ProjectValidator.Resolve(projectDir, settings.SignToolPath))
                   ?? throw new BuildFailedException("signtool.exe was not found. Install the Windows SDK or set signing.signToolPath.");
        var args = new List<string> { "sign", "/fd", "SHA256", "/d", description };
        if (!string.IsNullOrEmpty(settings.TimestampUrl)) args.AddRange(["/tr", settings.TimestampUrl, "/td", "SHA256"]);
        if (!string.IsNullOrEmpty(settings.CertificateThumbprint))
        {
            args.AddRange(["/sha1", settings.CertificateThumbprint.Replace(" ", "", StringComparison.Ordinal)]);
        }
        else
        {
            args.AddRange(["/f", ProjectValidator.Resolve(projectDir, settings.CertificateFile)]);
            if (!string.IsNullOrEmpty(settings.PasswordEnvironmentVariable))
            {
                var password = Environment.GetEnvironmentVariable(settings.PasswordEnvironmentVariable)
                               ?? throw new BuildFailedException($"Environment variable {settings.PasswordEnvironmentVariable} (certificate password) is not set.");
                args.AddRange(["/p", password]);
            }
        }
        args.Add(file);
        var (code, output) = Run(tool, args);
        // Never log the password.
        var passwordIndex = args.IndexOf("/p") + 1;
        log("signtool " + string.Join(' ', args.Select((a, i) => passwordIndex > 0 && i == passwordIndex ? "***" : a)));
        if (code != 0) throw new BuildFailedException($"Signing failed (signtool exit code {code}): {output.Trim()}");
        log(output.Trim());
    }

    public static (int ExitCode, string Output) Verify(string file, string? signTool = null)
    {
        var tool = FindSignTool(signTool) ?? throw new FileNotFoundException("signtool.exe not found");
        return Run(tool, ["verify", "/pa", "/all", file]);
    }

    private static (int, string) Run(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new BuildFailedException("Could not start signtool.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
