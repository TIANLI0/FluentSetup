using System.Buffers.Binary;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>.ico / .png handling for the installer icon.</summary>
public static class IconConverter
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Loads an .ico, or wraps a .png (≤ 256 px) into a single-image .ico (PNG-compressed icons are valid
    /// since Windows Vista).</summary>
    public static byte[] LoadAsIco(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(PngSignature))
        {
            var (w, h) = PngSize(bytes);
            if (w > 256 || h > 256 || w == 0 || h == 0) throw new InvalidDataException($"PNG icons must be at most 256×256 pixels ({w}×{h}).");
            var ico = new byte[6 + 16 + bytes.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);
            ico[6] = (byte)(w >= 256 ? 0 : w);
            ico[7] = (byte)(h >= 256 ? 0 : h);
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(10), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(12), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), 22);
            bytes.CopyTo(ico, 22);
            return ico;
        }
        Validate(bytes);
        return bytes;
    }

    private static (int W, int H) PngSize(byte[] png) =>
        png.Length < 24 ? (0, 0) : (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

    public static void Validate(byte[] ico)
    {
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) != 1)
            throw new InvalidDataException("Not a valid .ico file.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        if (count == 0 || count > 64 || ico.Length < 6 + 16 * count) throw new InvalidDataException("The .ico file has no usable images.");
        for (var i = 0; i < count; i++)
        {
            var e = ico.AsSpan(6 + 16 * i);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (size == 0 || offset + (long)size > ico.Length) throw new InvalidDataException("The .ico file is truncated.");
        }
    }

    /// <summary>Splits an .ico into RT_ICON images and the RT_GROUP_ICON directory referencing them.</summary>
    public static (List<byte[]> Images, byte[] Group) ToResources(byte[] ico, int firstId)
    {
        Validate(ico);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        var images = new List<byte[]>();
        var group = new byte[6 + 14 * count];
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(4), count);
        for (var i = 0; i < count; i++)
        {
            var e = ico.AsSpan(6 + 16 * i, 16);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            images.Add(ico.AsSpan((int)offset, (int)size).ToArray());
            var g = group.AsSpan(6 + 14 * i, 14);
            e[..8].CopyTo(g);                                   // width, height, colors, reserved, planes, bit count
            BinaryPrimitives.WriteUInt32LittleEndian(g[8..], size);
            BinaryPrimitives.WriteUInt16LittleEndian(g[12..], (ushort)(firstId + i));
        }
        return (images, group);
    }
}
