namespace FluentSetup.Builder.Core.Validation;

public enum Severity { Info, Warning, Error }

/// <param name="Code">Stable identifier, e.g. FS1001 (documented in docs/diagnostics.md).</param>
/// <param name="Field">The project property the message is about, e.g. "app.version" (for UI navigation).</param>
public sealed record Diagnostic(Severity Severity, string Code, string Field, string Message)
{
    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()} {Code} [{Field}]: {Message}";
}

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];
    public IReadOnlyList<Diagnostic> Items => _items;
    public bool HasErrors => _items.Any(d => d.Severity == Severity.Error);
    public void Error(string code, string field, string message) => _items.Add(new(Severity.Error, code, field, message));
    public void Warning(string code, string field, string message) => _items.Add(new(Severity.Warning, code, field, message));
    public void Info(string code, string field, string message) => _items.Add(new(Severity.Info, code, field, message));
    public void AddRange(IEnumerable<Diagnostic> items) => _items.AddRange(items);
}
