using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentSetup.Builder.Core.Prerequisites;
using FluentSetup.Builder.Services;

namespace FluentSetup.Builder.ViewModels;

/// <summary>A version offered by a version box: the value and a line about it ("LTS · supported until …").</summary>
public sealed class VersionSuggestion(string version, string detail)
{
    public string Version { get; } = version;
    public string Detail { get; } = detail;
    public override string ToString() => Version;
}

/// <summary>
/// The versions of one prerequisite from Microsoft (<see cref="VersionCatalog"/>), shared by every version box of that
/// prerequisite. Loaded when the first box appears, refreshed on request.
/// </summary>
public sealed partial class VersionCatalogViewModel(VersionProduct product) : ObservableObject
{
    private bool _started;

    public VersionProduct Product { get; } = product;
    public ObservableCollection<VersionSuggestion> Items { get; } = [];

    /// <summary>"From Microsoft, 14/11/2026 08:00", "Offline · list from …", …</summary>
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsLoading { get; set; }

    /// <summary>Visual C++ has no online list.</summary>
    public bool CanRefresh => Product != VersionProduct.VcRedist;

    public void EnsureLoaded()
    {
        if (_started) return;
        _started = true;
        _ = LoadAsync(refresh: false);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(refresh: true);

    private async Task LoadAsync(bool refresh)
    {
        if (IsLoading) return;
        IsLoading = true;
        if (refresh || Items.Count == 0) Status = Loc.Get("Versions_Loading");
        try
        {
            var list = await VersionCatalog.Shared.GetAsync(Product, refresh);
            Items.Clear();
            foreach (var v in list.Versions) Items.Add(new VersionSuggestion(v.Version, Describe(v)));
            Status = list.Source switch
            {
                VersionListSource.Online or VersionListSource.Cache => Loc.Format("Versions_FromMicrosoft", When(list.RetrievedAt)),
                VersionListSource.StaleCache => Loc.Format("Versions_OfflineSaved", When(list.RetrievedAt)),
                _ when Product == VersionProduct.VcRedist => Loc.Get("Versions_VcNote"),
                _ => Loc.Get("Versions_OfflineBuiltIn"),
            };
        }
        catch (Exception ex)
        {
            Status = Loc.Format("Versions_Failed", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string When(DateTimeOffset? at) => at?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";

    private static string Date(DateOnly? d) => d?.ToString("d", CultureInfo.CurrentCulture) ?? "";

    /// <summary>The line under a version in the list, in the Builder's language.</summary>
    private string Describe(CatalogVersion v)
    {
        var parts = new List<string>();
        switch (Product)
        {
            case VersionProduct.WebView2:
                parts.Add(v.Channel.Equals("Beta", StringComparison.OrdinalIgnoreCase) ? Loc.Get("Versions_EdgeBeta") : Loc.Get("Versions_EdgeStable"));
                if (v.Released is not null) parts.Add(Loc.Format("Versions_Released", Date(v.Released)));
                break;
            case VersionProduct.WindowsAppRuntime:
                if (v.Version.StartsWith("1.", StringComparison.Ordinal))
                {
                    parts.Add(Loc.Format("Versions_WasChannel", v.Version));
                    if (v.Latest.Length > 0) parts.Add(Loc.Format("Versions_Latest", v.Latest));
                }
                else
                {
                    parts.Add("Windows App SDK " + v.Version);
                }
                break;
            case VersionProduct.DotNet:
                parts.Add(".NET " + v.Channel);
                if (v.ReleaseType.Length > 0) parts.Add(v.IsLts ? Loc.Get("Versions_Lts") : Loc.Get("Versions_Sts"));
                if (v.IsEndOfLife) parts.Add(v.EndOfSupport is null ? Loc.Get("Versions_Eol") : Loc.Format("Versions_EolSince", Date(v.EndOfSupport)));
                else if (v.EndOfSupport is not null) parts.Add(Loc.Format("Versions_SupportedUntil", Date(v.EndOfSupport)));
                if (v.Latest.Length > 0) parts.Add(Loc.Format("Versions_Latest", v.Latest));
                break;
            default:
                parts.Add(v.Version == "14.0" ? Loc.Get("Versions_VcAny") : v.Channel);
                break;
        }
        if (v.IsNewest && Product != VersionProduct.VcRedist) parts.Add(Loc.Get("Versions_Newest"));
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }
}
