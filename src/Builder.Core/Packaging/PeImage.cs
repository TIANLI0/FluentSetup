using System.Buffers.Binary;
using System.Text;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>
/// Minimal PE32/PE32+ reader and writer, enough to replace the resource section of an executable on any OS (the
/// builder runs on Linux and macOS too, where kernel32's UpdateResource does not exist).
/// <para>
/// The new resource section takes the place of the old one. Only sections that nothing else refers to may follow it
/// (the base relocation table, which is moved behind it and re-pointed); otherwise the new section is appended after
/// the last one and the old bytes stay unused. Section alignment, SizeOfImage, the data directories and the checksum
/// are kept consistent; an Authenticode certificate table is dropped (it would no longer match).
/// </para>
/// </summary>
internal sealed class PeImage
{
    public const int DirectoryResource = 2, DirectorySecurity = 4, DirectoryBaseRelocation = 5, DirectoryDebug = 6;
    private const int SectionHeaderSize = 40;
    private const uint ScnInitializedData = 0x00000040, ScnMemRead = 0x40000000;

    internal sealed class Section
    {
        public string Name { get; set; } = "";
        public uint VirtualSize { get; set; }
        public uint VirtualAddress { get; set; }
        public uint SizeOfRawData { get; set; }
        public uint PointerToRawData { get; set; }
        public uint Characteristics { get; set; }
        /// <summary>The 40 header bytes as read (relocation/line-number fields are carried over unchanged).</summary>
        public byte[] Header { get; set; } = new byte[SectionHeaderSize];

        public uint VirtualEnd => VirtualAddress + Math.Max(VirtualSize, SizeOfRawData);
        public bool ContainsRva(uint rva) => rva >= VirtualAddress && rva < VirtualEnd;
    }

    public byte[] Data { get; }
    public bool Is64 { get; }
    public int OptionalHeaderOffset { get; }
    public int SectionTableOffset { get; }
    public uint SectionAlignment { get; }
    public uint FileAlignment { get; }
    public uint SizeOfHeaders { get; }
    public int NumberOfDirectories { get; }
    public List<Section> Sections { get; } = [];

    private int DataDirectoryOffset => OptionalHeaderOffset + (Is64 ? 112 : 96);
    public int ChecksumOffset => OptionalHeaderOffset + 64;

