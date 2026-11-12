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
        NormalizeNulls(project, new System.Reflection.NullabilityInfoContext());
        return project;
    }

    /// <summary>
    /// A JSON <c>null</c> in a field the model declares non-nullable (text, list, table, section) becomes its empty
    /// value, so validation reports what is missing instead of failing; optional sections keep their null.
    /// </summary>
    private static void NormalizeNulls(object node, System.Reflection.NullabilityInfoContext nullability)
    {
        foreach (var property in node.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0) continue;
            if (Attribute.IsDefined(property, typeof(JsonExtensionDataAttribute))) continue;
            var type = property.PropertyType;
            var value = property.GetValue(node);
            if (value is null)
            {
                if (nullability.Create(property).WriteState == System.Reflection.NullabilityState.Nullable || Nullable.GetUnderlyingType(type) is not null) continue;
                value = type == typeof(string) ? "" : type.GetConstructor(Type.EmptyTypes) is not null ? Activator.CreateInstance(type) : null;
                if (value is null) continue;
                property.SetValue(node, value);
            }
            NormalizeValue(value, nullability);
        }
    }

    private static void NormalizeValue(object value, System.Reflection.NullabilityInfoContext nullability)
    {
        switch (value)
        {
            case string:
                return;
            case System.Collections.IList list:
                var elementType = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : typeof(object);
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] is { } item) NormalizeValue(item, nullability);
                    else if (elementType == typeof(string)) list[i] = "";
                    else list.RemoveAt(i);  // "options": [null] is no option
                }
                return;
            case System.Collections.IDictionary table:
                var valueType = table.GetType().IsGenericType ? table.GetType().GetGenericArguments()[1] : typeof(object);
                foreach (var key in table.Keys.Cast<object>().ToList())
                {
                    if (table[key] is { } item) NormalizeValue(item, nullability);
                    else if (valueType == typeof(string)) table[key] = "";
                    else if (valueType.GetConstructor(Type.EmptyTypes) is not null) table[key] = Activator.CreateInstance(valueType);
                }
                return;
            default:
                if (value.GetType().Namespace == typeof(SetupProject).Namespace) NormalizeNulls(value, nullability);
                return;
        }
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
