using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentSetup.Builder.Core.Model;
using Json.Schema;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>
/// shared/schemas/fluentsetup.project.schema.json against what the builder writes and reads: the serializer writes
/// every property (null for unset sections, "" for unset text), and $(Name) placeholders may stand in any string.
/// </summary>
public class SchemaTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SchemaPath = Path.Combine(RepoRoot, "shared", "schemas", "fluentsetup.project.schema.json");
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(SchemaPath)));
    private static readonly Lazy<JsonObject> SchemaNode = new(() => JsonNode.Parse(File.ReadAllText(SchemaPath))!.AsObject());

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "shared", "schemas", "fluentsetup.project.schema.json"))) return dir.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>Validation errors as "instance location: message" lines (empty when valid).</summary>
    private static List<string> Errors(string json)
    {
        var result = Schema.Value.Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical, RequireFormatValidation = true });
        var errors = new List<string>();
        // Only down failing branches: a failed "if" or the other alternatives of a passing anyOf are not errors.
        void Collect(EvaluationResults r)
        {
            if (r.IsValid) return;
            if (r.Errors is { } e) errors.AddRange(e.Select(x => $"{r.InstanceLocation} ({r.EvaluationPath}): {x.Value}"));
            foreach (var d in r.Details) Collect(d);
        }
        Collect(result);
        return errors;
    }

    private static void AssertValid(string json)
    {
        var errors = Errors(json);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public async Task InitOutputIsValid()
    {
        using var dir = new TempDir();
        dir.File("app/App.exe", "x");
        var project = Path.Combine(dir.Path, "installer");
        Assert.Equal(0, await global::FluentSetup.Cli.Program.Main(["init", project, "--source", Path.Combine(dir.Path, "app"),
            "--name", "Contoso Notes", "--publisher", "Contoso", "--version", "1.2.3", "--main-exe", "App.exe"]));
        AssertValid(File.ReadAllText(Path.Combine(project, "fluentsetup.json")));
    }

    [Fact]
    public void DefaultProjectIsValid()
    {
        var p = new SetupProject { App = new AppInfo { Id = "Contoso.Notes", Name = "Notes", Version = "1.0.0" }, Files = new FileSettings { Source = "bin" } };
        AssertValid(ProjectSerializer.Serialize(p));
    }

    [Fact]
    public void FullyPopulatedModelIsValid()
    {
        var p = new SetupProject { App = new AppInfo { Id = "Contoso.Notes", Name = "Notes", Version = "1.0.0", MainExecutable = "Notes.exe" }, Files = new FileSettings { Source = "bin" } };
        Populate(p, 0);
        p.Options[0].Label["en-US"] = "Option";
        p.Inputs[0].Label["en-US"] = "Server";
        p.Associations[0].Extensions.Add(".note");
        p.Uninstall.Elevated.ServiceAccess[0].Rights.Add("START");
        p.Signing.Command!.Arguments.Add("{file}");
        // Items the builder only accepts with an id or name: what a real project has.
        var node = JsonNode.Parse(ProjectSerializer.Serialize(p))!.AsObject();
        FillRequired(node, SchemaNode.Value, SchemaNode.Value);
        AssertValid(node.ToJsonString());
    }

    [Theory]
    [InlineData("samples/DemoApp/fluentsetup.json")]
    [InlineData("packaging/fluentsetup.json")]
    public void RepositoryProjectsAreValid(string path)
    {
        AssertValid(File.ReadAllText(Path.Combine(RepoRoot, path)));
        // And as the builder saves them again.
        AssertValid(ProjectSerializer.Serialize(ProjectSerializer.Load(Path.Combine(RepoRoot, path))));
    }

    [Fact]
    public void PlaceholdersAreAccepted()
    {
        var p = new SetupProject
        {
            Variables = { ["Version"] = "1.2.0", ["Site"] = "https://contoso.com", ["Exe"] = "Notes.exe", ["Hash"] = new string('a', 64) },
            App = new AppInfo { Id = "$(Id)", Name = "Notes", Version = "$(Version)", MainExecutable = "$(Exe)", Website = "$(Site)/notes", Icon = "$(Icon)" },
            Files = new FileSettings { Source = "$(PublishDir)" },
            Output = new OutputSettings { FileName = "$(Name)-$(Version).exe" },
        };
        p.Requirements.WebView2.Required = true;
        p.Requirements.WebView2.HelpUrl = "$(Site)/webview2";
        p.Requirements.WebView2.Download = new DownloadSettings { Url = "$(Site)/wv2.exe", Sha256 = "$(Hash)" };
        p.Requirements.NetFramework = "$(NetFx)";
        p.Requirements.DotNet.Add(new DotNetRequirement { Version = "$(DotNet)", Installer = @"$(Redist)\dotnet.exe" });
        p.Requirements.WindowsAppRuntime.Version = "$(WinAppSdk)";
        AssertValid(ProjectSerializer.Serialize(p));

        // A placeholder does not make every string valid: without "$(" the patterns still apply.
        p.App.Version = "one";
        Assert.NotEmpty(Errors(ProjectSerializer.Serialize(p)));
    }

    [Fact]
    public void NullForOptionalSectionsAndEmptyForUnsetText()
    {
        var p = new SetupProject { App = new AppInfo { Id = "Contoso.Notes", Name = "Notes", Version = "1.0.0" }, Files = new FileSettings { Source = "bin" } };
        var json = ProjectSerializer.Serialize(p);
        Assert.Contains("\"closeCommand\": null", json, StringComparison.Ordinal);
        AssertValid(json);
    }

    /// <summary>Every property the serializer writes has a schema entry, and nullable ones accept null.</summary>
    [Fact]
    public void EveryModelPropertyIsInTheSchema()
    {
        var missing = new List<string>();
        CheckType(typeof(SetupProject), SchemaNode.Value, SchemaNode.Value, "", missing, []);
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static readonly NullabilityInfoContext Nullability = new();

    private static IEnumerable<(PropertyInfo Property, string Name)> JsonProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 &&
                        !p.IsDefined(typeof(JsonExtensionDataAttribute)) && !p.IsDefined(typeof(JsonIgnoreAttribute)))
            .Select(p => (p, p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name)));

    private static bool IsModel(Type t) => t.Namespace == typeof(SetupProject).Namespace && t.IsClass && t != typeof(string);

    private static Type? ElementType(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) ? t.GetGenericArguments()[0] : null;

    private static Type? DictionaryValueType(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>) ? t.GetGenericArguments()[1] : null;

    /// <summary>The schema itself and every schema it refers to or combines (allOf/anyOf/oneOf, if/then/else).</summary>
    private static IEnumerable<JsonObject> Alternatives(JsonNode? schema, JsonObject root, bool conditional = true, HashSet<JsonObject>? seen = null)
    {
        seen ??= new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        if (schema is not JsonObject o || !seen.Add(o)) yield break;
        yield return o;
        if (o["$ref"]?.GetValue<string>() is { } r && r.StartsWith("#/$defs/", StringComparison.Ordinal))
        {
            foreach (var x in Alternatives(root["$defs"]![r["#/$defs/".Length..]], root, conditional, seen)) yield return x;
        }
        foreach (var key in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (o[key] is JsonArray a) foreach (var item in a) foreach (var x in Alternatives(item, root, conditional, seen)) yield return x;
        }
        if (!conditional) yield break;
        foreach (var key in new[] { "then", "else" })
        {
            foreach (var x in Alternatives(o[key], root, conditional, seen)) yield return x;
        }
    }

    /// <summary>Every schema of the property (a "name": true entry only allows what another alternative defines).</summary>
    private static List<JsonObject> PropertySchemas(JsonObject schema, string name, JsonObject root, bool conditional = true) =>
        [.. Alternatives(schema, root, conditional).Select(s => s["properties"]?[name]).OfType<JsonObject>()];

    private static bool AcceptsNull(JsonNode schema, JsonObject root) =>
        Alternatives(schema, root).Any(s => s["type"] switch
        {
            JsonValue v => v.GetValue<string>() == "null",
            JsonArray a => a.Any(t => t?.GetValue<string>() == "null"),
            _ => false,
        });

    /// <param name="schema">The type's schema with its conditional parts (if/then/else).</param>
    /// <param name="plain">The same without them: what applies whatever the other values are.</param>
    private static void CheckType(Type type, JsonObject schema, JsonObject plain, string path, List<string> missing, HashSet<Type> visiting)
    {
        if (!visiting.Add(type)) return;
        var root = SchemaNode.Value;
        foreach (var (property, name) in JsonProperties(type))
        {
            var where = path.Length == 0 ? name : $"{path}.{name}";
            var schemas = PropertySchemas(schema, name, root);
            if (schemas.Count == 0)
            {
                missing.Add($"{where} ({type.Name}.{property.Name}) is not in the schema");
                continue;
            }
            var plainSchemas = PropertySchemas(plain, name, root, conditional: false);
            var nullable = Nullability.Create(property).WriteState == NullabilityState.Nullable || Nullable.GetUnderlyingType(property.PropertyType) is not null;
            // "if provider is command, then command is an object" is right: only the unconditional schemas must accept null.
            if (nullable && !plainSchemas.All(ps => AcceptsNull(ps, root))) missing.Add($"{where} is nullable in the model but the schema rejects null");
            var t = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var resolved = schemas.SelectMany(ps => Alternatives(ps, root)).ToList();
            var resolvedPlain = plainSchemas.SelectMany(ps => Alternatives(ps, root, conditional: false)).ToList();
            if (IsModel(t))
            {
                CheckType(t, ObjectSchema(resolved), ObjectSchema(resolvedPlain), where, missing, visiting);
            }
            else if ((ElementType(t) ?? DictionaryValueType(t)) is { } inner && IsModel(inner))
            {
                var key = ElementType(t) is not null ? "items" : "additionalProperties";
                var items = resolved.Select(s => s[key]).OfType<JsonObject>().ToList();
                if (items.Count == 0)
                {
                    missing.Add($"{where} has no {key} schema");
                    continue;
                }
                var itemsPlain = resolvedPlain.Select(s => s[key]).OfType<JsonObject>();
                CheckType(inner, ObjectSchema([.. items.SelectMany(x => Alternatives(x, root))]),
                          ObjectSchema([.. itemsPlain.SelectMany(x => Alternatives(x, root, conditional: false))]), where + "[]", missing, visiting);
            }
        }
        visiting.Remove(type);
    }

    /// <summary>A synthetic schema made of all alternatives (for walking nested types).</summary>
    private static JsonObject ObjectSchema(List<JsonObject> alternatives) =>
        new() { ["allOf"] = new JsonArray([.. alternatives.Select(a => (JsonNode)a.DeepClone())]) };

    /// <summary>Sets every null section to a new instance and gives every list of sections one default element.</summary>
    private static void Populate(object node, int depth)
    {
        if (depth > 6) return;
        foreach (var (property, _) in JsonProperties(node.GetType()))
        {
            var t = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var value = property.GetValue(node);
            if (value is null && IsModel(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            {
                value = Activator.CreateInstance(t)!;
                property.SetValue(node, value);
            }
            if (value is null) continue;
            if (IsModel(t))
            {
                Populate(value, depth + 1);
            }
            else if (ElementType(t) is { } inner && IsModel(inner) && value is IList list)
            {
                if (list.Count == 0) list.Add(Activator.CreateInstance(inner));
                foreach (var item in list) if (item is not null) Populate(item, depth + 1);
            }
        }
    }

    /// <summary>
    /// Gives required properties that are still empty a value their schema accepts (an id, a name...): a default
    /// list element has none, as a real project always has.
    /// </summary>
    private static void FillRequired(JsonNode? node, JsonNode? schema, JsonObject root)
    {
        if (schema is null || node is null) return;
        var alternatives = Alternatives(schema, root).ToList();
        if (node is JsonObject obj)
        {
            foreach (var s in alternatives)
            {
                if (s["required"] is JsonArray req)
                {
                    foreach (var r in req.Select(x => x!.GetValue<string>()))
                    {
                        if (obj[r] is JsonValue v && v.TryGetValue<string>(out var str) && str.Length == 0) obj[r] = SampleFor(r);
                    }
                }
            }
            foreach (var (k, v) in obj.ToList())
            {
                foreach (var ps in alternatives.Select(s => s["properties"]?[k]).OfType<JsonObject>()) FillRequired(v, ps, root);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var items in alternatives.Select(s => s["items"]).OfType<JsonObject>())
            {
                foreach (var item in arr) FillRequired(item, items, root);
            }
        }
    }

    private static string SampleFor(string property) => property switch
    {
        "url" or "endpoint" => "https://contoso.com/x",
        "version" => "1.0.0",
        "progId" => "Contoso.Notes",
        "scheme" => "contoso-notes",
        "key" => @"Software\Contoso",
        "path" => @"{ProgramFiles}\Contoso\x.dll",
        "run" or "target" or "program" => "Notes.exe",
        "accentColor" => "#0067C0",
        _ => "x1",
    };
}
