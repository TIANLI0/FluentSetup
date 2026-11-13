namespace FluentSetup.Builder.Core.Packaging;

/// <summary>
/// Relative-path rules for files inside the install directory. Mirrors fsetup::path::ValidateRelative in the native
/// engine (src/Common/path.cpp) - the engine re-validates everything, this copy only gives authors early, precise errors.
/// </summary>
public static class PathRules
{
    public const int MaxRelativePath = 1024;
    public const int MaxComponent = 255;
    private static readonly string[] ReservedNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"];

    /// <summary>Characters Windows does not allow in file names (what Path.GetInvalidFileNameChars returns on Windows), on every OS.</summary>
    public static readonly char[] InvalidFileNameChars =
        [.. Enumerable.Range(0, 32).Select(i => (char)i), '"', '<', '>', '|', ':', '*', '?', '\\', '/'];

    /// <summary>
    /// How paths on the build machine compare: case-insensitively on Windows and macOS (their usual file systems),
    /// exactly on Linux. Install paths (inside the package) always compare case-insensitively, as on Windows.
    /// </summary>
    public static StringComparison HostPathComparison { get; } = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Rooted in the Windows sense on every OS: "\x", "/x", "C:x", "C:\x", UNC paths.</summary>
    public static bool IsRooted(string path) => path.StartsWith('\\') || path.StartsWith('/') || (path.Length >= 2 && path[1] == ':');

    /// <summary>
    /// A symbolic link, junction or other reparse point (never followed when collecting files). .NET reports symbolic
    /// links on Linux and macOS as reparse points too; <see cref="FileSystemInfo.LinkTarget"/> (a cheap readlink there)
    /// is asked as well.
    /// </summary>
    public static bool IsLink(FileSystemInfo entry) =>
        entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || (!OperatingSystem.IsWindows() && entry.LinkTarget is not null);

    public static bool IsReservedDeviceName(string component)
    {
        var dot = component.IndexOf('.');
        var baseName = (dot >= 0 ? component[..dot] : component).TrimEnd(' ');
        if (ReservedNames.Any(n => string.Equals(n, baseName, StringComparison.OrdinalIgnoreCase))) return true;
        if (baseName.Length == 4 &&
            (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            var d = baseName[3];
            return d is >= '0' and <= '9' or '¹' or '²' or '³';
        }
        return false;
    }

    public static bool IsValidComponent(string c, out string? reason)
    {
        reason = null;
        if (c.Length == 0) { reason = "empty path component"; return false; }
        if (c.Length > MaxComponent) { reason = "path component longer than 255 characters"; return false; }
        if (c is "." or "..") { reason = "'.' and '..' are not allowed"; return false; }
        foreach (var ch in c)
        {
            if (ch < 0x20 || "<>:\"/\\|?*".Contains(ch)) { reason = $"character '{(ch < 0x20 ? $"\\x{(int)ch:x2}" : ch.ToString())}' is not allowed"; return false; }
        }
        if (c.EndsWith('.') || c.EndsWith(' ')) { reason = "names may not end with a dot or space"; return false; }
        if (c.StartsWith(' ')) { reason = "names may not start with a space"; return false; }
        if (IsReservedDeviceName(c)) { reason = $"'{c}' is a reserved Windows device name"; return false; }
        return true;
    }

    /// <summary>Validates and normalises (backslashes) a relative install path.</summary>
    public static bool TryNormalizeRelative(string rel, out string normalized, out string? reason)
    {
        normalized = "";
        reason = null;
        if (string.IsNullOrEmpty(rel)) { reason = "empty path"; return false; }
        if (rel.Length > MaxRelativePath) { reason = "path longer than 1024 characters"; return false; }
        var work = rel.Replace('/', '\\');
        if (work.StartsWith('\\')) { reason = "absolute or UNC path"; return false; }
        if (work.Length >= 2 && work[1] == ':') { reason = "drive-qualified path"; return false; }
        var parts = work.Split('\\');
        if (parts.Length > 64) { reason = "nested too deeply"; return false; }
        foreach (var p in parts)
        {
            if (!IsValidComponent(p, out reason)) return false;
        }
        normalized = string.Join('\\', parts);
        return true;
    }

    /// <summary>Names the installer itself owns inside the install directory.</summary>
    public static bool IsInstallerReserved(string normalizedRel)
    {
        var first = normalizedRel.Split('\\')[0];
        return string.Equals(normalizedRel, "uninstall.exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedRel, "uninstall.json", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(first, ".fsetup", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidAppId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') &&
        !id.StartsWith('.') && !id.EndsWith('.');
}
