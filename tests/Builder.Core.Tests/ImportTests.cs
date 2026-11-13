using FluentSetup.Builder.Core.Import;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Validation;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>fluentsetup import: Inno Setup and NSIS scripts under tests/fixtures/import become projects plus notes.</summary>
public sealed class ImportTests
{
    private static string FixturesRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "import");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("tests/fixtures/import not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A copy of a fixture folder in a temporary folder (imports write next to the script).</summary>
    private static TempDir Fixture(string name)
    {
        var dir = new TempDir();
        var source = Path.Combine(FixturesRoot(), name);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dir.Path, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return dir;
    }

    private static bool Has(ImportResult r, ImportSeverity severity, string text) =>
        r.Notes.Any(n => n.Severity == severity && n.Message.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static string Expand(SetupProject p, string s) => p.Variables.Aggregate(s, (t, v) => t.Replace("$(" + v.Key + ")", v.Value, StringComparison.OrdinalIgnoreCase));

    // ---- Inno Setup, per-user --------------------------------------------------------------------------------

    [Fact]
    public void Inno_per_user_identity_scope_and_output()
    {
        using var dir = Fixture("inno-per-user");
        var script = Path.Combine(dir.Path, "installer", "setup.iss");
        var r = ProjectImporter.Import(script, Path.Combine(dir.Path, "installer"));
        var p = r.Project;
        Assert.Equal(ScriptFormat.InnoSetup, r.Format);

        Assert.Equal("$(MyAppName)", p.App.Name);
        Assert.Equal("$(MyAppVersion)", p.App.Version);
        Assert.Equal("$(MyAppPublisher)", p.App.Publisher);
        Assert.Equal("$(MyAppURL)", p.App.Website);
        Assert.Equal("Contoso Notes", p.Variables["MyAppName"]);
        Assert.Equal("2.4.1", p.Variables["MyAppVersion"]);
        Assert.False(p.Variables.ContainsKey("MyAppExeName"));   // only used in paths, which are written out
        Assert.Equal("Contoso.Notes", p.App.Id);
        Assert.Equal(["{8C4E5D1A-3B2F-4F6A-9C7D-1E2F3A4B5C6D}_is1"], p.Install.LegacyUninstallKeys);
        Assert.Contains("unins???.exe", p.Install.Cleanup.RemoveFiles);
        Assert.Equal("ContosoNotes.exe", p.App.MainExecutable);

        Assert.Equal("user", p.Install.Scope);
        Assert.Equal(@"{LocalAppData}\Programs\{AppName}", p.Install.UserDir);
        Assert.Equal("ContosoNotes-{Version}-Setup.exe", p.Output.FileName);
        Assert.Equal("../dist", p.Output.Directory);
        Assert.Equal("assets/notes.ico", p.App.Icon);
        Assert.Equal("LICENSE.txt", p.Ui.License);
        Assert.Equal("x64", p.Output.Architecture);
        Assert.Equal("max", p.Output.Compression);
        Assert.Equal(17763, p.Install.MinWindowsBuild);
        Assert.Equal("auto", p.Install.RestartManager);

        // The icon is not in the fixture: a note, not a broken project.
        Assert.True(Has(r, ImportSeverity.Warning, "Icon file not found"));
    }

    [Fact]
    public void Inno_per_user_tasks_shortcuts_and_registry()
    {
        using var dir = Fixture("inno-per-user");
        var r = ProjectImporter.Import(Path.Combine(dir.Path, "installer", "setup.iss"), Path.Combine(dir.Path, "installer"));
        var p = r.Project;

        Assert.Equal("on", p.Install.StartMenuShortcut);
        Assert.Equal("$(MyAppName)", p.Install.StartMenuFolder);
        Assert.Equal("optionalOff", p.Install.DesktopShortcut);      // task desktopicon, Flags: unchecked
        Assert.Equal("optionalOn", p.Install.LaunchAfterInstall);    // [Run] postinstall
        Assert.Empty(p.Shortcuts);                                    // the uninstall shortcut is not needed
        Assert.True(Has(r, ImportSeverity.Info, "to the uninstaller is not created"));

        var autostart = Assert.Single(p.Options);
        Assert.Equal("autostart", autostart.Id);
        Assert.False(autostart.Default);
        Assert.Equal("Start Contoso Notes when I sign in", autostart.Label["en-US"]);
        Assert.Equal("登录时启动 Contoso Notes", autostart.Label["zh-CN"]);
        Assert.NotNull(autostart.Startup);
        Assert.Equal("ContosoNotes", autostart.Startup!.Name);
        Assert.Equal(["--background"], autostart.Startup.Arguments);

        var key = Assert.Single(p.Registry);
        Assert.Equal(@"Software\Contoso\Notes", key.Key);
        Assert.Equal("key", key.Uninstall);
        Assert.Equal("{InstallDir}", key.Values.Single(v => v.Name == "InstallDir").Data);
        var dword = key.Values.Single(v => v.Name == "FirstRun");
        Assert.Equal("dword", dword.Type);
        Assert.Equal("0x1", dword.Data);                              // Inno's Pascal hex $1

        Assert.Equal(["{InstallDir}\\logs", "{LocalAppData}\\Contoso\\Notes"], p.Uninstall.RemoveData);
        Assert.Equal(["logs"], p.Files.EmptyDirectories);
    }

    [Fact]
    public void Inno_per_user_files_use_the_build_output_as_source()
    {
        using var dir = Fixture("inno-per-user");
        var r = ProjectImporter.Import(Path.Combine(dir.Path, "installer", "setup.iss"), Path.Combine(dir.Path, "installer"));
        var f = r.Project.Files;
        Assert.Equal("../build/publish", f.Source);
        Assert.Equal(["**/*"], f.Include);
        Assert.Contains("*.pdb", f.Exclude);
        Assert.Contains("*.xml", f.Exclude);
        var extra = Assert.Single(f.Extra);
        Assert.Equal("defaults/settings.json", extra.Source);
        Assert.Equal("config", extra.Target);
        Assert.Equal(["config/settings.json"], f.Preserve);
        // The build output does not exist yet: reported, not an error in the project.
        Assert.True(Has(r, ImportSeverity.Warning, "Source folder not found"));
    }

    [Fact]
    public void Inno_per_user_project_is_valid_once_the_build_output_exists()
    {
        using var dir = Fixture("inno-per-user");
        dir.File("build/publish/ContosoNotes.exe", "MZ");
        dir.File("installer/assets/notes.ico", "ico");
        var installer = Path.Combine(dir.Path, "installer");
        var r = ProjectImporter.Import(Path.Combine(installer, "setup.iss"), installer);
        ProjectImporter.Save(r, overwrite: false);

        var saved = ProjectSerializer.Load(Path.Combine(installer, "fluentsetup.json"));
        var bag = new DiagnosticBag();
        var expanded = ProjectVariables.Expand(saved, null, bag);
        var errors = ProjectValidator.Validate(expanded, installer).Items.Concat(bag.Items).Where(d => d.Severity == Severity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.DoesNotContain(r.Notes, n => n.Message.Contains("still has an error", StringComparison.Ordinal));

        var report = File.ReadAllText(Path.Combine(installer, "import-report.md"));
        Assert.Contains("# Import report: setup.iss", report);
        Assert.Contains("## To do by hand", report);
        Assert.Contains("## Mapped", report);
        Assert.Contains("`setup.iss:", report);
        Assert.Throws<IOException>(() => ProjectImporter.Save(r, overwrite: false));
    }

    // ---- Inno Setup, per-machine with overrides --------------------------------------------------------------

    [Fact]
    public void Inno_per_machine_scope_folders_and_computed_version()
    {
        using var dir = Fixture("inno-per-machine");
        var r = ProjectImporter.Import(Path.Combine(dir.Path, "installer", "setup.iss"), Path.Combine(dir.Path, "installer"));
        var p = r.Project;

        Assert.Equal("Fabrikam.Studio", p.App.Id);                    // AppId is a valid app id: kept
        Assert.Equal(["Fabrikam.Studio_is1"], p.Install.LegacyUninstallKeys);
        Assert.Equal("userOrMachine", p.Install.Scope);
        Assert.Equal("machine", p.Install.DefaultScope);
        Assert.Equal(@"{ProgramFiles}\Fabrikam\{AppName}", p.Install.MachineDir);
        Assert.Equal(@"{LocalAppData}\Programs\Fabrikam\{AppName}", p.Install.UserDir);
        Assert.Equal("Fabrikam", p.Install.StartMenuFolder);
        Assert.Equal("FabrikamStudio-Setup.exe", p.Output.FileName);
        Assert.Equal("x64", p.Output.Architecture);
        Assert.True(Has(r, ImportSeverity.Info, "--arch arm64"));

        // AppVersion comes from the built Studio.exe (GetVersionNumbersString), which does not exist here.
        Assert.Equal("$(AppVersion)", p.App.Version);
        Assert.Equal("1.0.0", p.Variables["AppVersion"]);
        Assert.True(Has(r, ImportSeverity.Manual, "#define AppVersion GetVersionNumbersString"));
        Assert.True(Has(r, ImportSeverity.Info, "#include \"common.iss\" read"));
        Assert.Contains(r.Notes, n => n.File.EndsWith("common.iss", StringComparison.OrdinalIgnoreCase) && n.Line == 4);
    }

    [Fact]
    public void Inno_per_machine_file_type_protocol_app_paths_and_environment()
    {
        using var dir = Fixture("inno-per-machine");
        var r = ProjectImporter.Import(Path.Combine(dir.Path, "installer", "setup.iss"), Path.Combine(dir.Path, "installer"));
        var p = r.Project;

        var assoc = Assert.Single(p.Associations);
        Assert.Equal([".fstudio"], assoc.Extensions);
        Assert.Equal("Fabrikam.Studio.Project", assoc.ProgId);
        Assert.Equal("Fabrikam Studio Project", assoc.Description);
        Assert.Equal("", assoc.Target);                               // the main executable
        Assert.Equal(["%1"], assoc.Arguments);
        Assert.Equal(1, assoc.IconIndex);
        Assert.Equal("fileassoc", assoc.IfOption);

        var protocol = Assert.Single(p.Protocols);
        Assert.Equal("fabrikam-studio", protocol.Scheme);
        Assert.Equal("Fabrikam Studio Protocol", protocol.Description);
        Assert.Equal(["--open-url", "%1"], protocol.Arguments);
        Assert.Equal("", protocol.IfOption);

        Assert.True(p.Install.AppPaths);
        Assert.Equal(["tools"], p.Install.AddToPath);
        Assert.True(Has(r, ImportSeverity.Warning, "PATH entry depended on task \"addtopath\""));
        var env = Assert.Single(p.Environment);
        Assert.Equal("FABRIKAM_STUDIO_HOME", env.Name);
        Assert.Equal("{InstallDir}", env.Value);

        var settings = Assert.Single(p.Registry);
        Assert.Equal(@"Software\Fabrikam\Studio", settings.Key);
        Assert.Equal("key", settings.Uninstall);
        Assert.Equal("$(AppVersion)", settings.Values.Single(v => v.Name == "Version").Data);
        Assert.True(Has(r, ImportSeverity.Manual, @"Software\Policies\Fabrikam"));

        var ini = Assert.Single(p.ConfigFiles);
        Assert.Equal("studio.ini", ini.Path);
        Assert.Equal("ini", ini.Format);
        Assert.Equal("{InstallDir}", ini.Values.Single(v => v.Key == "InstallDir").Value);
        Assert.Equal("General", ini.Values[0].Section);
    }

    [Fact]
    public void Inno_per_machine_tasks_shortcuts_runs_and_code()
    {
        using var dir = Fixture("inno-per-machine");
        var r = ProjectImporter.Import(Path.Combine(dir.Path, "installer", "setup.iss"), Path.Combine(dir.Path, "installer"));
        var p = r.Project;

        Assert.Equal("optionalOn", p.Install.DesktopShortcut);
        Assert.Equal("optionalOff", p.Install.LaunchAfterInstall);
        Assert.Equal("Fabrikam.Studio", p.App.AppUserModelId);
        var safe = Assert.Single(p.Shortcuts);
        Assert.Equal("$(AppName) (Safe Mode)", safe.Name);
        Assert.Equal("", safe.Target);
        Assert.Equal(["--safe-mode"], safe.Arguments);
        Assert.Equal("safe.ico", safe.Icon);
        Assert.True(Has(r, ImportSeverity.Manual, "Fabrikam on the Web"));

        var option = Assert.Single(p.Options);                        // addtopath only controls files and PATH
        Assert.Equal("fileassoc", option.Id);
        Assert.True(option.Default);
        Assert.Equal("Associate $(AppName) with the .fstudio file extension", option.Label["en-US"]);
        Assert.Single(option.Label);                                  // German has no text of its own here
        Assert.True(Has(r, ImportSeverity.Warning, "Task \"addtopath\" controls files, PATH"));

        Assert.True(p.Requirements.VcRedist.Required);
        Assert.Equal("bundle", p.Requirements.VcRedist.Auto);
        Assert.Equal("", p.Requirements.VcRedist.Architecture);

        Assert.Equal(2, p.Actions.Count);
        Assert.Equal("afterInstall", p.Actions[0].When);
        Assert.Equal("StudioHelper.exe", p.Actions[0].Run);
        Assert.Equal(["--register-fonts", "{InstallDir}\\fonts"], p.Actions[0].Arguments);
        Assert.Equal("warn", p.Actions[0].OnFailure);
        Assert.Equal("Registering fonts...", p.Actions[0].Name["en-US"]);
        Assert.Equal("beforeUninstall", p.Actions[1].When);
        Assert.Equal(["--unregister-fonts"], p.Actions[1].Arguments);
        Assert.Equal(["StudioAgent.exe"], p.Install.CloseProcesses);
        Assert.True(Has(r, ImportSeverity.Manual, "firewall[]"));
        Assert.Equal(["{CommonAppData}\\Fabrikam\\Studio\\cache"], p.Uninstall.RemoveData);

        Assert.True(Has(r, ImportSeverity.Manual, "[Code]"));
        Assert.True(Has(r, ImportSeverity.Manual, "Event functions: InitializeSetup"));
        Assert.True(Has(r, ImportSeverity.Manual, "requirements.netFramework"));
        Assert.True(Has(r, ImportSeverity.Manual, "SignTool"));
        Assert.True(Has(r, ImportSeverity.Manual, "fluentsetup locale add de-DE"));
        Assert.True(Has(r, ImportSeverity.Warning, "Check: NeedsAddPath"));

        // Files: the build output is the source; other folders are extra.
        Assert.Equal("../bin/Release", p.Files.Source);
        Assert.Equal(["**/*"], p.Files.Include);
        Assert.Contains(p.Files.Extra, e => e.Source == "../docs/manual.pdf" && e.Target == "docs");
        Assert.Contains(p.Files.Extra, e => e.Source == "../tools" && e.Target == "tools");
    }

    // ---- NSIS ------------------------------------------------------------------------------------------------

    [Fact]
    public void Nsis_mui2_identity_scope_and_output()
    {
        using var dir = Fixture("nsis-mui2");
        var installer = Path.Combine(dir.Path, "installer");
        var r = ProjectImporter.Import(Path.Combine(installer, "installer.nsi"), installer);
        var p = r.Project;
        Assert.Equal(ScriptFormat.Nsis, r.Format);

        Assert.Equal("NorthwindSync", p.App.Id);                      // the uninstall key
        Assert.True(p.Install.AdoptExistingInstall);
        Assert.Empty(p.Install.LegacyUninstallKeys);
        Assert.Equal("$(APPNAME)", p.App.Name);
        Assert.Equal("$(VERSION)", p.App.Version);
        Assert.Equal("3.2.0", p.Variables["VERSION"]);
        Assert.Equal("$(COMPANYNAME)", p.App.Publisher);
        Assert.Equal("$(HELPURL)", p.App.Website);
        Assert.Equal("$(DESCRIPTION)", p.App.Description);
        Assert.Equal("NorthwindSync.exe", p.App.MainExecutable);

        Assert.Equal("machine", p.Install.Scope);
        Assert.Equal(@"{ProgramFiles}\{Publisher}\{AppName}", p.Install.MachineDir);
        Assert.Equal("NorthwindSync-{Version}-x64-setup.exe", p.Output.FileName);
        Assert.Equal("../dist", p.Output.Directory);
        Assert.Equal("resources/sync.ico", p.App.Icon);
        Assert.Equal("license.txt", p.Ui.License);
        Assert.Equal("optionalOn", p.Install.LaunchAfterInstall);
        Assert.Equal(["uninst.exe"], p.Install.Cleanup.RemoveFiles);
        Assert.Equal(["NorthwindSync Updater"], p.Install.Cleanup.RemoveScheduledTasks);
    }

    [Fact]
    public void Nsis_mui2_sections_become_options()
    {
        using var dir = Fixture("nsis-mui2");
        var installer = Path.Combine(dir.Path, "installer");
        var r = ProjectImporter.Import(Path.Combine(installer, "installer.nsi"), installer);
        var p = r.Project;

        Assert.Equal("on", p.Install.StartMenuShortcut);
        Assert.Equal("$(APPNAME)", p.Install.StartMenuFolder);
        Assert.Equal("optionalOn", p.Install.DesktopShortcut);       // section "Desktop shortcut" (selected)
        Assert.Empty(p.Shortcuts);                                    // the uninstaller shortcut is not needed

        var main = p.Options.Single(o => o.Id == "SecMain");
        Assert.True(main.Required);                                   // SectionIn RO
        Assert.Equal("$(APPNAME) (required)", main.Label["en-US"]);
        var autostart = p.Options.Single(o => o.Id == "SecAutostart");
        Assert.False(autostart.Default);                              // Section /o
        Assert.Equal("NorthwindSync", autostart.Startup!.Name);
        Assert.Equal(["--tray"], autostart.Startup.Arguments);
        var shell = p.Options.Single(o => o.Id == "SecShell");
        Assert.True(shell.Default);
        Assert.DoesNotContain(p.Options, o => o.Id == "SecDesktop");  // became the desktop shortcut's checkbox

        var assoc = Assert.Single(p.Associations);
        Assert.Equal([".nwsync"], assoc.Extensions);
        Assert.Equal("Northwind.Sync.Link", assoc.ProgId);
        Assert.Equal("SecShell", assoc.IfOption);
        Assert.True(Has(r, ImportSeverity.Manual, "context-menu"));

        var action = p.Actions.Single(a => a.When == "afterInstall");
        Assert.Equal("NorthwindSync.exe", action.Run);
        Assert.Equal(["--install-service"], action.Arguments);
        Assert.Equal("SecMain", action.IfOption);

        var key = Assert.Single(p.Registry);
        Assert.Equal(@"Software\$(COMPANYNAME)\$(APPNAME)", key.Key);
        Assert.Equal("key", key.Uninstall);                           // DeleteRegKey in the uninstall section
        Assert.Equal(["InstallDir", "Version"], key.Values.Select(v => v.Name));
        Assert.Equal("{InstallDir}", key.Values[0].Data);
    }

    [Fact]
    public void Nsis_mui2_files_and_uninstall_section()
    {
        using var dir = Fixture("nsis-mui2");
        var installer = Path.Combine(dir.Path, "installer");
        var r = ProjectImporter.Import(Path.Combine(installer, "installer.nsi"), installer);
        var p = r.Project;

        Assert.Equal("../build/Release", p.Files.Source);
        Assert.Equal(["**/*"], p.Files.Include);
        Assert.Contains("*.pdb", p.Files.Exclude);
        Assert.Contains(p.Files.Extra, e => e.Source == "../third_party/nwfilter/nwfilter.inf" && e.Target == "drivers");
        Assert.Contains(p.Files.Extra, e => e.Source == "defaults/sync.ini" && e.Target == "");
        Assert.Equal(["./sync.ini"], p.Files.Preserve);               // SetOverwrite off
        Assert.Equal(["logs"], p.Files.EmptyDirectories);

        var un = p.Actions.Single(a => a.When == "beforeUninstall");
        Assert.Equal(["--uninstall-service"], un.Arguments);
        Assert.Equal(["{InstallDir}\\logs"], p.Uninstall.RemoveData);
        Assert.Equal(["{LocalAppData}\\Northwind\\Sync"], p.Uninstall.UserData.Paths);
        Assert.False(p.Uninstall.UserData.Default);                   // un.section /o
        Assert.Empty(p.Uninstall.RemoveRunValues);                     // the startup option removes its own value

        Assert.True(Has(r, ImportSeverity.Manual, "Function .onInit"));
        Assert.True(Has(r, ImportSeverity.Manual, "checks the architecture"));
        Assert.Contains(r.Notes, n => n.Message.Contains("SimpChinese (zh-CN)", StringComparison.Ordinal));
    }

    [Fact]
    public void Nsis_per_user_with_a_legacy_uninstall_key()
    {
        using var dir = new TempDir();
        var script = dir.File("paint.nsi", """
            !define APP "Contoso Paint"
            !define VERSION "1.5"
            Name "${APP}"
            OutFile "ContosoPaint-${VERSION}.exe"
            InstallDir "$LOCALAPPDATA\Programs\${APP}"
            RequestExecutionLevel user
            Section
              SetOutPath "$INSTDIR"
              File "app\Paint.exe"
              File /r "app\brushes"
              CreateShortCut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\Paint.exe"
              CreateShortCut "$SMSTARTUP\${APP} Tray.lnk" "$INSTDIR\Paint.exe" "--tray"
              WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}" "DisplayName" "${APP}"
              WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}" "Publisher" "Contoso"
              ${If} ${FileExists} "$INSTDIR\old.dll"
                Delete "$INSTDIR\old.dll"
              ${Else}
                DetailPrint "clean"
              ${EndIf}
            SectionEnd
            """);
        var r = ProjectImporter.Import(script, dir.Path);
        var p = r.Project;
        Assert.Equal("user", p.Install.Scope);
        Assert.Equal(@"{LocalAppData}\Programs\{AppName}", p.Install.UserDir);
        Assert.Equal(["Contoso Paint"], p.Install.LegacyUninstallKeys);  // not a valid app id
        Assert.Equal("Contoso.Paint", p.App.Id);
        Assert.Equal("1.5", p.Variables["VERSION"]);
        Assert.Equal("$(VERSION)", p.App.Version);
        Assert.Equal("ContosoPaint-{Version}.exe", p.Output.FileName);
        Assert.Equal("Paint.exe", p.App.MainExecutable);
        Assert.Equal("on", p.Install.StartMenuShortcut);
        Assert.Equal("", p.Install.StartMenuFolder);
        var tray = Assert.Single(p.Shortcuts);
        Assert.Equal("startup", tray.Location);
        Assert.Equal(["--tray"], tray.Arguments);
        Assert.Equal(["old.dll"], p.Install.Cleanup.RemoveFiles);
        Assert.True(Has(r, ImportSeverity.Warning, "only the first branch"));
        Assert.Equal("app", p.Files.Source);
        Assert.Equal(["./Paint.exe", "brushes/**/*"], p.Files.Include);
    }

    [Theory]
    [InlineData("inno-per-user", "installer/setup.iss", new[] { "build/publish/ContosoNotes.exe", "installer/assets/notes.ico" })]
    [InlineData("inno-per-machine", "installer/setup.iss", new[] { "bin/Release/Studio.exe", "docs/manual.pdf", "tools/studio-cli.exe" })]
    [InlineData("nsis-mui2", "installer/installer.nsi", new[] { "build/Release/NorthwindSync.exe", "third_party/nwfilter/nwfilter.inf", "installer/resources/sync.ico" })]
    public void Every_fixture_gives_a_valid_project(string fixture, string script, string[] buildOutput)
    {
        using var dir = Fixture(fixture);
        foreach (var file in buildOutput) dir.File(file, "MZ");
        var path = Path.Combine(dir.Path, script);
        var r = ProjectImporter.Import(path, Path.GetDirectoryName(path)!);
        var bag = new DiagnosticBag();
        var expanded = ProjectVariables.Expand(r.Project, null, bag);
        var errors = ProjectValidator.Validate(expanded, r.OutputDirectory).Items.Concat(bag.Items).Where(d => d.Severity == Severity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.DoesNotContain(r.Notes, n => n.Message.Contains("not found", StringComparison.Ordinal) && n.Field.Length > 0);
        // Every note points at a line of a script file (or at the script as a whole).
        Assert.All(r.Notes, n => Assert.True(File.Exists(n.File), n.ToString()));
    }

    [Theory]
    [InlineData("Contoso.Notes.Document", ".cnote", "Contoso.Notes.Document")]
    [InlineData("MyAppFile", ".myp", "MyAppFile.myp")]
    [InlineData("My_App-File.1", ".x", "My.App.File.1")]
    [InlineData("1st.Type", ".x", "st.Type")]
    [InlineData("A.Very.Long.ProgId.That.Goes.Beyond.The.Limit.Of.Windows", ".x", "A.That.Goes.Beyond.The.Limit.Of.Windows")]
    public void ProgIds_are_made_acceptable(string progId, string extension, string expected) =>
        Assert.Equal(expected, ImportMapper.SanitizeProgId(progId, extension));

    // ---- preprocessor, detection, CLI ------------------------------------------------------------------------

    [Fact]
    public void Inno_preprocessor_conditionals_and_components()
    {
        using var dir = new TempDir();
        var script = dir.File("app.iss", """
            #define Edition "Pro"
            #define Name "Widget " + Edition
            #if Edition == "Pro"
              #define Exe "WidgetPro.exe"
            #else
              #define Exe "Widget.exe"
            #endif
            #ifdef Undefined
              #error not here
            #endif
            [Setup]
            AppName={#Name}
            AppVersion=v3.1
            AppPublisher=Widgets $(Inc)
            DefaultDirName={autopf}\{#Name}
            PrivilegesRequired=lowest

            [Components]
            Name: "main"; Description: "Program files"; Types: full compact; Flags: fixed
            Name: "plugins"; Description: "Plug-ins"; Types: full

            [Types]
            Name: "full"; Description: "Full installation"
            Name: "compact"; Description: "Compact installation"

            [Files]
            Source: "bin\*"; DestDir: "{app}"; Components: main; Flags: recursesubdirs
            Source: "bin\{#Exe}"; DestDir: "{app}"; DestName: "Widget.exe"

            [Icons]
            Name: "{autoprograms}\{#Name}"; Filename: "{app}\{#Exe}"
            Name: "{autoprograms}\Plug-in manager"; Filename: "{app}\plugins.exe"; Components: plugins

            [Registry]
            Root: HKCU; Subkey: "Software\Widgets"; ValueType: string; ValueName: "Edition"; ValueData: "{#Edition}"; Components: plugins

            [Bogus]
            Something: "x"
            """);
        var r = ProjectImporter.Import(script, dir.Path);
        var p = r.Project;
        Assert.Equal("Widget $(Edition)", p.App.Name);                // a concatenation keeps its variables
        Assert.Equal("Pro", p.Variables["Edition"]);
        Assert.Equal("3.1", p.App.Version);                          // "v3.1" fixed
        Assert.True(Has(r, ImportSeverity.Warning, "AppVersion \"v3.1\""));
        Assert.Equal("Widgets $$(Inc)", p.App.Publisher);              // a literal "$(" is escaped
        Assert.Equal("WidgetPro.exe", p.App.MainExecutable);
        Assert.Equal(@"{LocalAppData}\Programs\{AppName}", p.Install.UserDir);
        Assert.True(Has(r, ImportSeverity.Manual, "installed as \"Widget.exe\""));
        Assert.True(Has(r, ImportSeverity.Manual, "Section [Bogus]"));
        Assert.DoesNotContain(r.Notes, n => n.Message.Contains("not here", StringComparison.Ordinal));

        var plugins = Assert.Single(p.Options);
        Assert.Equal("plugins", plugins.Id);
        Assert.True(plugins.Default);                                 // in the first type ("full")
        Assert.Equal("Plug-ins", plugins.Label["en-US"]);
        Assert.Equal("plugins", Assert.Single(p.Shortcuts).IfOption);
        Assert.Equal("plugins", Assert.Single(p.Registry).IfOption);
        Assert.Equal("Widget Pro", Expand(p, p.App.Name));
    }

    [Fact]
    public void Inno_relaunch_and_close_commands_are_not_actions()
    {
        using var dir = new TempDir();
        var script = dir.File("app.iss", """
            [Setup]
            AppName=Relauncher
            AppVersion=1.0
            DefaultDirName={autopf}\Relauncher
            PrivilegesRequired=lowest
            [Files]
            Source: "bin\*"; DestDir: "{app}"; Flags: recursesubdirs
            [Icons]
            Name: "{autoprograms}\Relauncher"; Filename: "{app}\Relauncher.exe"
            [Run]
            Filename: "{app}\Relauncher.exe"; Flags: nowait postinstall skipifsilent; Check: not IsRelaunchRequested
            ; In-app update: starts the app again after a silent install
            Filename: "{app}\Relauncher.exe"; Parameters: "--updated"; Flags: nowait runasoriginaluser; Check: IsRelaunchRequested
            Filename: "{app}\TrayHelper.exe"; Flags: nowait
            Filename: "{app}\Core.exe"; Parameters: "--enable"; Description: "Enable now"; Flags: postinstall runhidden waituntilterminated skipifsilent
            [UninstallRun]
            Filename: "{app}\Relauncher.exe"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "Stop"
            """);
        var r = ProjectImporter.Import(script, dir.Path);
        var p = r.Project;
        Assert.Equal("optionalOn", p.Install.LaunchAfterInstall);
        Assert.Equal(["--updated"], p.Install.RelaunchArguments);
        Assert.NotNull(p.Install.CloseCommand);
        Assert.Equal("Relauncher.exe", p.Install.CloseCommand!.Run);
        Assert.Equal(["--exit"], p.Install.CloseCommand.Arguments);
        Assert.True(Has(r, ImportSeverity.Manual, "TrayHelper.exe is started without waiting"));
        // A finish-page checkbox running another program: an option (off in quiet installs) and an action.
        var enable = Assert.Single(p.Options);
        Assert.Equal("Core", enable.Id);
        Assert.Equal("Enable now", enable.Label["en-US"]);
        Assert.True(enable.Default);
        Assert.False(enable.QuietDefault);
        var action = Assert.Single(p.Actions);
        Assert.Equal("Core.exe", action.Run);
        Assert.Equal(["--enable"], action.Arguments);
        Assert.Equal("Core", action.IfOption);
    }

    [Theory]
    [InlineData("a.iss", "", ScriptFormat.InnoSetup)]
    [InlineData("a.nsi", "", ScriptFormat.Nsis)]
    [InlineData("a.nsh", "", ScriptFormat.Nsis)]
    [InlineData("setup.txt", "[Setup]\nAppName=X\n[Files]\n", ScriptFormat.InnoSetup)]
    [InlineData("build.script", "!include MUI2.nsh\nOutFile x.exe\nSection\nSectionEnd\n", ScriptFormat.Nsis)]
    public void Detects_the_format(string name, string content, ScriptFormat format)
    {
        using var dir = new TempDir();
        Assert.Equal(format, ProjectImporter.Detect(dir.File(name, content)));
    }

    [Fact]
    public void Rejects_files_that_are_not_scripts()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.txt", "hello world");
        Assert.Null(ProjectImporter.Detect(path));
        Assert.Throws<InvalidDataException>(() => ProjectImporter.Import(path, dir.Path));
        Assert.Throws<FileNotFoundException>(() => ProjectImporter.Import(Path.Combine(dir.Path, "missing.iss"), dir.Path));
    }

    [Fact]
    public async Task Cli_import_writes_the_project_and_refuses_to_overwrite()
    {
        using var dir = Fixture("nsis-mui2");
        var script = Path.Combine(dir.Path, "installer", "installer.nsi");
        var output = Path.Combine(dir.Path, "out");
        Assert.Equal(0, await global::FluentSetup.Cli.Program.Main(["import", script, "-o", output]));
        Assert.True(File.Exists(Path.Combine(output, "fluentsetup.json")));
        Assert.True(File.Exists(Path.Combine(output, "import-report.md")));
        // Paths are relative to the output folder.
        Assert.Equal("../build/Release", ProjectSerializer.Load(Path.Combine(output, "fluentsetup.json")).Files.Source);
        Assert.Equal(2, await global::FluentSetup.Cli.Program.Main(["import", script, "-o", output]));
        Assert.Equal(0, await global::FluentSetup.Cli.Program.Main(["import", script, "-o", output, "--force", "--json"]));
        Assert.Equal(2, await global::FluentSetup.Cli.Program.Main(["import", Path.Combine(dir.Path, "missing.nsi")]));
        Assert.Equal(2, await global::FluentSetup.Cli.Program.Main(["import", script, "--bogus"]));
    }
}
