using CommunityToolkit.Mvvm.Input;
using FluentSetup.Builder.Core.Import;
using FluentSetup.Builder.Services;

namespace FluentSetup.Builder.ViewModels;

/// <summary>Import from an Inno Setup or NSIS script (fluentsetup import in the GUI).</summary>
public partial class MainViewModel
{
    [RelayCommand]
    private async Task ImportProjectAsync()
    {
        if (!await ConfirmCloseProjectAsync()) return;
        var script = await Ui.PickFileAsync(".iss", ".nsi");
        if (script is null) return;
        // The project goes into the folder the user picks (usually the script's own installer folder).
        var folder = await Ui.PickFolderAsync();
        if (folder is null) return;
        var projectPath = Path.Combine(folder, ProjectImporter.ProjectFileName);
        if (File.Exists(projectPath))
        {
            var replace = await Ui.ShowDialogAsync(new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = Loc.Get("Import_ReplaceTitle"),
                Content = string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Get("Import_ReplaceBody"), projectPath),
                PrimaryButtonText = Loc.Get("Import_Replace"),
                CloseButtonText = Loc.Get("Import_Cancel"),
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
            });
            if (replace != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return;
        }
        ImportResult result;
        try
        {
            result = ProjectImporter.Import(script, folder);
            ProjectImporter.Save(result, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await Ui.MessageAsync(Loc.Get("Import_FailedTitle"), ex.Message);
            return;
        }
        OpenPath(result.ProjectPath);
        await Ui.MessageAsync(Loc.Get("Import_DoneTitle"), string.Format(System.Globalization.CultureInfo.CurrentCulture, Loc.Get("Import_DoneBody"),
            result.Count(ImportSeverity.Manual), result.Count(ImportSeverity.Warning), result.ReportPath));
    }
}
