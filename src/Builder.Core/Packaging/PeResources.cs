using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>Replaces the icon group and version resource of the (unsigned) stub before the package is appended.</summary>
public static partial class PeResources
{
    private const ushort LangNeutral = 0;
    private static readonly IntPtr RtIcon = 3, RtGroupIcon = 14, RtVersion = 16;

    [LibraryImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr BeginUpdateResource(string fileName, [MarshalAs(UnmanagedType.Bool)] bool deleteExisting);

    [LibraryImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[]? data, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndUpdateResource(IntPtr update, [MarshalAs(UnmanagedType.Bool)] bool discard);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr module);

    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesW", SetLastError = true)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr param);

    public sealed record VersionInfo(string ProductName, string CompanyName, string FileDescription, string ProductVersion,
                                     string OriginalFilename, string Copyright);

    public static void Apply(string exePath, byte[]? icoFile, VersionInfo version)
    {
        var existingIcons = ListIntegerNames(exePath, RtIcon);
        var handle = BeginUpdateResource(exePath, false);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "BeginUpdateResource failed");
        try
        {
            if (icoFile is not null)
            {
                // Drop the stub's default icon images, then add the new ones under fresh ids and point group 1 at them.
                foreach (var id in existingIcons) Check(UpdateResource(handle, RtIcon, id, LangNeutral, null, 0), "remove icon");
                var (images, group) = IconConverter.ToResources(icoFile, firstId: 1);
                for (var i = 0; i < images.Count; i++)
                {
                    Check(UpdateResource(handle, RtIcon, (IntPtr)(i + 1), LangNeutral, images[i], (uint)images[i].Length), "add icon image");
                }
                Check(UpdateResource(handle, RtGroupIcon, (IntPtr)1, LangNeutral, group, (uint)group.Length), "replace icon group");
            }
            var ver = BuildVersionResource(version);
            Check(UpdateResource(handle, RtVersion, (IntPtr)1, LangNeutral, ver, (uint)ver.Length), "replace version resource");
        }
        catch
        {
            EndUpdateResource(handle, true);  // discard
            throw;
        }
        if (!EndUpdateResource(handle, false)) throw new Win32Exception(Marshal.GetLastWin32Error(), "EndUpdateResource failed");
    }

    private static void Check(bool ok, string what)
    {
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), $"UpdateResource failed ({what})");
    }

    private static List<IntPtr> ListIntegerNames(string exePath, IntPtr type)
    {
        var names = new List<IntPtr>();
        var module = LoadLibraryEx(exePath, IntPtr.Zero, 0x00000002 /*LOAD_LIBRARY_AS_DATAFILE*/ | 0x00000020 /*AS_IMAGE_RESOURCE*/);
        if (module == IntPtr.Zero) return names;
        try
        {
            EnumResourceNames(module, type, (_, _, name, _) =>
            {
                if ((ulong)name <= 0xFFFF) names.Add(name);
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            FreeLibrary(module);
        }
        return names;
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
