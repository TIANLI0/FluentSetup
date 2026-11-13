using System.Buffers.Binary;
using System.Text;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>
/// Replaces the icon group and version resource of the (unsigned) stub before the package is appended. Managed, so it
/// works the same on Windows, Linux and macOS (see <see cref="PeImage"/>).
/// </summary>
public static class PeResources
{
    private const ushort LangNeutral = 0;
    private const ushort IconGroupId = 1;   // IDI_APP in src/Bootstrapper/resource.h
    private const ushort VersionId = 1;     // VS_VERSION_INFO

    public sealed record VersionInfo(string ProductName, string CompanyName, string FileDescription, string ProductVersion,
                                     string OriginalFilename, string Copyright);

    public static void Apply(string exePath, byte[]? icoFile, VersionInfo version) =>
        File.WriteAllBytes(exePath, Apply(File.ReadAllBytes(exePath), icoFile, version));

    /// <summary>Returns the executable with icon group 1 (and its images) and version resource 1 replaced.</summary>
    public static byte[] Apply(byte[] exe, byte[]? icoFile, VersionInfo version)
    {
        var pe = PeImage.Parse(exe);
        var tree = Win32ResourceTree.Read(pe);
        if (icoFile is not null)
        {
            // Drop the images of the old icon group, then add the new ones under ids no other group uses.
            var oldImages = tree.OfType(Win32ResourceTree.RtGroupIcon).Where(e => e.Name == (ResourceId)IconGroupId)
                                .SelectMany(e => IconGroupImageIds(e.Data)).ToHashSet();
            tree.Entries.RemoveAll(e => e.Type == (ResourceId)Win32ResourceTree.RtIcon && e.Name.Name is null && oldImages.Contains(e.Name.Id));
            tree.Remove(Win32ResourceTree.RtGroupIcon, IconGroupId);
            var firstId = tree.OfType(Win32ResourceTree.RtIcon).Where(e => e.Name.Name is null).Select(e => e.Name.Id + 1).DefaultIfEmpty(1).Max();
            var (images, group) = IconConverter.ToResources(icoFile, firstId);
            if (firstId + images.Count > ushort.MaxValue) throw new InvalidDataException("The executable has too many icons.");
            for (var i = 0; i < images.Count; i++) tree.Set(Win32ResourceTree.RtIcon, (ushort)(firstId + i), LangNeutral, images[i]);
            tree.Set(Win32ResourceTree.RtGroupIcon, IconGroupId, LangNeutral, group);
        }
        tree.Remove(Win32ResourceTree.RtVersion, VersionId);
        tree.Set(Win32ResourceTree.RtVersion, VersionId, LangNeutral, BuildVersionResource(version));
        return pe.WithResources(tree);
    }

    /// <summary>The RT_ICON ids an RT_GROUP_ICON directory refers to.</summary>
    internal static IEnumerable<ushort> IconGroupImageIds(byte[] group)
    {
        if (group.Length < 6) yield break;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4));
        for (var i = 0; i < count && 6 + 14 * (i + 1) <= group.Length; i++)
            yield return BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(6 + 14 * i + 12));
    }

    // ---- VS_VERSIONINFO ----------------------------------------------------------------------------------------

    public static (ushort, ushort, ushort, ushort) NumericVersion(string version)
    {
        var core = version.Split('-')[0].Split('.');
        ushort Part(int i) => i < core.Length && ushort.TryParse(core[i], out var v) ? v : (ushort)0;
        return (Part(0), Part(1), Part(2), Part(3));
    }

    internal static byte[] BuildVersionResource(VersionInfo v)
    {
        var (a, b, c, d) = NumericVersion(v.ProductVersion);
        var fixedInfo = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(0), 0xFEEF04BD);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(8), (uint)(a << 16 | b));
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(12), (uint)(c << 16 | d));
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(16), (uint)(a << 16 | b));
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(20), (uint)(c << 16 | d));
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(24), 0x3F);   // flags mask
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(28), 0);      // flags
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(32), 0x40004); // VOS_NT_WINDOWS32
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(36), 1);      // VFT_APP

        var fileVersion = $"{a}.{b}.{c}.{d}";
        var strings = new List<Node>
        {
            Text("CompanyName", v.CompanyName),
            Text("FileDescription", v.FileDescription),
            Text("FileVersion", fileVersion),
            Text("InternalName", "Setup"),
            Text("LegalCopyright", v.Copyright),
            Text("OriginalFilename", v.OriginalFilename),
            Text("ProductName", v.ProductName),
            Text("ProductVersion", v.ProductVersion),
        };
        var stringTable = new Node("000004b0", null, true, strings);
        var stringFileInfo = new Node("StringFileInfo", null, true, [stringTable]);
        var translation = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(translation, 0x0000);
        BinaryPrimitives.WriteUInt16LittleEndian(translation.AsSpan(2), 1200);
        var varFileInfo = new Node("VarFileInfo", null, true, [new Node("Translation", translation, false, [])]);
        var root = new Node("VS_VERSION_INFO", fixedInfo, false, [stringFileInfo, varFileInfo]);
        return root.Serialize();

        static Node Text(string key, string value) => new(key, Encoding.Unicode.GetBytes((string.IsNullOrEmpty(value) ? " " : value) + "\0"), true, []) { TextValue = true };
    }

    private sealed class Node(string key, byte[]? value, bool textType, List<Node> children)
    {
        public bool TextValue { get; init; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((ushort)0); // length placeholder
            var valueLength = value is null ? 0 : TextValue ? value.Length / 2 : value.Length;
            w.Write((ushort)valueLength);
            w.Write((ushort)(textType || TextValue ? 1 : 0));
            w.Write(Encoding.Unicode.GetBytes(key + "\0"));
            Pad(w);
            if (value is not null)
            {
                w.Write(value);
                Pad(w);
            }
            foreach (var child in children)
            {
                Pad(w);
                w.Write(child.Serialize());
            }
            w.Flush();
            var bytes = ms.ToArray();
            // wLength excludes trailing padding after the last child per spec; we include exactly what we wrote.
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)bytes.Length);
            return bytes;
        }

        private static void Pad(BinaryWriter w)
        {
            while (w.BaseStream.Position % 4 != 0) w.Write((byte)0);
        }
    }
}
