using System.Text.Json;
using FluentSetup.Builder.Core.Packaging;

namespace FluentSetup.Builder.Services;

/// <summary>Builder preferences and recent projects (%LocalAppData%\FluentSetup\Builder\settings.json).</summary>
public sealed class AppSettings
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentSetup", "Builder");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<string> RecentProjects { get; set; } = [];
    /// <summary>system | light | dark</summary>
    public string Theme { get; set; } = "system";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void AddRecent(string path)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 10) RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
        Save();
    }
}

/// <summary>One line per build in %LocalAppData%\FluentSetup\Builder\history\&lt;appId&gt;.jsonl.</summary>
public static class BuildHistoryStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string FileFor(string appId)
    {
        var safe = string.Concat((string.IsNullOrWhiteSpace(appId) ? "unnamed" : appId).Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(AppSettings.Folder, "history", safe + ".jsonl");
    }

    public static void Append(BuildReport report)
    {
        try
        {
            var file = FileFor(report.AppId);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, JsonSerializer.Serialize(report, Json) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static List<BuildReport> Load(string appId)
    {
        var list = new List<BuildReport>();
        var file = FileFor(appId);
        if (!File.Exists(file)) return list;
        foreach (var line in File.ReadLines(file))
        {
            try
            {
                if (JsonSerializer.Deserialize<BuildReport>(line, Json) is { } r) list.Add(r);
            }
            catch (JsonException)
            {
            }
        }
        list.Reverse();
        return list;
    }
}
