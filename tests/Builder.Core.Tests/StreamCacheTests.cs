using FluentSetup.Builder.Core.Model;
using FluentSetup.Builder.Core.Packaging;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>Stream cache and stable block ends: rebuilds reuse unchanged streams and give byte-identical packages.</summary>
public class StreamCacheTests
{
    private static string RuntimeRoot => Path.Combine(AppContext.BaseDirectory, "runtime");

    private static (SetupProject Project, string ProjectPath) Project(TempDir dir, string compression)
    {
        var project = new SetupProject
        {
            App = new AppInfo { Id = "Test.Cache", Name = "Cache Test", Publisher = "P", Version = "1.0.0", MainExecutable = "App.exe" },
            Files = new FileSettings { Source = "app" },
            Output = new OutputSettings { Directory = "out", Compression = compression },
        };
        var projectPath = Path.Combine(dir.Path, "fluentsetup.json");
        ProjectSerializer.Save(project, projectPath);
        return (project, projectPath);
    }

    private static void RandomFile(TempDir dir, string rel, int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        var path = Path.Combine(dir.Path, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public async Task RebuildsAreIdenticalWithAndWithoutTheCache()
    {
        Assert.True(File.Exists(Path.Combine(RuntimeRoot, "x64", "FluentSetup.Stub.exe")), "installer runtime missing - build the native projects first");
        using var dir = new TempDir();
        dir.File("app/App.exe", "MZ fake");
        for (var i = 0; i < 20; i++) dir.File($"app/data/f{i}.txt", string.Concat(Enumerable.Repeat($"line {i} of a test file\n", 200 + i)));
        var (project, projectPath) = Project(dir, "normal");

        var builder = new PackageBuilder();
        var plain = await builder.BuildAsync(project, projectPath, new BuildOptions { RuntimeRoot = RuntimeRoot, UseCache = false, OutputDirectory = Path.Combine(dir.Path, "a") });
        var first = await builder.BuildAsync(project, projectPath, new BuildOptions { RuntimeRoot = RuntimeRoot, OutputDirectory = Path.Combine(dir.Path, "b") });
        var second = await builder.BuildAsync(project, projectPath, new BuildOptions { RuntimeRoot = RuntimeRoot, OutputDirectory = Path.Combine(dir.Path, "c") });
        Assert.True(plain.Success && first.Success && second.Success, plain.Error ?? first.Error ?? second.Error);
        Assert.Equal(0, plain.CachedStreams);
        Assert.Equal(second.StreamCount + (second.IncludesWinUI ? 1 : 0), second.CachedStreams);  // everything from the cache

        // The package does not depend on where its streams came from, nor on when it was built.
        var bytes = File.ReadAllBytes(plain.OutputPath!);
        Assert.Equal(bytes, File.ReadAllBytes(first.OutputPath!));
        Assert.Equal(bytes, File.ReadAllBytes(second.OutputPath!));
        Assert.Equal(plain.Sha256, second.Sha256);
    }

    [Fact]
    public async Task AChangedFileLeavesTheLaterBlocksUnchanged()
    {
        Assert.True(File.Exists(Path.Combine(RuntimeRoot, "x64", "FluentSetup.Stub.exe")), "installer runtime missing - build the native projects first");
        using var dir = new TempDir();
        dir.File("app/App.exe", "MZ fake");
        for (var i = 0; i < 48; i++) RandomFile(dir, $"app/data/f{i:D2}.bin", 1536 * 1024, i);
        var (project, projectPath) = Project(dir, "fast");
        var builder = new PackageBuilder();
        var before = await builder.BuildAsync(project, projectPath, new BuildOptions { RuntimeRoot = RuntimeRoot, OutputDirectory = Path.Combine(dir.Path, "a") });
        Assert.True(before.Success, before.Error);
        Assert.True(before.StreamCount >= 3, $"{before.StreamCount} streams");

        // An early file grows: only the blocks up to the next block end change.
        RandomFile(dir, "app/data/f03.bin", 1536 * 1024 + 4321, 1000);
        var after = await builder.BuildAsync(project, projectPath, new BuildOptions { RuntimeRoot = RuntimeRoot, OutputDirectory = Path.Combine(dir.Path, "b") });
        Assert.True(after.Success, after.Error);
        var payloadFromCache = after.CachedStreams - (after.IncludesWinUI ? 1 : 0);
        Assert.True(payloadFromCache >= after.StreamCount - 2, $"{payloadFromCache} of {after.StreamCount} payload streams reused");
    }

    [Fact]
    public async Task DiffListsChangedFilesAndReusableStreams()
    {
        Assert.True(File.Exists(Path.Combine(RuntimeRoot, "x64", "FluentSetup.Stub.exe")), "installer runtime missing - build the native projects first");
        using var dir = new TempDir();
        dir.File("app/App.exe", "MZ fake");
        dir.File("app/gone.txt", "removed in 1.1");
        dir.File("app/notes.txt", "1.0");
        for (var i = 0; i < 24; i++) RandomFile(dir, $"app/data/f{i:D2}.bin", 1536 * 1024, i);
        var (project, projectPath) = Project(dir, "fast");
        var builder = new PackageBuilder();
        var v1 = await builder.BuildAsync(project, projectPath, new BuildOptions { RuntimeRoot = RuntimeRoot, OutputDirectory = Path.Combine(dir.Path, "a") });
        File.Delete(Path.Combine(dir.Path, "app", "gone.txt"));
        dir.File("app/notes.txt", "1.1, longer");
        dir.File("app/new.txt", "added in 1.1");
        project.App.Version = "1.1.0";
        var v2 = await builder.BuildAsync(project, projectPath, new BuildOptions { RuntimeRoot = RuntimeRoot, OutputDirectory = Path.Combine(dir.Path, "b") });
        Assert.True(v1.Success && v2.Success, v1.Error ?? v2.Error);

        var d = PackageDiff.Compare(PackageInspector.Inspect(v1.OutputPath!, verifyStreams: false), PackageInspector.Inspect(v2.OutputPath!, verifyStreams: false));
        Assert.Equal(("1.0.0", "1.1.0"), (d.OldVersion, d.NewVersion));
        Assert.Equal(["new.txt"], d.Added.Select(c => c.Path));
        Assert.Equal(["gone.txt"], d.Removed.Select(c => c.Path));
        Assert.Equal(["notes.txt"], d.Changed.Select(c => c.Path));
        Assert.Equal(25, d.Unchanged);
        Assert.True(d.DifferentialDownload < d.NewFileSize / 2, $"{d.DifferentialDownload} of {d.NewFileSize}");  // the data blocks are reused
    }

    [Fact]
    public void BlockEndsDependOnThePathOnly()
    {
        Assert.Equal(PackageBuilder.IsBlockBoundary("bin\\App.dll"), PackageBuilder.IsBlockBoundary("BIN\\app.DLL"));
        var share = Enumerable.Range(0, 4000).Count(i => PackageBuilder.IsBlockBoundary($"lib\\file{i}.dll")) / 4000.0;
        Assert.InRange(share, 0.2, 0.3);  // about one path in four
    }
}
