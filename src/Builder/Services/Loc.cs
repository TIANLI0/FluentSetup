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

    /// <summary>"zh-CN" if Windows shows Chinese, else "en-US" (matches the installer's language choice).</summary>
    public static string InstallerLanguage(string projectDefault)
    {
        if (projectDefault is "en-US" or "zh-CN") return projectDefault;
        foreach (var lang in Windows.System.UserProfile.GlobalizationPreferences.Languages)
        {
            if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en-US";
        }
        return "en-US";
    }
}
