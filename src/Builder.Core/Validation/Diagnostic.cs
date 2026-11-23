using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace FluentSetup.Builder.Core.Validation;

public enum Severity { Info, Warning, Error }

/// <param name="Code">Stable identifier, e.g. FS1001 (documented in docs/diagnostics.md).</param>
/// <param name="Field">The project property the message is about, e.g. "app.version" (for UI navigation).</param>
public sealed record Diagnostic(Severity Severity, string Code, string Field, string Message)
{
    /// <summary>
    /// The English message as a composite format string ("Icon file not found: {0}") with <see cref="Arguments"/> as its
    /// values, so a user interface can show a translation (looked up by <see cref="DiagnosticMessage.Key"/>). Null when
    /// the message was not written as one (it is then shown as is).
    /// </summary>
    [JsonIgnore] public string? Template { get; init; }

    /// <summary>The values of the template's placeholders, already formatted as in <see cref="Message"/>.</summary>
    [JsonIgnore] public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>A diagnostic whose message keeps its template (an interpolated string or a plain one).</summary>
    public static Diagnostic Create(Severity severity, string code, string field, DiagnosticMessage message) =>
        new(severity, code, field, message.Text) { Template = message.Template, Arguments = message.Arguments };

    // The template only describes the message: two diagnostics with the same text are the same.
    public bool Equals(Diagnostic? other) =>
        other is not null && Severity == other.Severity && Code == other.Code && Field == other.Field && Message == other.Message;

    public override int GetHashCode() => HashCode.Combine(Severity, Code, Field, Message);

    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()} {Code} [{Field}]: {Message}";
}

/// <summary>
/// A diagnostic message written as an interpolated string: keeps the English text (what the CLI prints) and the same
/// text as a format template with its values, for translated user interfaces.
/// </summary>
[InterpolatedStringHandler]
public struct DiagnosticMessage
{
    private StringBuilder? _text;
    private StringBuilder? _template;
    private List<string>? _arguments;
    private string? _plain;

    public DiagnosticMessage(int literalLength, int formattedCount)
    {
        _text = new StringBuilder(literalLength + formattedCount * 16);
        _template = new StringBuilder(literalLength + formattedCount * 4);
        _arguments = new List<string>(formattedCount);
    }

    private DiagnosticMessage(string plain) => _plain = plain;

    public static implicit operator DiagnosticMessage(string? text) => new(text ?? "");

    public void AppendLiteral(string value)
    {
        _text!.Append(value);
        _template!.Append(EscapeBraces(value));
    }

    public void AppendFormatted<T>(T value) => AppendFormatted(value, 0, null);

    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value, 0, format);

    public void AppendFormatted<T>(T value, int alignment) => AppendFormatted(value, alignment, null);

    /// <summary>Formats like a regular interpolated string (current culture), so the text does not change.</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        var s = value is IFormattable f ? f.ToString(format, CultureInfo.CurrentCulture) : value?.ToString() ?? "";
        if (alignment > 0) s = s.PadLeft(alignment);
        else if (alignment < 0) s = s.PadRight(-alignment);
        _text!.Append(s);
        _template!.Append('{').Append(_arguments!.Count.ToString(CultureInfo.InvariantCulture)).Append('}');
        _arguments.Add(s);
    }

    public readonly string Text => _plain ?? _text?.ToString() ?? "";

    public readonly string Template => _plain is not null ? EscapeBraces(_plain) : _template?.ToString() ?? "";

    public readonly IReadOnlyList<string> Arguments => _arguments is null ? [] : [.. _arguments];

    private static string EscapeBraces(string s) => s.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);

    /// <summary>
    /// The resource key of a message's translation: "Diag_" + code + "_" + the FNV-1a hash of the template (UTF-16
    /// code units), so each wording of each code has its own entry and a changed English text needs a new translation.
    /// </summary>
    public static string Key(string code, string template)
    {
        var hash = 2166136261u;
        foreach (var c in template)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return $"Diag_{code}_{hash.ToString("x8", CultureInfo.InvariantCulture)}";
    }
}

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];
    public IReadOnlyList<Diagnostic> Items => _items;
    public bool HasErrors => _items.Any(d => d.Severity == Severity.Error);
    public void Error(string code, string field, DiagnosticMessage message) => _items.Add(Diagnostic.Create(Severity.Error, code, field, message));
    public void Warning(string code, string field, DiagnosticMessage message) => _items.Add(Diagnostic.Create(Severity.Warning, code, field, message));
    public void Info(string code, string field, DiagnosticMessage message) => _items.Add(Diagnostic.Create(Severity.Info, code, field, message));
    public void AddRange(IEnumerable<Diagnostic> items) => _items.AddRange(items);
}
