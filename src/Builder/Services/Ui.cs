using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FluentSetup.Builder.Services;

/// <summary>Window-bound UI services (dialogs, pickers, clipboard) used by the view model.</summary>
public static class Ui
{
    public static Window? Window { get; set; }
    public static XamlRoot? XamlRoot => Window?.Content?.XamlRoot;
    private static IntPtr Hwnd => Window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(Window);
    private static bool _dialogOpen;

    public static async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (XamlRoot is null || _dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try
        {
            dialog.XamlRoot = XamlRoot;
            dialog.Style ??= Application.Current.Resources["DefaultContentDialogStyle"] as Style;
            if (Window?.Content is FrameworkElement root) dialog.RequestedTheme = root.ActualTheme;
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    public static Task<ContentDialogResult> MessageAsync(string title, string message) =>
        ShowDialogAsync(new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, CloseButtonText = Loc.Get("Common_Close") });

    /// <summary>Returns Primary = save, Secondary = discard, None = cancel.</summary>
    public static Task<ContentDialogResult> ConfirmUnsavedAsync() =>
        ShowDialogAsync(new ContentDialog
        {
            Title = Loc.Get("Unsaved_Title"),
            Content = Loc.Get("Unsaved_Body"),
            PrimaryButtonText = Loc.Get("Unsaved_Save"),
            SecondaryButtonText = Loc.Get("Unsaved_Discard"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        });

    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        foreach (var e in extensions) picker.FileTypeFilter.Add(e);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add(Loc.Get("FileType_Project"), [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static void CopyText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    public static void OpenFolder(string path, bool select = false)
    {
        try
        {
            var psi = select
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
