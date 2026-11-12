using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Validation;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace FluentSetup.Builder.Core.Packaging;

public enum BuildStage { Validating, Collecting, Compressing, Writing, Signing, Verifying, Completed }

public sealed record BuildProgress(BuildStage Stage, double Fraction, string Message);

public sealed class BuildOptions
{
    public string? OutputDirectory { get; set; }
    public string? Architecture { get; set; }
    /// <summary>Variants to include (overrides output.variants); null = as configured.</summary>
    public List<string>? Variants { get; set; }
    /// <summary>null = as configured in the project.</summary>
    public bool? Sign { get; set; }
    public string? RuntimeRoot { get; set; }
    public bool VerifyOutput { get; set; } = true;
    public Action<string>? Log { get; set; }
}

public sealed class SizeBreakdown
{
    public long Stub { get; set; }
    public long WinUiFrontEnd { get; set; }
    public long Header { get; set; }
    public long Footer { get; set; }
    public long Padding { get; set; }
    public long Signature { get; set; }
    public long PayloadCompressed { get; set; }
    public long PayloadRaw { get; set; }
    public long Total { get; set; }
    /// <summary>Everything that is not the compressed payload.</summary>
    public long InstallerOverhead => Total - PayloadCompressed;
}

public sealed class BuildReport
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OutputPath { get; set; }
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Architecture { get; set; } = "";
    public int FileCount { get; set; }
    public int StreamCount { get; set; }
    public bool IncludesWinUI { get; set; }
    public bool Signed { get; set; }
    public SizeBreakdown Sizes { get; set; } = new();
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public double DurationSeconds => (FinishedAt - StartedAt).TotalSeconds;
    public List<Diagnostic> Diagnostics { get; set; } = [];
    public string? Sha256 { get; set; }
}

public sealed class BuildFailedException(string message, IEnumerable<Diagnostic>? diagnostics = null) : Exception(message)
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics?.ToList() ?? [];
}

/// <summary>Turns a project into Setup.exe: validate → collect → hash+compress → write container → sign → verify.</summary>
public sealed class PackageBuilder
{
    private const long SolidBlockBytes = 32L * 1024 * 1024;
    private const long OwnStreamBytes = 16L * 1024 * 1024;

