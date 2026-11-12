using FluentSetup.Builder.Services;
using FluentSetup.Builder.ViewModels;
using FluentSetup.Builder.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace FluentSetup.Builder;

public sealed partial class MainWindow : Window
{
    public MainViewModel VM => App.VM;
    private bool _closeConfirmed;

    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["overview"] = typeof(OverviewPage),
        ["app"] = typeof(AppInfoPage),
        ["files"] = typeof(FilesPage),
        ["install"] = typeof(InstallPage),
        ["prerequisites"] = typeof(PrerequisitesPage),
        ["options"] = typeof(OptionsPage),
        ["appearance"] = typeof(AppearancePage),
        ["upgrade"] = typeof(UpgradePage),
        ["uninstall"] = typeof(UninstallPage),
        ["build"] = typeof(BuildPage),
        ["history"] = typeof(HistoryPage),
        ["settings"] = typeof(SettingsPage),
    };

    public MainWindow()
    {
        InitializeComponent();
        Ui.Window = this;
        Title = Loc.Get("App_Title");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1160 * scale), (int)(760 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(760 * scale);
            presenter.PreferredMinimumHeight = (int)(520 * scale);
        }
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(work.X + (work.Width - AppWindow.Size.Width) / 2, work.Y + (work.Height - AppWindow.Size.Height) / 2));
        var icon = Path.Combine(AppContext.BaseDirectory, "runtime", "default-app.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        ApplyTheme();
        VM.ThemeChanged += (_, _) => ApplyTheme();
        VM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HasProject) && !VM.HasProject) Navigate("overview");
        };
        AppWindow.Closing += OnClosing;
        _ = VM.StartupUpdateCheckAsync();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    private void ApplyTheme()
    {
        var theme = VM.Settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        RootGrid.RequestedTheme = theme;
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !VM.IsDirty) return;
        args.Cancel = true;
        if (await VM.ConfirmCloseProjectAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "overview";
        if (Pages.TryGetValue(tag, out var page) && ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        }
    }

    /// <summary>Navigates to a page by tag (used by the overview's problem list and shortcuts).</summary>
    public void Navigate(string tag)
    {
        if (tag == "settings")
        {
            Nav.SelectedItem = Nav.SettingsItem;
            return;
        }
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if ((item.Tag as string) == tag)
            {
                Nav.SelectedItem = item;
                return;
            }
        }
    }
}
