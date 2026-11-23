using Microsoft.Windows.ApplicationModel.Resources;

namespace FluentSetup.Builder.Services;

/// <summary>Localized strings from Strings\&lt;lang&gt;\Resources.resw (follows the Windows display language).</summary>
public static class Loc
{
    private static ResourceLoader? _loader;

    public static string Get(string key)
    {
        try
        {
            _loader ??= new ResourceLoader();
            var value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            return key;
        }
    }

    public static string Format(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);

    private static readonly Dictionary<string, string?> DiagnosticTemplates = [];

    /// <summary>
    /// A validation message in the display language: the translation of its template (resource "Diag_&lt;code&gt;_&lt;hash&gt;",
    /// see DiagnosticMessage.Key) with the message's values, or the English message when there is none.
    /// </summary>
    public static string Diagnostic(FluentSetup.Builder.Core.Validation.Diagnostic d)
    {
        if (d.Template is null) return d.Message;
        var key = FluentSetup.Builder.Core.Validation.DiagnosticMessage.Key(d.Code, d.Template);
        string? template;
        lock (DiagnosticTemplates)
        {
            if (!DiagnosticTemplates.TryGetValue(key, out template))
            {
                var value = Get(key);
                DiagnosticTemplates[key] = template = value == key ? null : value;
            }
        }
        if (template is null) return d.Message;
        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, template, [.. d.Arguments]);
        }
        catch (FormatException)
        {
            return d.Message;
        }
    }

    /// <summary>The installer language for this PC (same rule as the installer: the project's language, else the first
    /// Windows display language it ships, e.g. zh-HK -> zh-TW, de-AT -> de-DE).</summary>
    public static string InstallerLanguage(string projectDefault)
    {
        if (FluentSetup.Builder.Core.Packaging.Localization.BuiltInLanguage(projectDefault) is { } pinned) return pinned;
        foreach (var lang in Windows.System.UserProfile.GlobalizationPreferences.Languages)
        {
            if (FluentSetup.Builder.Core.Packaging.Localization.BuiltInLanguage(lang) is { } match) return match;
        }
        return "en-US";
    }
}