    public async Task<BuildReport> BuildAsync(SetupProject project, string projectPath, BuildOptions options,
                                              IProgress<BuildProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var report = new BuildReport { StartedAt = DateTimeOffset.Now, AppId = project.App.Id, AppName = project.App.Name, Version = project.App.Version };
        var log = options.Log ?? (_ => { });
        var tempDir = Path.Combine(Path.GetTempPath(), "FluentSetup-build-" + Guid.NewGuid().ToString("N")[..12]);
        string? partial = null;
        try
        {
            await Task.Run(() => Build(project, projectPath, options, report, tempDir, p => partial = p, progress, log, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            report.Success = true;
        }
        catch (OperationCanceledException)
        {
            report.Error = "Build cancelled.";
            throw;
        }
        catch (BuildFailedException ex)
        {
            report.Error = ex.Message;
            report.Diagnostics.AddRange(ex.Diagnostics.Where(d => !report.Diagnostics.Contains(d)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception or JsonException)
        {
            report.Error = ex.Message;
        }
        finally
        {
            report.FinishedAt = DateTimeOffset.Now;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
            if (!report.Success && partial is not null) { try { File.Delete(partial); } catch (IOException) { } }
        }
        return report;
    }

    /// <summary>The variants that go into this package: --variant, else output.variants, else all.</summary>
    internal static List<VariantSettings> SelectedVariants(SetupProject project, BuildOptions options)
    {
        var wanted = options.Variants is { Count: > 0 } ? options.Variants : project.Output.Variants;
        var all = project.Files.Variants;
        if (wanted.Count == 0) return [.. all];
        var missing = wanted.Where(id => all.All(v => v.Id != id)).ToList();
        if (missing.Count > 0)
            throw new BuildFailedException($"Unknown variant: {string.Join(", ", missing)}.", [new Diagnostic(Severity.Error, "FS1662", "output.variants", $"No variant {string.Join(", ", missing)} in files.variants.")]);
        return [.. all.Where(v => wanted.Contains(v.Id))];
    }
    private static void Build(SetupProject project, string projectPath, BuildOptions options, BuildReport report, string tempDir,
                              Action<string> setPartial, IProgress<BuildProgress>? progress, Action<string> log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var arch = options.Architecture ?? project.Output.Architecture;
        report.Architecture = arch;
        var assets = new RuntimeAssets(options.RuntimeRoot);

        // ---- validate ------------------------------------------------------------------------------------------
        progress?.Report(new(BuildStage.Validating, 0, "Validating project"));
        var diagnostics = ProjectValidator.Validate(project, projectDir);
        report.Diagnostics.AddRange(diagnostics.Items);
        if (!assets.HasStub(arch)) diagnostics.Error("FS2001", "output.architecture", $"The installer runtime for {arch} is missing: {assets.StubPath(arch)}");
        var includeUi = project.Output.IncludeWinUI && assets.HasUi(arch);
        if (project.Output.IncludeWinUI && !includeUi)
        {
            diagnostics.Warning("FS2002", "output.includeWinUI", $"The WinUI front end for {arch} is not available; the package will use the Win32 interface only.");
        }
        report.IncludesWinUI = includeUi;
        if (diagnostics.HasErrors)
        {
            report.Diagnostics.Clear();
            report.Diagnostics.AddRange(diagnostics.Items);
            throw new BuildFailedException("The project has errors.", diagnostics.Items);
        }

        // ---- collect -------------------------------------------------------------------------------------------
        progress?.Report(new(BuildStage.Collecting, 0, "Collecting files"));
        var variants = SelectedVariants(project, options);
        var collected = FileCollector.Collect(project, projectDir, variants, ct);
        report.Diagnostics.Clear();
        report.Diagnostics.AddRange(diagnostics.Items);
        report.Diagnostics.AddRange(collected.Diagnostics.Items);
        var fileChecks = new DiagnosticBag();
        ExtensionValidator.ValidateAgainstFiles(project, collected.Files, fileChecks);
        report.Diagnostics.AddRange(fileChecks.Items);
        if (collected.Diagnostics.HasErrors || fileChecks.HasErrors) throw new BuildFailedException("Some files cannot be installed.", report.Diagnostics);
        report.FileCount = collected.Files.Count;
        report.Sizes.PayloadRaw = collected.TotalSize;
        log($"Collected {collected.Files.Count:N0} files, {Format(collected.TotalSize)} in {sw.Elapsed.TotalSeconds:F1}s");

        Directory.CreateDirectory(tempDir);

        // ---- compress ------------------------------------------------------------------------------------------
        var level = project.Output.Compression switch { "fast" => 3, "max" => 19, _ => 12 };
        // Shared files first, then each variant's files in streams of their own: installing one variant never has to
        // decode another variant's data.
        var ordered = collected.Files
            .OrderBy(f => f.Variant, StringComparer.Ordinal)
            .ThenBy(f => Path.GetExtension(f.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var blocks = new List<List<CollectedFile>>();
        var current = new List<CollectedFile>();
        long currentSize = 0;
        foreach (var f in ordered)
        {
            if (current.Count > 0 && current[0].Variant != f.Variant)
            {
                blocks.Add(current);
                current = [];
                currentSize = 0;
            }
            if (f.Size >= OwnStreamBytes)
            {
                blocks.Add([f]);
                continue;
            }
            current.Add(f);
            currentSize += f.Size;
            if (currentSize >= SolidBlockBytes)
            {
                blocks.Add(current);
                current = [];
                currentSize = 0;
            }
        }
        if (current.Count > 0) blocks.Add(current);

        var results = new CompressedStream[blocks.Count];
        long done = 0;
        var total = Math.Max(1, collected.TotalSize);
        progress?.Report(new(BuildStage.Compressing, 0, $"Compressing {collected.Files.Count:N0} files"));
        Parallel.For(0, blocks.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var workers = blocks[i].Count == 1 && blocks[i][0].Size >= OwnStreamBytes ? Math.Min(4, Environment.ProcessorCount) : 0;
            results[i] = CompressFiles(blocks[i], Path.Combine(tempDir, $"block-{i}.zst"), level, workers, project.Output.Compression == "max", n =>
            {
                var now = Interlocked.Add(ref done, n);
                progress?.Report(new(BuildStage.Compressing, (double)now / total, $"Compressing ({Format(now)} of {Format(total)})"));
            }, ct);
        });
        report.StreamCount = results.Length;
        log($"Compressed to {Format(results.Sum(r => r.CompressedSize))} in {sw.Elapsed.TotalSeconds:F1}s (level {level}, {blocks.Count} streams)");

        CompressedStream? ui = null;
        byte[] icon = !string.IsNullOrEmpty(project.App.Icon)
            ? IconConverter.LoadAsIco(ProjectValidator.Resolve(projectDir, project.App.Icon))
            : File.Exists(assets.DefaultIcon) ? File.ReadAllBytes(assets.DefaultIcon) : [];
        if (includeUi)
        {
            var uiFiles = assets.UiFiles(arch).Select(f => new CollectedFile(f, Path.GetFileName(f), new FileInfo(f).Length, false, 0)).ToList();
            if (icon.Length > 0)
            {
                var iconTemp = Path.Combine(tempDir, "app.ico");
                File.WriteAllBytes(iconTemp, icon);
                uiFiles.Add(new CollectedFile(iconTemp, "app.ico", icon.Length, false, 0));
            }
            ui = CompressFiles(uiFiles, Path.Combine(tempDir, "ui.zst"), 19, 0, true, _ => { }, ct);
        }

        // ---- write ---------------------------------------------------------------------------------------------
        progress?.Report(new(BuildStage.Writing, 0, "Writing Setup.exe"));
        var outputDir = ProjectValidator.Resolve(projectDir, options.OutputDirectory ?? project.Output.Directory);
        Directory.CreateDirectory(outputDir);
        var outputName = ProjectValidator.ExpandFileName(project.Output.FileName, project);
        var outputPath = Path.Combine(outputDir, outputName);
        var partial = Path.Combine(outputDir, $".{outputName}.{Guid.NewGuid():N}.partial");
        setPartial(partial);
        File.Copy(assets.StubPath(arch), partial, overwrite: true);
        File.SetAttributes(partial, FileAttributes.Normal);
        PeResources.Apply(partial, icon.Length > 0 ? icon : null, new PeResources.VersionInfo(
            ProductName: project.App.Name,
            CompanyName: project.App.Publisher,
            FileDescription: $"{project.App.Name} Setup",
            ProductVersion: project.App.Version,
            OriginalFilename: outputName,
            Copyright: string.IsNullOrEmpty(project.App.Publisher) ? "" : $"© {project.App.Publisher}"));
        report.Sizes.Stub = new FileInfo(partial).Length;

        using (var output = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            output.Seek(0, SeekOrigin.End);
            Pad8(output, report.Sizes);
            var overlayStart = output.Position;
            StreamRecord? uiRecord = null;
            if (ui is not null)
            {
                uiRecord = AppendStream(output, ui, overlayStart, report.Sizes);
                report.Sizes.WinUiFrontEnd = ui.CompressedSize;
            }
            var payloadRecords = new List<StreamRecord>();
            foreach (var r in results)
            {
                ct.ThrowIfCancellationRequested();
                payloadRecords.Add(AppendStream(output, r, overlayStart, report.Sizes));
            }
            report.Sizes.PayloadCompressed = results.Sum(r => r.CompressedSize);

            var license = string.IsNullOrEmpty(project.Ui.License) ? "" : File.ReadAllText(ProjectValidator.Resolve(projectDir, project.Ui.License));
            var texts = Localization.Load(project, projectDir);
            var licenses = texts.LicenseFiles.ToDictionary(kv => kv.Key, kv => File.ReadAllText(kv.Value));
            var headerJson = HeaderWriter.Write(project, texts, variants, arch, license, licenses, payloadRecords, results, collected.Directories, uiRecord, ui);
            var headerCompressed = new Compressor(19).Wrap(headerJson).ToArray();
            var headerOffset = output.Position - overlayStart;
            output.Write(headerCompressed);
            report.Sizes.Header = headerCompressed.Length;
            Pad8(output, report.Sizes);
            var footer = PackageFormat.EncodeFooter(new PackageFormat.Footer(PackageFormat.FormatVersion, PackageFormat.FlagHeaderCompressed,
                (ulong)overlayStart, (ulong)headerOffset, (ulong)headerCompressed.Length, (ulong)headerJson.Length, SHA256.HashData(headerCompressed)));
            output.Write(footer);
            report.Sizes.Footer = footer.Length;
            output.Flush(flushToDisk: true);
        }

        // ---- sign ----------------------------------------------------------------------------------------------
        if (options.Sign ?? project.Signing.Enabled)
        {
            progress?.Report(new(BuildStage.Signing, 0, "Signing"));
            var before = new FileInfo(partial).Length;
            Signer.Sign(project.Signing, projectDir, partial, project.App.Name, log);
            report.Signed = true;
            report.Sizes.Signature = new FileInfo(partial).Length - before;
        }

        // ---- verify --------------------------------------------------------------------------------------------
        if (options.VerifyOutput)
        {
            progress?.Report(new(BuildStage.Verifying, 0, "Verifying"));
            var info = PackageInspector.Inspect(partial, verifyStreams: true);
            if (info.Problems.Count > 0) throw new BuildFailedException("The written package failed verification: " + string.Join("; ", info.Problems));
        }

        File.Move(partial, outputPath, overwrite: true);
        report.OutputPath = outputPath;
        report.Sizes.Total = new FileInfo(outputPath).Length;
        using (var fs = File.OpenRead(outputPath)) report.Sha256 = PackageFormat.Hex(SHA256.HashData(fs));
        progress?.Report(new(BuildStage.Completed, 1, "Done"));
        log($"Wrote {outputPath} ({Format(report.Sizes.Total)}) in {sw.Elapsed.TotalSeconds:F1}s");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    internal sealed record FileEntry(CollectedFile File, long Offset, byte[] Sha256);

    internal sealed class CompressedStream
    {
        public required string TempFile { get; init; }
        public required List<FileEntry> Files { get; init; }
        public long RawSize { get; init; }
        public long CompressedSize { get; init; }
        public required byte[] Sha256 { get; init; }
    }

    internal sealed record StreamRecord(long Offset, long Size, long RawSize, byte[] Sha256);

    private static CompressedStream CompressFiles(List<CollectedFile> files, string tempFile, int level, int workers, bool longWindow,
                                                  Action<long> onBytes, CancellationToken ct)
    {
        var entries = new List<FileEntry>(files.Count);
        long raw = 0;
        using var fileOut = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        using var hashOut = new HashingStream(fileOut);
        using (var zstd = new CompressionStream(hashOut, level, 1 << 20, leaveOpen: true))
        {
            if (workers > 1) zstd.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, workers);
            // The decoder caps the window at 2^27; never produce frames that need more.
            if (longWindow)
            {
                zstd.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, 27);
                zstd.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
            }
            zstd.SetPledgedSrcSize((ulong)files.Sum(f => f.Size));
            var buffer = new byte[1 << 20];
            foreach (var f in files)
            {
                using var input = new FileStream(f.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                if (input.Length != f.Size) throw new BuildFailedException($"'{f.SourcePath}' changed while the package was being built.");
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long left = f.Size;
                while (left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n == 0) throw new BuildFailedException($"'{f.SourcePath}' became shorter while the package was being built.");
                    sha.AppendData(buffer, 0, n);
                    zstd.Write(buffer, 0, n);
                    left -= n;
                    onBytes(n);
                }
                entries.Add(new FileEntry(f, raw, sha.GetHashAndReset()));
                raw += f.Size;
            }
        }
        hashOut.Flush();
        return new CompressedStream
        {
            TempFile = tempFile,
            Files = entries,
            RawSize = raw,
            CompressedSize = hashOut.Length,
            Sha256 = hashOut.GetHash(),
        };
    }

    private static StreamRecord AppendStream(FileStream output, CompressedStream s, long overlayStart, SizeBreakdown sizes)
    {
        var offset = output.Position - overlayStart;
        using (var input = File.OpenRead(s.TempFile)) input.CopyTo(output, 1 << 20);
        Pad8(output, sizes);
        return new StreamRecord(offset, s.CompressedSize, s.RawSize, s.Sha256);
    }

    private static void Pad8(Stream s, SizeBreakdown sizes)
    {
        var pad = (int)((8 - s.Position % 8) % 8);
        if (pad > 0)
        {
            s.Write(new byte[pad]);
            sizes.Padding += pad;
        }
    }

    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
    }

    /// <summary>Write-through stream computing SHA-256 and length of everything written.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
            _length += count;
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            inner.Write(buffer);
            _length += buffer.Length;
        }
        public byte[] GetHash() => _hash.GetHashAndReset();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Package header (see docs/package-format.md and src/Install.Core/package.cpp).</summary>
    internal static class HeaderWriter
    {
        public static byte[] Write(SetupProject p, Localization.Texts texts, IReadOnlyList<VariantSettings> variants, string arch, string license, Dictionary<string, string> licenses, List<StreamRecord> payload, CompressedStream[] payloadStreams,
                                   List<string> directories, StreamRecord? uiRecord, CompressedStream? ui)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteNumber("format", 1);
                w.WriteString("kind", "setup");
                w.WriteStartObject("app");
                w.WriteString("id", p.App.Id);
                w.WriteString("name", p.App.Name);
                w.WriteString("publisher", p.App.Publisher);
                w.WriteString("version", p.App.Version);
                w.WriteString("description", p.App.Description);
                w.WriteString("website", p.App.Website);
                w.WriteString("mainExecutable", string.IsNullOrEmpty(p.App.MainExecutable) ? "" : p.App.MainExecutable.Replace('/', '\\'));
                w.WriteString("architecture", arch);
                w.WriteNumber("minWindowsBuild", p.Install.MinWindowsBuild);
                w.WriteEndObject();

                w.WriteStartObject("install");
                w.WriteString("scope", p.Install.Scope);
                w.WriteString("defaultScope", p.Install.DefaultScope);
                w.WriteString("userDir", p.Install.UserDir);
                w.WriteString("machineDir", p.Install.MachineDir);
                w.WriteBoolean("allowCustomDir", p.Install.AllowCustomDir);
                w.WriteString("desktopShortcut", p.Install.DesktopShortcut);
                w.WriteString("startMenuShortcut", p.Install.StartMenuShortcut);
                w.WriteString("launchAfterInstall", p.Install.LaunchAfterInstall);
                w.WriteBoolean("allowDowngrade", p.Install.AllowDowngrade);
                w.WriteStartArray("closeProcesses");
                foreach (var n in p.Install.CloseProcesses) w.WriteStringValue(n);
                w.WriteEndArray();
                if (p.Install.AdoptExistingInstall) w.WriteString("adoptUninstallKey", p.App.Id);
                WriteStrings(w, "legacyUninstallKeys", p.Install.LegacyUninstallKeys);
                if (p.Install.CloseCommand is { Run.Length: > 0 } cc)
                {
                    w.WriteStartObject("closeCommand");
                    w.WriteString("run", cc.Run.Replace('/', '\\'));
                    WriteStrings(w, "args", cc.Arguments);
                    w.WriteEndObject();
                }
                WriteStrings(w, "ngen", p.Install.Ngen.Select(n => n.Replace('/', '\\')));
                w.WriteStartObject("cleanup");
                WriteStrings(w, "removeScheduledTasks", p.Install.Cleanup.RemoveScheduledTasks);
                WriteStrings(w, "removeRunValues", p.Install.Cleanup.RemoveRunValues);
                WriteStrings(w, "removeShortcuts", p.Install.Cleanup.RemoveShortcuts);
                WriteStrings(w, "removeUninstallKeys", p.Install.Cleanup.RemoveUninstallKeys);
                w.WriteEndObject();
                w.WriteEndObject();
                WriteExtensions(w, p, texts, variants);
                w.WriteStartArray("variants");
                if (variants.Count > 1)
                {
                    var defaultId = (variants.FirstOrDefault(v => v.Default) ?? variants[0]).Id;
                    foreach (var v in variants)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", v.Id);
                        WriteLocalized(w, "label", texts.VariantLabels.GetValueOrDefault(v.Id) ?? v.Label);
                        WriteLocalized(w, "description", texts.VariantDescriptions.GetValueOrDefault(v.Id) ?? v.Description);
                        w.WriteBoolean("default", v.Id == defaultId);
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();

                w.WriteStartObject("ui");
                w.WriteString("title", p.Ui.Title);
                w.WriteString("welcomeText", p.Ui.WelcomeText);
                w.WriteString("finishText", p.Ui.FinishText);
                w.WriteString("theme", p.Ui.Theme);
                w.WriteString("backdrop", p.Ui.Backdrop);
                w.WriteBoolean("useSystemAccent", p.Ui.UseSystemAccent);
                w.WriteString("accentColor", p.Ui.AccentColor);
                w.WriteString("license", license);
                w.WriteStartObject("licenses");
                foreach (var (lang, text) in licenses) w.WriteString(lang, text);
                w.WriteEndObject();
                w.WriteString("licenseMode", p.Ui.LicenseMode);
                w.WriteString("defaultLanguage", p.Ui.DefaultLanguage);
                w.WriteStartObject("strings");
                foreach (var (lang, table) in texts.Strings)
                {
                    w.WriteStartObject(lang);
                    foreach (var (k, v) in table) w.WriteString(k, v);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndObject();

                w.WriteStartObject("payload");
                WriteStreams(w, payload);
                w.WriteStartArray("files");
                for (var s = 0; s < payloadStreams.Length; s++) WriteFiles(w, payloadStreams[s], s);
                w.WriteEndArray();
                w.WriteStartArray("directories");
                foreach (var d in directories) w.WriteStringValue(d);
                w.WriteEndArray();
                w.WriteEndObject();

                if (uiRecord is not null && ui is not null)
                {
                    w.WriteStartObject("frontend");
                    WriteStreams(w, [uiRecord]);
                    w.WriteStartArray("files");
                    WriteFiles(w, ui, 0);
                    w.WriteEndArray();
                    w.WriteString("entry", "FluentSetup.UI.exe");
                    if (ui.Files.Any(f => f.File.RelativePath == "app.ico")) w.WriteString("icon", "app.ico");
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            return ms.ToArray();
        }

        private static void WriteLocalized(Utf8JsonWriter w, string name, Dictionary<string, string> text)
        {
            w.WriteStartObject(name);
            foreach (var (lang, s) in text) w.WriteString(lang, s);
            w.WriteEndObject();
        }

        private static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
        {
            w.WriteStartArray(name);
            foreach (var v in values) w.WriteStringValue(v);
            w.WriteEndArray();
        }

        private static void WriteSource(Utf8JsonWriter w, PrerequisiteSource? s)
        {
            if (s is null) return;
            if (!string.IsNullOrEmpty(s.Installer)) w.WriteString("installer", s.Installer.Replace('/', '\\'));
            WriteStrings(w, "args", s.Arguments);
            if (s.Download is { Url.Length: > 0 } dl)
            {
                w.WriteStartObject("download");
                w.WriteString("url", dl.Url);
                w.WriteString("sha256", dl.Sha256.ToLowerInvariant());
                w.WriteNumber("size", dl.Size);
                w.WriteEndObject();
            }
            if (!string.IsNullOrEmpty(s.HelpUrl)) w.WriteString("helpUrl", s.HelpUrl);
            w.WriteString("onFailure", s.OnFailure);
        }

        private static void WriteExtensions(Utf8JsonWriter w, SetupProject p, Localization.Texts texts, IReadOnlyList<VariantSettings> variants)
        {
            // A requirement limited to variants that are not in this package is dropped; with a single variant the
            // filter is resolved here (the package then has no variants).
            bool Applies(PrerequisiteSource? s) => s is null || s.Variants.Count == 0 || variants.Count == 0 || s.Variants.Any(id => variants.Any(v => v.Id == id));
            void WriteVariantFilter(PrerequisiteSource? s)
            {
                if (s is null || s.Variants.Count == 0 || variants.Count < 2) return;
                WriteStrings(w, "variants", s.Variants.Where(id => variants.Any(v => v.Id == id)));
            }
            w.WriteStartArray("requirements");
            if (ExtensionValidator.NetFrameworkRelease(p.Requirements.NetFramework) is { } release && Applies(p.Requirements.NetFrameworkSetup))
            {
                w.WriteStartObject();
                w.WriteString("type", "netfx");
                w.WriteNumber("minRelease", release);
                w.WriteString("name", $".NET Framework {p.Requirements.NetFramework}");
                WriteSource(w, p.Requirements.NetFrameworkSetup);
                WriteVariantFilter(p.Requirements.NetFrameworkSetup);
                w.WriteEndObject();
            }
            if (p.Requirements.WebView2.Required && Applies(p.Requirements.WebView2))
            {
                w.WriteStartObject();
                w.WriteString("type", "webview2");
                w.WriteString("name", "Microsoft Edge WebView2 Runtime");
                if (!string.IsNullOrEmpty(p.Requirements.WebView2.MinVersion)) w.WriteString("minVersion", p.Requirements.WebView2.MinVersion);
                WriteSource(w, p.Requirements.WebView2);
                WriteVariantFilter(p.Requirements.WebView2);
                w.WriteEndObject();
            }
            if (p.Requirements.WindowsAppRuntime is { Required: true } war && Applies(war))
            {
                var parts = war.Version.Split('-')[0].Split('.');
                var channel = parts[0] == "1" && parts.Length > 1 ? $"{parts[0]}.{parts[1]}" : parts[0];
                w.WriteStartObject();
                w.WriteString("type", "windowsAppRuntime");
                w.WriteString("channel", channel);
                w.WriteString("name", $"Windows App Runtime {war.Version}");
                if (parts[0] != "1") w.WriteString("minVersion", war.Version);  // 2.x+: the package version is the SDK version
                WriteSource(w, war);
                WriteVariantFilter(war);
                w.WriteEndObject();
            }
            foreach (var r in p.Requirements.DotNet.Where(Applies))
            {
                var (runtime, label) = ExtensionValidator.DotNetRuntime(r.Runtime);
                w.WriteStartObject();
                w.WriteString("type", "dotnet");
                w.WriteString("runtime", runtime);
                w.WriteString("name", $"{label} {r.Version}");
                w.WriteString("minVersion", r.Version);
                w.WriteString("rollForward", r.RollForward);
                WriteSource(w, r);
                WriteVariantFilter(r);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("options");
            foreach (var o in p.Options)
            {
                w.WriteStartObject();
                w.WriteString("id", o.Id);
                WriteLocalized(w, "label", texts.OptionLabels.GetValueOrDefault(o.Id) ?? o.Label);
                w.WriteBoolean("default", o.Default || o.Required);
                if (o.Required) w.WriteBoolean("required", true);
                if (o.Startup is { Name.Length: > 0 } st)
                {
                    w.WriteStartObject("startup");
                    w.WriteString("name", st.Name);
                    WriteStrings(w, "args", st.Arguments);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("actions");
            foreach (var (a, index) in p.Actions.Select((a, i) => (a, i)))
            {
                w.WriteStartObject();
                w.WriteString("when", a.When);
                w.WriteString("run", a.Run.Replace('/', '\\'));
                w.WriteStartArray("args");
                foreach (var x in a.Arguments) w.WriteStringValue(x);
                w.WriteEndArray();
                w.WriteNumber("timeoutSeconds", a.TimeoutSeconds);
                w.WriteStartArray("successExitCodes");
                foreach (var code in a.SuccessExitCodes) w.WriteNumberValue(code);
                w.WriteEndArray();
                w.WriteString("onFailure", a.OnFailure);
                if (!string.IsNullOrEmpty(a.IfOption)) w.WriteString("ifOption", a.IfOption);
                if (a.SkipIfInstalled is { } c)
                {
                    w.WriteStartObject("skipIfInstalled");
                    w.WriteString("hive", c.Hive);
                    w.WriteString("key", c.Key);
                    w.WriteString("value", c.Value);
                    w.WriteString("version", c.Version);
                    w.WriteEndObject();
                }
                WriteLocalized(w, "name", texts.ActionNames.GetValueOrDefault(index) ?? a.Name);
                if (a.FallbackArguments is { } fb) WriteStrings(w, "fallbackArgs", fb);
                if (a.FallbackRunValue is { Name.Length: > 0 } rv)
                {
                    w.WriteStartObject("fallbackRunValue");
                    w.WriteString("name", rv.Name);
                    WriteStrings(w, "args", rv.Arguments);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("uninstall");
            foreach (var (name, list) in new[] { ("removeData", p.Uninstall.RemoveData), ("removeScheduledTasks", p.Uninstall.RemoveScheduledTasks), ("removeRunValues", p.Uninstall.RemoveRunValues), ("removeShortcuts", p.Uninstall.RemoveShortcuts) })
            {
                w.WriteStartArray(name);
                foreach (var s in list) w.WriteStringValue(s);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }

        private static void WriteStreams(Utf8JsonWriter w, List<StreamRecord> streams)
        {
            w.WriteStartArray("streams");
            foreach (var s in streams)
            {
                w.WriteStartObject();
                w.WriteNumber("offset", s.Offset);
                w.WriteNumber("size", s.Size);
                w.WriteNumber("rawSize", s.RawSize);
                w.WriteString("sha256", PackageFormat.Hex(s.Sha256));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        private static void WriteFiles(Utf8JsonWriter w, CompressedStream s, int streamIndex)
        {
            foreach (var f in s.Files)
            {
                w.WriteStartObject();
                w.WriteString("path", f.File.RelativePath);
                w.WriteNumber("size", f.File.Size);
                w.WriteString("sha256", PackageFormat.Hex(f.Sha256));
                w.WriteNumber("stream", streamIndex);
                w.WriteNumber("offset", f.Offset);
                if (f.File.Attributes != 0) w.WriteNumber("attributes", (int)f.File.Attributes);
                if (f.File.Preserve) w.WriteBoolean("preserve", true);
                if (f.File.Variant.Length > 0) w.WriteString("variant", f.File.Variant);
                w.WriteEndObject();
            }
        }
    }
}
