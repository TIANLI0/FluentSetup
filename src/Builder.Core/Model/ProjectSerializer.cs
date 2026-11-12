using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentSetup.Builder.Core.Model;

public static class ProjectSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static SetupProject Load(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    public static SetupProject Parse(string json)
    {
        var project = JsonSerializer.Deserialize<SetupProject>(json, Options)
            ?? throw new InvalidDataException("The project file is empty.");
        if (project.SchemaVersion > SetupProject.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"This project uses schema version {project.SchemaVersion}; this FluentSetup supports up to {SetupProject.CurrentSchemaVersion}.");
        }
        project.SchemaVersion = SetupProject.CurrentSchemaVersion;
        return project;
    }

    public static string Serialize(SetupProject project) => JsonSerializer.Serialize(project, Options);

    /// <summary>Writes atomically (temp file + replace) so a crash never leaves a truncated project.</summary>
    public static void Save(SetupProject project, string path)
    {
        var full = Path.GetFullPath(path);
        var temp = full + ".tmp";
        File.WriteAllText(temp, Serialize(project) + Environment.NewLine);
        File.Move(temp, full, overwrite: true);
    }

    public static SetupProject Clone(SetupProject project) => Parse(Serialize(project));
}
