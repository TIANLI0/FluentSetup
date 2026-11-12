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

    /// <summary>The installer language for this PC (same rule as the installer: first supported Windows display language).</summary>
    public static string InstallerLanguage(string projectDefault)
    {
        var supported = FluentSetup.Builder.Core.Packaging.Localization.SupportedLanguages;
        if (supported.Contains(projectDefault)) return projectDefault;
        foreach (var lang in Windows.System.UserProfile.GlobalizationPreferences.Languages)
        {
            var match = supported.FirstOrDefault(s => lang.StartsWith(s[..2], StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return "en-US";
    }
}
