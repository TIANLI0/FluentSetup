using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentSetup.Builder.Core.Validation;

namespace FluentSetup.Builder.Core.Model;

/// <summary>
/// $(Name) placeholders in project strings. Values come from the project's "variables" and, with priority, from the
/// command line (<c>fluentsetup build -D Version=1.2.1</c>), so a CI release build can stamp the version (or any other
/// value) without rewriting the project file. "$$(" writes a literal "$(".
/// </summary>
public static partial class ProjectVariables
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"\$\$\(|\$\(([^)]*)\)")]
    private static partial Regex PlaceholderRegex();

    public static bool IsValidName(string name) => NameRegex().IsMatch(name);

    /// <summary>Parses "NAME=VALUE" (a -D argument).</summary>
    public static bool TryParseDefine(string text, out string name, out string value)
    {
        var eq = text.IndexOf('=');
        name = eq > 0 ? text[..eq].Trim() : "";
        value = eq > 0 ? text[(eq + 1)..] : "";
        return IsValidName(name);
    }

    /// <summary>True if any string of the project uses a placeholder (the raw project then differs from what is built).</summary>
    public static bool UsesPlaceholders(SetupProject project) =>
        PlaceholderRegex().IsMatch(JsonSerializer.Serialize(project, ProjectSerializer.Options));

    /// <summary>
    /// A copy of the project with every $(Name) replaced. Unknown names and invalid variable names are reported as errors
    /// (FS1801, FS1802); their placeholders stay as written so the validator then points at the field.
    /// </summary>
    public static SetupProject Expand(SetupProject project, IReadOnlyDictionary<string, string>? overrides, DiagnosticBag? diagnostics = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in project.Variables)
        {
            if (IsValidName(k)) values[k] = v;
            else diagnostics?.Error("FS1802", $"variables.{k}", $"'{k}' is not a valid variable name (letters, digits, _, . and -, starting with a letter or _).");
        }
        if (overrides is not null)
        {
            foreach (var (k, v) in overrides) values[k] = v;
        }
        var node = JsonSerializer.SerializeToNode(project, ProjectSerializer.Options)!;
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(node, "", values, diagnostics, reported);
        var expanded = node.Deserialize<SetupProject>(ProjectSerializer.Options)!;
        expanded.Variables = project.Variables;  // the table itself is not expanded
        return expanded;
    }

    /// <summary>Replaces placeholders in place; returns the new value for a string that changed, else null.</summary>
    private static JsonNode? Walk(JsonNode node, string path, Dictionary<string, string> values, DiagnosticBag? d, HashSet<string> reported)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (path.Length == 0 && key is "variables" or "$schema") continue;
                    if (obj[key] is { } child && Walk(child, path.Length == 0 ? key : $"{path}.{key}", values, d, reported) is { } replaced) obj[key] = replaced;
                }
                return null;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is { } child && Walk(child, $"{path}[{i}]", values, d, reported) is { } replaced) arr[i] = replaced;
                }
                return null;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Contains("$("):
                var result = Replace(s, path, values, d, reported);
                return result == s ? null : JsonValue.Create(result);
            default:
                return null;
        }
    }

    private static string Replace(string s, string field, Dictionary<string, string> values, DiagnosticBag? d, HashSet<string> reported) =>
        PlaceholderRegex().Replace(s, m =>
        {
            if (m.Value == "$$(") return "$(";
            var name = m.Groups[1].Value.Trim();
            if (values.TryGetValue(name, out var value)) return value;
            if (reported.Add(field + "|" + name))
                d?.Error("FS1801", field, $"Unknown variable $({name}). Define it in \"variables\" or with -D {name}=value.");
            return m.Value;
        });

    /// <summary>Human-readable list for logs: "Version=1.2.0, Channel=stable".</summary>
    public static string Describe(IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in values) sb.Append(sb.Length == 0 ? "" : ", ").Append(k).Append('=').Append(v);
        return sb.ToString();
    }
}
