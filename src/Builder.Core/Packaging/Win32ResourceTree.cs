using System.Buffers.Binary;
using System.Text;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>A resource type or name: an integer id or a string (strings sort first, as the loader expects).</summary>
internal readonly record struct ResourceId(ushort Id, string? Name) : IComparable<ResourceId>
{
    public static implicit operator ResourceId(ushort id) => new(id, null);

    public int CompareTo(ResourceId other) => (Name, other.Name) switch
    {
        (not null, not null) => string.CompareOrdinal(Name, other.Name),
        (not null, null) => -1,
        (null, not null) => 1,
        _ => Id.CompareTo(other.Id),
    };

    public override string ToString() => Name ?? Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed record ResourceEntry(ResourceId Type, ResourceId Name, ushort Language, byte[] Data, uint CodePage = 0);

/// <summary>
/// The resources of a PE file as a flat list of (type, name, language) leaves, read from and written to the standard
/// three-level IMAGE_RESOURCE_DIRECTORY tree.
/// </summary>
internal sealed class Win32ResourceTree
{
    public const ushort RtIcon = 3, RtGroupIcon = 14, RtVersion = 16, RtManifest = 24;
    private const int MaxEntries = 65536;

    public List<ResourceEntry> Entries { get; } = [];

    public IEnumerable<ResourceEntry> OfType(ushort type) => Entries.Where(e => e.Type == (ResourceId)type);

    public byte[]? Find(ushort type, ResourceId name) => Entries.FirstOrDefault(e => e.Type == (ResourceId)type && e.Name == name)?.Data;

    public void Remove(ushort type, ResourceId name) => Entries.RemoveAll(e => e.Type == (ResourceId)type && e.Name == name);

    public void Set(ushort type, ResourceId name, ushort language, byte[] data)
    {
        Entries.RemoveAll(e => e.Type == (ResourceId)type && e.Name == name && e.Language == language);
        Entries.Add(new ResourceEntry(type, name, language, data));
    }

    public static Win32ResourceTree Read(PeImage pe)
    {
        var tree = new Win32ResourceTree();
        var (rootRva, _) = pe.Directory(PeImage.DirectoryResource);
        if (rootRva == 0) return tree;
        var section = pe.Sections.FirstOrDefault(s => s.ContainsRva(rootRva));
        var start = rootRva - (section?.VirtualAddress ?? 0);
        if (section is null || start >= section.SizeOfRawData) throw new InvalidDataException("The resource directory is outside the PE file's data.");
        // Directory tables, data entries and names lie between the root and the end of its section.
        var tables = pe.ReadRva(rootRva, section.SizeOfRawData - start);
        var visited = new HashSet<uint>();

        void ReadDirectory(ReadOnlySpan<byte> t, uint offset, int level, ResourceId type, ResourceId name)
        {
            if (!visited.Add(offset) || offset + 16L > t.Length) throw new InvalidDataException("The resource directory is damaged.");
            int count = BinaryPrimitives.ReadUInt16LittleEndian(t[(int)(offset + 12)..]) + BinaryPrimitives.ReadUInt16LittleEndian(t[(int)(offset + 14)..]);
            if (offset + 16L + 8L * count > t.Length || visited.Count + count > MaxEntries) throw new InvalidDataException("The resource directory is damaged.");
            for (var i = 0; i < count; i++)
            {
                var e = (int)offset + 16 + 8 * i;
                var nameField = BinaryPrimitives.ReadUInt32LittleEndian(t[e..]);
                var dataField = BinaryPrimitives.ReadUInt32LittleEndian(t[(e + 4)..]);
                ResourceId id;
                if ((nameField & 0x80000000) != 0)
                {
                    var at = nameField & 0x7FFFFFFF;
                    if (at + 2L > t.Length) throw new InvalidDataException("The resource directory is damaged.");
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(t[(int)at..]);
                    if (at + 2L + 2L * length > t.Length) throw new InvalidDataException("The resource directory is damaged.");
                    id = new ResourceId(0, Encoding.Unicode.GetString(t.Slice((int)at + 2, 2 * length)));
                }
                else
                {
                    if (nameField > 0xFFFF) throw new InvalidDataException("The resource directory is damaged.");
                    id = (ushort)nameField;
                }
                var subdirectory = (dataField & 0x80000000) != 0;
                if (subdirectory != (level < 2)) throw new InvalidDataException("Unsupported resource directory layout (expected type, name and language levels).");
                if (level == 0) ReadDirectory(t, dataField & 0x7FFFFFFF, 1, id, default);
                else if (level == 1) ReadDirectory(t, dataField & 0x7FFFFFFF, 2, type, id);
                else
                {
                    if (id.Name is not null || dataField + 16L > t.Length) throw new InvalidDataException("The resource directory is damaged.");
                    var rva = BinaryPrimitives.ReadUInt32LittleEndian(t[(int)dataField..]);
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(t[(int)(dataField + 4)..]);
                    var codePage = BinaryPrimitives.ReadUInt32LittleEndian(t[(int)(dataField + 8)..]);
                    tree.Entries.Add(new ResourceEntry(type, name, id.Id, pe.ReadRva(rva, size).ToArray(), codePage));
                }
            }
        }

        ReadDirectory(tables, 0, 0, default, default);
        return tree;
    }

    /// <summary>
    /// Serializes the tree as a resource section starting at <paramref name="sectionRva"/>: directory tables (root, types,
    /// names), data entries, name strings, then the data (8-byte aligned). Entries are sorted (names first, ordinal; then
    /// ids ascending) because the loader binary-searches them.
    /// </summary>
    public byte[] Serialize(uint sectionRva)
    {
        var types = Entries.GroupBy(e => e.Type).OrderBy(g => g.Key)
            .Select(g => (Type: g.Key, Names: g.GroupBy(e => e.Name).OrderBy(n => n.Key).Select(n => (Name: n.Key, Leaves: n.OrderBy(e => e.Language).ToList())).ToList()))
            .ToList();
        foreach (var t in types)
        {
            foreach (var n in t.Names)
            {
                if (n.Leaves.Select(l => l.Language).Distinct().Count() != n.Leaves.Count)
                    throw new InvalidOperationException($"Duplicate resource {t.Type}/{n.Name}.");
            }
        }

        var leafCount = types.Sum(t => t.Names.Sum(n => n.Leaves.Count));
        var directoryBytes = 16 + 8 * types.Count + types.Sum(t => 16 + 8 * t.Names.Count) + types.Sum(t => t.Names.Sum(n => 16 + 8 * n.Leaves.Count));
        var dataEntriesAt = directoryBytes;
        var stringsAt = dataEntriesAt + 16 * leafCount;
        var strings = new Dictionary<string, int>(StringComparer.Ordinal);
        var stringBytes = 0;
        foreach (var s in types.SelectMany(t => t.Names.Select(n => n.Name).Prepend(t.Type)).Where(r => r.Name is not null).Select(r => r.Name!))
        {
            if (strings.ContainsKey(s)) continue;
            strings[s] = stringsAt + stringBytes;
            stringBytes += 2 + 2 * s.Length;
        }
        var dataAt = Align8(stringsAt + stringBytes);
        var total = dataAt;
        foreach (var leaf in types.SelectMany(t => t.Names.SelectMany(n => n.Leaves))) total = Align8(total + leaf.Data.Length);

        var b = new byte[total];
        foreach (var (s, at) in strings)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)s.Length);
            Encoding.Unicode.GetBytes(s).CopyTo(b, at + 2);
        }

        var typeDirAt = 16 + 8 * types.Count;
        var nameDirAt = typeDirAt + types.Sum(t => 16 + 8 * t.Names.Count);
        var dataEntry = dataEntriesAt;
        var data = dataAt;
        WriteDirectoryHeader(b, 0, types.Select(t => t.Type));
        for (var ti = 0; ti < types.Count; ti++)
        {
            var (type, names) = types[ti];
            WriteEntry(b, 16 + 8 * ti, type, strings, 0x80000000u | (uint)typeDirAt);
            WriteDirectoryHeader(b, typeDirAt, names.Select(n => n.Name));
            for (var ni = 0; ni < names.Count; ni++)
            {
                var (name, leaves) = names[ni];
                WriteEntry(b, typeDirAt + 16 + 8 * ni, name, strings, 0x80000000u | (uint)nameDirAt);
                WriteDirectoryHeader(b, nameDirAt, leaves.Select(l => (ResourceId)l.Language));
                for (var li = 0; li < leaves.Count; li++)
                {
                    var leaf = leaves[li];
                    WriteEntry(b, nameDirAt + 16 + 8 * li, leaf.Language, strings, (uint)dataEntry);
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(dataEntry), sectionRva + (uint)data);
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(dataEntry + 4), (uint)leaf.Data.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(dataEntry + 8), leaf.CodePage);
                    leaf.Data.CopyTo(b, data);
                    dataEntry += 16;
                    data = Align8(data + leaf.Data.Length);
                }
                nameDirAt += 16 + 8 * leaves.Count;
            }
            typeDirAt += 16 + 8 * names.Count;
        }
        return b;
    }

    private static void WriteDirectoryHeader(byte[] b, int at, IEnumerable<ResourceId> ids)
    {
        var list = ids.ToList();
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 12), (ushort)list.Count(i => i.Name is not null));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 14), (ushort)list.Count(i => i.Name is null));
    }

    private static void WriteEntry(byte[] b, int at, ResourceId id, Dictionary<string, int> strings, uint target)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), id.Name is null ? id.Id : 0x80000000u | (uint)strings[id.Name]);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), target);
    }

    private static int Align8(int v) => (v + 7) & ~7;
}
