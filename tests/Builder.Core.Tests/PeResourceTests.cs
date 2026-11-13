using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using FluentSetup.Builder.Core.Packaging;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>
/// The managed resource rewriter (PeImage, Win32ResourceTree, PeResources): checked with its own reader on every OS and,
/// on Windows, against what the loader (LoadLibraryEx/FindResource), GetFileVersionInfo and imagehlp see.
/// </summary>
public partial class PeResourceTests
{
    private static readonly PeResources.VersionInfo Version = new("Contoso Notes", "Contoso Ltd", "Contoso Notes Setup", "2.5.1-beta.3",
                                                                  "Contoso Notes-2.5.1-Setup.exe", "© Contoso Ltd");

    /// <summary>The installer stub when the native runtime is built, and always a managed assembly (also a PE with .rsrc and .reloc).</summary>
    public static TheoryData<string> Executables()
    {
        var data = new TheoryData<string>();
        var stub = Path.Combine(AppContext.BaseDirectory, "runtime", "x64", "FluentSetup.Stub.exe");
        if (File.Exists(stub)) data.Add(stub);
        data.Add(typeof(PeResources).Assembly.Location);
        return data;
    }

    internal static byte[] MakeIco(params (int Size, int Bytes)[] images)
    {
        var header = 6 + 16 * images.Length;
        var ico = new byte[header + images.Sum(i => i.Bytes)];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), (ushort)images.Length);
        var offset = header;
        for (var i = 0; i < images.Length; i++)
        {
            var e = ico.AsSpan(6 + 16 * i);
            e[0] = (byte)(images[i].Size >= 256 ? 0 : images[i].Size);
            e[1] = e[0];
            BinaryPrimitives.WriteUInt16LittleEndian(e[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(e[6..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)images[i].Bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)offset);
            for (var b = 0; b < images[i].Bytes; b++) ico[offset + b] = (byte)(i * 31 + b);
            offset += images[i].Bytes;
        }
        return ico;
    }

    [Theory]
    [MemberData(nameof(Executables))]
    public void ReplacesIconAndVersion(string path)
    {
        var original = File.ReadAllBytes(path);
        var ico = MakeIco((16, 300), (32, 1200), (256, 70_000));
        var updated = PeResources.Apply(original, ico, Version);
        CheckLayout(updated);

        var before = Win32ResourceTree.Read(PeImage.Parse(original));
        var after = Win32ResourceTree.Read(PeImage.Parse(updated));
        var group = after.Find(Win32ResourceTree.RtGroupIcon, 1)!;
        var ids = PeResources.IconGroupImageIds(group).ToList();
        Assert.Equal(3, ids.Count);
        var (images, _) = IconConverter.ToResources(ico, ids[0]);
        for (var i = 0; i < ids.Count; i++) Assert.Equal(images[i], after.Find(Win32ResourceTree.RtIcon, ids[i]));
        Assert.Equal(PeResources.BuildVersionResource(Version), after.Find(Win32ResourceTree.RtVersion, 1));
        Assert.Single(after.OfType(Win32ResourceTree.RtVersion));

        // The old icon images are gone; every other resource is kept byte for byte.
        var oldImages = before.OfType(Win32ResourceTree.RtGroupIcon).Where(e => e.Name == (ResourceId)1).SelectMany(e => PeResources.IconGroupImageIds(e.Data)).ToHashSet();
        Assert.Equal(before.OfType(Win32ResourceTree.RtIcon).Count(e => !oldImages.Contains(e.Name.Id)) + 3, after.OfType(Win32ResourceTree.RtIcon).Count());
        foreach (var e in before.Entries.Where(e => e.Type != (ResourceId)Win32ResourceTree.RtIcon && e.Type != (ResourceId)Win32ResourceTree.RtGroupIcon && e.Type != (ResourceId)Win32ResourceTree.RtVersion))
            Assert.Contains(after.Entries, a => a.Type == e.Type && a.Name == e.Name && a.Language == e.Language && a.Data.AsSpan().SequenceEqual(e.Data));

        // Applying again (e.g. to an already customised stub) works and gives the same file.
        Assert.Equal(updated, PeResources.Apply(updated, ico, Version));

        // Rewritten in place (only .reloc follows .rsrc in MSVC and Roslyn output): no new section, no wasted bytes.
        // Everything outside the resource and relocation sections is unchanged.
        var pe = PeImage.Parse(original);
        Assert.Equal(pe.Sections.Count, PeImage.Parse(updated).Sections.Count);
        Assert.True(updated.Length < original.Length + 80_000);
        var resource = pe.Sections.First(s => s.ContainsRva(pe.Directory(PeImage.DirectoryResource).Rva));
        var text = pe.Sections[0];
        Assert.Equal(original.AsSpan((int)text.PointerToRawData, (int)text.SizeOfRawData).ToArray(), updated.AsSpan((int)text.PointerToRawData, (int)text.SizeOfRawData).ToArray());
        Assert.True(resource.PointerToRawData > text.PointerToRawData);

        if (OperatingSystem.IsWindows()) CheckWithWindows(updated, ico, ids);
    }

    [Theory]
    [MemberData(nameof(Executables))]
    public void AppendsANewSectionWhenTheResourcesCannotGrowInPlace(string path)
    {
        // Another section after .reloc that nothing describes: the rewriter must not move it.
        var original = AddSection(File.ReadAllBytes(path), ".fsxtra", 5000);
        if (original is null) return;  // no room for two more section headers (managed assemblies have 0x200 bytes of headers)
        var updated = PeResources.Apply(original, MakeIco((48, 4000)), Version);
        CheckLayout(updated);
        var before = PeImage.Parse(original);
        var after = PeImage.Parse(updated);
        Assert.Equal(before.Sections.Count + 1, after.Sections.Count);
        Assert.Equal(".rsrc", after.Sections[^1].Name);
        Assert.Equal(after.Sections[^1].VirtualAddress, after.Directory(PeImage.DirectoryResource).Rva);
        for (var i = 0; i < before.Sections.Count; i++)
        {
            Assert.Equal(before.Sections[i].VirtualAddress, after.Sections[i].VirtualAddress);
            Assert.Equal(before.Sections[i].PointerToRawData, after.Sections[i].PointerToRawData);
        }
        Assert.Equal(PeResources.BuildVersionResource(Version), Win32ResourceTree.Read(after).Find(Win32ResourceTree.RtVersion, 1));
        if (OperatingSystem.IsWindows()) CheckWithWindows(updated, MakeIco((48, 4000)), PeResources.IconGroupImageIds(Win32ResourceTree.Read(after).Find(Win32ResourceTree.RtGroupIcon, 1)!).ToList());
    }

    [Fact]
    public void KeepsOverlayAndDropsTheCertificateTable()
    {
        var stub = File.ReadAllBytes(typeof(PeResources).Assembly.Location);
        var overlay = Encoding.ASCII.GetBytes("FLSETUP overlay data 1234");
        var signedLike = stub.Concat(overlay).Concat(new byte[] { 16, 0, 0, 0, 0, 2, 2, 0, 1, 2, 3, 4, 5, 6, 7, 8 }).ToArray();
        var pe = PeImage.Parse(signedLike);
        var security = DirectoryOffset(signedLike, PeImage.DirectorySecurity);
        BinaryPrimitives.WriteUInt32LittleEndian(signedLike.AsSpan(security), (uint)(stub.Length + overlay.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(signedLike.AsSpan(security + 4), 16);
        Assert.NotNull(pe);

        var updated = PeResources.Apply(signedLike, null, Version);
        Assert.Equal((0u, 0u), PeImage.Parse(updated).Directory(PeImage.DirectorySecurity));
        Assert.True(updated.AsSpan(updated.Length - overlay.Length).SequenceEqual(overlay));
    }

    [Fact]
    public void RejectsFilesThatAreNotPe()
    {
        Assert.Throws<InvalidDataException>(() => PeResources.Apply(Encoding.ASCII.GetBytes("MZ but nothing else, really nothing at all here....................."), null, Version));
        Assert.Throws<InvalidDataException>(() => PeResources.Apply(new byte[10], null, Version));
    }

    [Theory]
    [MemberData(nameof(Executables))]
    public void ChecksumMatchesImagehlp(string path)
    {
        var bytes = PeResources.Apply(File.ReadAllBytes(path), null, Version);
        var pe = PeImage.Parse(bytes);
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pe.ChecksumOffset)), PeImage.ComputeChecksum(bytes, pe.ChecksumOffset));
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "c.exe");
        File.WriteAllBytes(file, bytes);
        Assert.Equal(0u, MapFileAndCheckSumW(file, out var header, out var computed));
        Assert.Equal(computed, header);
        Assert.Equal(computed, PeImage.ComputeChecksum(bytes, pe.ChecksumOffset));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static int DirectoryOffset(byte[] pe, int index)
    {
        var opt = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C)) + 24;
        return opt + (BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(opt)) == 0x20B ? 112 : 96) + 8 * index;
    }

    /// <summary>Structural rules Windows' loader relies on.</summary>
    private static void CheckLayout(byte[] image)
    {
        var pe = PeImage.Parse(image);
        uint previousEnd = 0;
        foreach (var s in pe.Sections)
        {
            Assert.Equal(0u, s.VirtualAddress % pe.SectionAlignment);
            Assert.True(s.VirtualAddress >= previousEnd, $"{s.Name} overlaps the previous section");
            if (s.SizeOfRawData > 0)
            {
                Assert.Equal(0u, s.PointerToRawData % pe.FileAlignment);
                Assert.Equal(0u, s.SizeOfRawData % pe.FileAlignment);
                Assert.True(s.PointerToRawData + s.SizeOfRawData <= image.Length);
            }
            previousEnd = PeImage.Align(s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData), pe.SectionAlignment);
        }
        var sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(pe.OptionalHeaderOffset + 56));
        Assert.Equal(previousEnd, sizeOfImage);
        var (rva, size) = pe.Directory(PeImage.DirectoryResource);
        var section = pe.Sections.Single(s => s.ContainsRva(rva));
        Assert.True(rva + size <= section.VirtualAddress + section.VirtualSize);
        var (relocRva, relocSize) = pe.Directory(PeImage.DirectoryBaseRelocation);
        if (relocRva != 0) Assert.True(pe.ReadRva(relocRva, relocSize).Length == relocSize);
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(pe.ChecksumOffset)), PeImage.ComputeChecksum(image, pe.ChecksumOffset));
    }

    /// <summary>Adds a section with raw data after the last one (as some linkers and tools do).</summary>
    private static byte[]? AddSection(byte[] image, string name, int size)
    {
        var pe = PeImage.Parse(image);
        if (pe.SectionTableOffset + 40 * (pe.Sections.Count + 2) > pe.SizeOfHeaders) return null;
        var last = pe.Sections[^1];
        var va = PeImage.Align(last.VirtualAddress + Math.Max(last.VirtualSize, last.SizeOfRawData), pe.SectionAlignment);
        var raw = (int)PeImage.Align((uint)image.Length, pe.FileAlignment);
        var rawSize = (int)PeImage.Align((uint)size, pe.FileAlignment);
        var result = new byte[raw + rawSize];
        image.CopyTo(result, 0);
        for (var i = 0; i < size; i++) result[raw + i] = (byte)(i % 251);
        var h = result.AsSpan(pe.SectionTableOffset + 40 * pe.Sections.Count, 40);
        Encoding.ASCII.GetBytes(name).CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], va);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)raw);
        BinaryPrimitives.WriteUInt32LittleEndian(h[36..], 0x40000040);
        var coff = pe.OptionalHeaderOffset - 20;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(coff + 2), (ushort)(pe.Sections.Count + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pe.OptionalHeaderOffset + 56), PeImage.Align(va + (uint)size, pe.SectionAlignment));
        return result;
    }

    /// <summary>What Windows itself reads from the rewritten file.</summary>
    private static void CheckWithWindows(byte[] image, byte[] ico, List<ushort> iconIds)
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "Setup.exe");
        File.WriteAllBytes(file, image);

        // Mapped as an image: the loader accepts the section layout (no code runs).
        var mapped = LoadLibraryExW(file, IntPtr.Zero, 0x00000001 /* DONT_RESOLVE_DLL_REFERENCES */);
        Assert.True(mapped != IntPtr.Zero, $"LoadLibraryEx (image) failed: {Marshal.GetLastPInvokeError()}");
        FreeLibrary(mapped);

        var module = LoadLibraryExW(file, IntPtr.Zero, 0x00000002 /* AS_DATAFILE */ | 0x00000020 /* AS_IMAGE_RESOURCE */);
        Assert.True(module != IntPtr.Zero, $"LoadLibraryEx (resources) failed: {Marshal.GetLastPInvokeError()}");
        try
        {
            var (images, group) = IconConverter.ToResources(ico, iconIds[0]);
            Assert.Equal(group, LoadNative(module, 14, 1));
            for (var i = 0; i < iconIds.Count; i++) Assert.Equal(images[i], LoadNative(module, 3, iconIds[i]));
            Assert.Equal(PeResources.BuildVersionResource(Version), LoadNative(module, 16, 1));
        }
        finally
        {
            FreeLibrary(module);
        }

        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);  // GetFileVersionInfo on Windows
        Assert.Equal("Contoso Notes", info.ProductName);
        Assert.Equal("Contoso Ltd", info.CompanyName);
        Assert.Equal("Contoso Notes Setup", info.FileDescription);
        Assert.Equal("2.5.1-beta.3", info.ProductVersion);
        Assert.Equal("2.5.1.0", info.FileVersion);
        Assert.Equal("Contoso Notes-2.5.1-Setup.exe", info.OriginalFilename);
        Assert.Equal("© Contoso Ltd", info.LegalCopyright);
        Assert.Equal(2, info.FileMajorPart);
        Assert.Equal(5, info.FileMinorPart);
        Assert.Equal(1, info.FileBuildPart);
    }

    private static byte[] LoadNative(IntPtr module, int type, int name)
    {
        var res = FindResourceW(module, name, type);
        Assert.True(res != IntPtr.Zero, $"FindResource({type}, {name}) failed");
        var size = SizeofResource(module, res);
        var data = LockResource(LoadResource(module, res));
        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, (int)size);
        return bytes;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr module);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr FindResourceW(IntPtr module, nint name, nint type);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LoadResource(IntPtr module, IntPtr resource);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LockResource(IntPtr data);

    [LibraryImport("kernel32.dll")]
    private static partial uint SizeofResource(IntPtr module, IntPtr resource);

    [LibraryImport("imagehlp.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint MapFileAndCheckSumW(string file, out uint headerSum, out uint checkSum);
}