    private PeImage(byte[] data)
    {
        Data = data;
        if (data.Length < 0x40 || data[0] != 'M' || data[1] != 'Z') throw new InvalidDataException("Not a PE file (no MZ header).");
        var pe = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C));
        if (pe < 4 || pe > data.Length - 24 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pe)) != 0x00004550)
            throw new InvalidDataException("Not a PE file (bad PE signature).");
        var coff = pe + 4;
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(coff + 2));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(coff + 16));
        OptionalHeaderOffset = coff + 20;
        if (OptionalHeaderOffset + 2 > data.Length) throw new InvalidDataException("Truncated PE header.");
        Is64 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(OptionalHeaderOffset)) switch
        {
            0x20B => true,
            0x10B => false,
            _ => throw new InvalidDataException("Unknown PE optional header."),
        };
        var fixedSize = Is64 ? 112 : 96;
        if (optionalSize < fixedSize || OptionalHeaderOffset + optionalSize > data.Length) throw new InvalidDataException("Truncated PE optional header.");
        SectionAlignment = U32(OptionalHeaderOffset + 32);
        FileAlignment = U32(OptionalHeaderOffset + 36);
        SizeOfHeaders = U32(OptionalHeaderOffset + 60);
        NumberOfDirectories = (int)Math.Min(U32(OptionalHeaderOffset + fixedSize - 4), (uint)(optionalSize - fixedSize) / 8);
        if (NumberOfDirectories < DirectoryBaseRelocation + 1) throw new InvalidDataException("The PE file has too few data directories.");
        if (!IsPowerOfTwo(FileAlignment) || FileAlignment < 512 && FileAlignment != SectionAlignment || FileAlignment > 0x10000 ||
            !IsPowerOfTwo(SectionAlignment) || SectionAlignment < FileAlignment)
            throw new InvalidDataException("The PE file has an invalid section or file alignment.");
        SectionTableOffset = OptionalHeaderOffset + optionalSize;
        if (sectionCount is 0 or > 96 || SectionTableOffset + sectionCount * SectionHeaderSize > Math.Min(data.Length, SizeOfHeaders))
            throw new InvalidDataException("The PE section table is invalid.");
        uint previousEnd = 0;
        for (var i = 0; i < sectionCount; i++)
        {
            var h = SectionTableOffset + i * SectionHeaderSize;
            var s = new Section
            {
                Name = Encoding.ASCII.GetString(data, h, 8).TrimEnd('\0'),
                VirtualSize = U32(h + 8),
                VirtualAddress = U32(h + 12),
                SizeOfRawData = U32(h + 16),
                PointerToRawData = U32(h + 20),
                Characteristics = U32(h + 36),
                Header = data.AsSpan(h, SectionHeaderSize).ToArray(),
            };
            if (s.SizeOfRawData > 0 && (long)s.PointerToRawData + s.SizeOfRawData > data.Length)
                throw new InvalidDataException($"PE section {s.Name} lies outside the file.");
            if (s.VirtualAddress < previousEnd || s.VirtualAddress % SectionAlignment != 0)
                throw new InvalidDataException("PE sections are not in ascending, aligned order.");
            previousEnd = s.VirtualEnd;
            Sections.Add(s);
        }
    }

    public static PeImage Parse(byte[] data) => new(data);

    public (uint Rva, uint Size) Directory(int index) =>
        index < NumberOfDirectories ? (U32(DataDirectoryOffset + 8 * index), U32(DataDirectoryOffset + 8 * index + 4)) : (0, 0);

    /// <summary>The bytes at an RVA, which must lie in the raw data of one section.</summary>
    public ReadOnlySpan<byte> ReadRva(uint rva, uint length)
    {
        foreach (var s in Sections)
        {
            if (!s.ContainsRva(rva)) continue;
            var delta = rva - s.VirtualAddress;
            if ((ulong)delta + length > s.SizeOfRawData) break;
            return Data.AsSpan((int)(s.PointerToRawData + delta), (int)length);
        }
        throw new InvalidDataException($"RVA 0x{rva:X} (+{length}) is outside the PE file's data.");
    }

    /// <summary>Returns a copy of the image with the resources replaced by <paramref name="resources"/>.</summary>
    public byte[] WithResources(Win32ResourceTree resources)
    {
        var (resourceRva, _) = Directory(DirectoryResource);
        var resourceIndex = resourceRva == 0 ? -1 : Sections.FindIndex(s => s.ContainsRva(resourceRva));
        if (resourceRva != 0 && resourceIndex < 0) throw new InvalidDataException("The resource directory is outside every PE section.");

        var rawEnd = Math.Max(SizeOfHeaders, Sections.Where(s => s.SizeOfRawData > 0).Select(s => s.PointerToRawData + s.SizeOfRawData).DefaultIfEmpty(0u).Max());
        var overlay = Overlay(rawEnd);
        var sections = Sections.Select(Clone).ToList();
        var initializedDataDelta = 0L;
        using var output = new MemoryStream();
        uint newResourceRva;
        byte[] resourceBytes;
        var relocation = Directory(DirectoryBaseRelocation);

        if (resourceIndex >= 0 && CanRewriteInPlace(resourceIndex))
        {
            // Same start, new size; the sections after it (only .reloc) move up behind it.
            var res = sections[resourceIndex];
            newResourceRva = res.VirtualAddress;
            resourceBytes = resources.Serialize(newResourceRva);
            output.Write(Data, 0, (int)res.PointerToRawData);
            var oldRaw = res.SizeOfRawData;
            res.VirtualSize = (uint)resourceBytes.Length;
            res.SizeOfRawData = Align((uint)resourceBytes.Length, FileAlignment);
            output.Write(resourceBytes);
            PadTo(output, FileAlignment);
            if ((res.Characteristics & ScnInitializedData) != 0) initializedDataDelta = (long)res.SizeOfRawData - oldRaw;
            var nextVa = Align(res.VirtualAddress + res.VirtualSize, SectionAlignment);
            for (var i = resourceIndex + 1; i < sections.Count; i++)
            {
                var s = sections[i];
                var old = Sections[i];
                s.VirtualAddress = nextVa;
                s.PointerToRawData = s.SizeOfRawData == 0 ? 0 : (uint)output.Length;
                if (s.SizeOfRawData > 0)
                {
                    output.Write(Data, (int)old.PointerToRawData, (int)old.SizeOfRawData);
                    PadTo(output, FileAlignment);
                }
                if (relocation.Rva != 0 && old.ContainsRva(relocation.Rva)) relocation.Rva = relocation.Rva - old.VirtualAddress + s.VirtualAddress;
                nextVa = Align(s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData), SectionAlignment);
            }
        }
        else
        {
            // Append a new section; everything else stays where it is.
            var headerEnd = SectionTableOffset + (sections.Count + 1) * SectionHeaderSize;
            var firstRaw = sections.Where(s => s.SizeOfRawData > 0).Select(s => s.PointerToRawData).DefaultIfEmpty(SizeOfHeaders).Min();
            if (headerEnd > SizeOfHeaders || headerEnd > firstRaw) throw new InvalidDataException("The PE header has no room for another section.");
            var last = sections[^1];
            newResourceRva = Align(last.VirtualAddress + Math.Max(last.VirtualSize, last.SizeOfRawData), SectionAlignment);
            resourceBytes = resources.Serialize(newResourceRva);
            output.Write(Data, 0, (int)rawEnd);
            PadTo(output, FileAlignment);
            var res = new Section
            {
                Name = ".rsrc",
                VirtualAddress = newResourceRva,
                VirtualSize = (uint)resourceBytes.Length,
                PointerToRawData = (uint)output.Length,
                SizeOfRawData = Align((uint)resourceBytes.Length, FileAlignment),
                Characteristics = ScnInitializedData | ScnMemRead,
            };
            output.Write(resourceBytes);
            PadTo(output, FileAlignment);
            sections.Add(res);
            initializedDataDelta = res.SizeOfRawData;
        }
        output.Write(overlay);

        var result = output.ToArray();
        var span = result.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span[(OptionalHeaderOffset - 20 + 2)..], (ushort)sections.Count);
        for (var i = 0; i < sections.Count; i++) WriteSectionHeader(span.Slice(SectionTableOffset + i * SectionHeaderSize, SectionHeaderSize), sections[i]);
        var lastSection = sections[^1];
        BinaryPrimitives.WriteUInt32LittleEndian(span[(OptionalHeaderOffset + 56)..],
                                                 Align(lastSection.VirtualAddress + Math.Max(lastSection.VirtualSize, lastSection.SizeOfRawData), SectionAlignment));
        var initialized = (long)U32(OptionalHeaderOffset + 8) + initializedDataDelta;
        BinaryPrimitives.WriteUInt32LittleEndian(span[(OptionalHeaderOffset + 8)..], (uint)Math.Clamp(initialized, 0, uint.MaxValue));
        WriteDirectory(span, DirectoryResource, newResourceRva, (uint)resourceBytes.Length);
        WriteDirectory(span, DirectoryBaseRelocation, relocation.Rva, relocation.Size);
        WriteDirectory(span, DirectorySecurity, 0, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[ChecksumOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[ChecksumOffset..], ComputeChecksum(result, ChecksumOffset));
        return result;
    }

    /// <summary>
    /// The resource section can be rewritten where it is if it is followed (in memory and in the file) only by the base
    /// relocation table, nothing but the resource, relocation and security directories points into that range, no
    /// debug data lives there, and no relocation fixes up an address in it.
    /// </summary>
    private bool CanRewriteInPlace(int resourceIndex)
    {
        var res = Sections[resourceIndex];
        var relocation = Directory(DirectoryBaseRelocation);
        for (var i = 0; i < Sections.Count; i++)
        {
            var s = Sections[i];
            if (i < resourceIndex && s.SizeOfRawData > 0 && s.PointerToRawData + s.SizeOfRawData > res.PointerToRawData) return false;
            if (i <= resourceIndex) continue;
            if (relocation.Rva == 0 || !s.ContainsRva(relocation.Rva) || relocation.Rva + relocation.Size > s.VirtualEnd) return false;
            if (s.SizeOfRawData > 0 && s.PointerToRawData < res.PointerToRawData + res.SizeOfRawData) return false;
        }
        if (Sections.Count - resourceIndex > 2) return false;
        for (var d = 0; d < NumberOfDirectories; d++)
        {
            if (d is DirectoryResource or DirectorySecurity or DirectoryBaseRelocation) continue;
            var (rva, size) = Directory(d);
            if (rva != 0 && (ulong)rva + Math.Max(size, 1u) > res.VirtualAddress) return false;
        }
        var debug = Directory(DirectoryDebug);
        if (debug.Rva != 0)
        {
            var entries = ReadRva(debug.Rva, debug.Size);
            for (var o = 0; o + 28 <= entries.Length; o += 28)
            {
                var address = BinaryPrimitives.ReadUInt32LittleEndian(entries[(o + 20)..]);
                var pointer = BinaryPrimitives.ReadUInt32LittleEndian(entries[(o + 24)..]);
                if (address >= res.VirtualAddress || pointer >= res.PointerToRawData) return false;
            }
        }
        if (relocation.Rva != 0)
        {
            var blocks = ReadRva(relocation.Rva, relocation.Size);
            for (var o = 0; o + 8 <= blocks.Length;)
            {
                var page = BinaryPrimitives.ReadUInt32LittleEndian(blocks[o..]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(blocks[(o + 4)..]);
                if (size < 8 || page + 0x1000 > res.VirtualAddress && size > 8) return false;
                o += (int)size;
            }
        }
        return true;
    }

    /// <summary>Bytes after the last section (installer data and the like) without an Authenticode certificate table.</summary>
    private byte[] Overlay(uint rawEnd)
    {
        if (rawEnd >= Data.Length) return [];
        var (certOffset, certSize) = Directory(DirectorySecurity);  // a file offset, not an RVA
        var overlay = Data.AsSpan((int)rawEnd);
        if (certOffset < rawEnd || certSize == 0 || (long)certOffset + certSize > Data.Length) return overlay.ToArray();
        var before = Data.AsSpan((int)rawEnd, (int)(certOffset - rawEnd));
        var after = Data.AsSpan((int)(certOffset + certSize));
        return [.. before, .. after];
    }

    private void WriteDirectory(Span<byte> image, int index, uint rva, uint size)
    {
        if (index >= NumberOfDirectories) return;
        BinaryPrimitives.WriteUInt32LittleEndian(image[(DataDirectoryOffset + 8 * index)..], rva);
        BinaryPrimitives.WriteUInt32LittleEndian(image[(DataDirectoryOffset + 8 * index + 4)..], size);
    }

    private static void WriteSectionHeader(Span<byte> h, Section s)
    {
        s.Header.CopyTo(h);
        h[..8].Clear();
        Encoding.ASCII.GetBytes(s.Name.Length > 8 ? s.Name[..8] : s.Name).CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], s.VirtualSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], s.VirtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], s.SizeOfRawData);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], s.PointerToRawData);
        BinaryPrimitives.WriteUInt32LittleEndian(h[36..], s.Characteristics);
    }

    private static Section Clone(Section s) => new()
    {
        Name = s.Name, VirtualSize = s.VirtualSize, VirtualAddress = s.VirtualAddress, SizeOfRawData = s.SizeOfRawData,
        PointerToRawData = s.PointerToRawData, Characteristics = s.Characteristics, Header = s.Header,
    };

    /// <summary>The PE checksum (as imagehlp's CheckSumMappedFile computes it), with the checksum field taken as zero.</summary>
    public static uint ComputeChecksum(ReadOnlySpan<byte> image, int checksumOffset)
    {
        ulong sum = 0;
        var i = 0;
        for (; i + 1 < image.Length; i += 2)
        {
            if (i >= checksumOffset && i < checksumOffset + 4) continue;
            sum += BinaryPrimitives.ReadUInt16LittleEndian(image[i..]);
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        if (i < image.Length) sum += image[i];
        sum = (sum & 0xFFFF) + (sum >> 16);
        sum = (sum & 0xFFFF) + (sum >> 16);
        return (uint)(sum + (ulong)image.Length);
    }

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(offset));
    private static bool IsPowerOfTwo(uint v) => v != 0 && (v & (v - 1)) == 0;
    internal static uint Align(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static void PadTo(Stream s, uint alignment)
    {
        var pad = (int)((alignment - s.Length % alignment) % alignment);
        if (pad > 0) s.Write(new byte[pad]);
    }
}
