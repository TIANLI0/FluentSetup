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
