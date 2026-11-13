using System.Text.Json;
using FluentSetup.Builder.Core.Model;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>
/// Writes the 1.2 sections of the package header (shortcuts, associations, protocols, registry, environment, services,
/// firewall, inputs, configFiles) with defaults resolved, as parsed by src/Install.Core/extensions.cpp.
/// </summary>
internal static class IntegrationWriter
{
    private static string Win(string path) => path.Replace('/', '\\');

    public static void Write(Utf8JsonWriter w, SetupProject p, Localization.Texts texts)
    {
        _ = texts;
        var main = Win(p.App.MainExecutable);

        w.WriteStartArray("shortcuts");
        foreach (var s in p.Shortcuts)
        {
            w.WriteStartObject();
            w.WriteString("name", s.Name);
            w.WriteString("target", s.Target.Length > 0 ? Win(s.Target) : main);
            Strings(w, "args", s.Arguments);
            if (s.Description.Length > 0) w.WriteString("description", s.Description);
            if (s.Icon.Length > 0) w.WriteString("icon", Win(s.Icon));
            if (s.IconIndex != 0) w.WriteNumber("iconIndex", s.IconIndex);
            if (s.WorkingDirectory.Length > 0) w.WriteString("workingDir", Win(s.WorkingDirectory));
            w.WriteString("location", s.Location);
            var folder = s.Location == "startMenu" ? (s.Folder.Length > 0 ? s.Folder : p.Install.StartMenuFolder) : "";
            if (folder.Length > 0) w.WriteString("folder", folder);
            if (s.IfOption.Length > 0) w.WriteString("ifOption", s.IfOption);
            if (s.AppUserModelId.Length > 0) w.WriteString("appUserModelId", s.AppUserModelId);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("associations");
        foreach (var a in p.Associations)
        {
            w.WriteStartObject();
            Strings(w, "extensions", a.Extensions.Select(x => x.ToLowerInvariant()));
            w.WriteString("progId", a.ProgId);
            w.WriteString("description", a.Description);
            if (a.Icon.Length > 0) w.WriteString("icon", Win(a.Icon));
            if (a.IconIndex != 0) w.WriteNumber("iconIndex", a.IconIndex);
            w.WriteString("target", a.Target.Length > 0 ? Win(a.Target) : main);
            Strings(w, "args", a.Arguments);
            if (a.ContentType.Length > 0) w.WriteString("contentType", a.ContentType);
            if (a.IfOption.Length > 0) w.WriteString("ifOption", a.IfOption);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("protocols");
        foreach (var pr in p.Protocols)
        {
            w.WriteStartObject();
            w.WriteString("scheme", pr.Scheme);
            w.WriteString("description", pr.Description);
            if (pr.Icon.Length > 0) w.WriteString("icon", Win(pr.Icon));
            if (pr.IconIndex != 0) w.WriteNumber("iconIndex", pr.IconIndex);
            w.WriteString("target", pr.Target.Length > 0 ? Win(pr.Target) : main);
            Strings(w, "args", pr.Arguments);
            if (pr.IfOption.Length > 0) w.WriteString("ifOption", pr.IfOption);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("registry");
        foreach (var r in p.Registry)
        {
            w.WriteStartObject();
            w.WriteString("key", Win(r.Key).Trim('\\'));
            w.WriteStartArray("values");
            foreach (var v in r.Values)
            {
                w.WriteStartObject();
                w.WriteString("name", v.Name);
                w.WriteString("type", v.Type);
                w.WriteString("data", v.Data);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("uninstall", r.Uninstall);
            if (r.IfOption.Length > 0) w.WriteString("ifOption", r.IfOption);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("environment");
        foreach (var e in p.Environment)
        {
            w.WriteStartObject();
            w.WriteString("name", e.Name);
            w.WriteString("value", e.Value);
            if (e.Expand) w.WriteBoolean("expand", true);
            if (e.IfOption.Length > 0) w.WriteString("ifOption", e.IfOption);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("services");
        foreach (var s in p.Services)
        {
            w.WriteStartObject();
            w.WriteString("name", s.Name);
            w.WriteString("displayName", s.DisplayName);
            w.WriteString("description", s.Description);
            w.WriteString("run", Win(s.Run));
            Strings(w, "args", s.Arguments);
            w.WriteString("startType", s.StartType);
            w.WriteString("account", s.Account);
            w.WriteBoolean("start", s.Start);
            Strings(w, "dependencies", s.Dependencies);
            w.WriteBoolean("restartOnFailure", s.RestartOnFailure);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("firewall");
        foreach (var f in p.Firewall)
        {
            w.WriteStartObject();
            w.WriteString("name", f.Name);
            w.WriteString("program", f.Program.Length > 0 ? Win(f.Program) : main);
            w.WriteString("direction", f.Direction);
            w.WriteString("protocol", f.Protocol);
            if (f.LocalPorts.Length > 0) w.WriteString("localPorts", f.LocalPorts);
            Strings(w, "profiles", f.Profiles);
            if (f.IfOption.Length > 0) w.WriteString("ifOption", f.IfOption);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("inputs");
        foreach (var i in p.Inputs)
        {
            w.WriteStartObject();
            w.WriteString("id", i.Id);
            Localized(w, "label", i.Label);
            Localized(w, "description", i.Description);
            w.WriteString("type", i.Type);
            w.WriteString("default", i.Default);
            w.WriteBoolean("required", i.Required);
            if (i.Pattern.Length > 0) w.WriteString("pattern", i.Pattern);
            w.WriteBoolean("remember", i.Remember && i.Type != "password");
            w.WriteStartArray("choices");
            foreach (var c in i.Choices)
            {
                w.WriteStartObject();
                w.WriteString("value", c.Value);
                Localized(w, "label", c.Label);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("configFiles");
        foreach (var c in p.ConfigFiles)
        {
            w.WriteStartObject();
            w.WriteString("path", Win(c.Path));
            w.WriteString("format", c.Format);
            w.WriteStartArray("values");
            foreach (var v in c.Values)
            {
                w.WriteStartObject();
                w.WriteString("key", v.Key);
                if (v.Section.Length > 0) w.WriteString("section", v.Section);
                w.WriteString("value", v.Value);
                w.WriteString("type", v.Type);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (c.IfOption.Length > 0) w.WriteString("ifOption", c.IfOption);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    private static void Localized(Utf8JsonWriter w, string name, Dictionary<string, string> text)
    {
        w.WriteStartObject(name);
        foreach (var (lang, s) in text) w.WriteString(lang, s);
        w.WriteEndObject();
    }
}
