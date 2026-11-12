using System.Buffers.Binary;
using System.Text;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>Container constants and footer codec. Must match src/Archive/container.h (format v1).</summary>
public static class PackageFormat
{
    public static readonly byte[] Magic = "FLSETUP\x1A"u8.ToArray();
    public const uint FormatVersion = 1;
    public const int FooterSize = 80;
    public const uint FlagHeaderCompressed = 1;
    public const long MaxHeaderBytes = 64L * 1024 * 1024;

    public sealed record Footer(uint Version, uint Flags, ulong OverlayStart, ulong HeaderOffset, ulong HeaderSize, ulong HeaderRawSize, byte[] HeaderSha256);

    public static byte[] EncodeFooter(Footer f)
    {
        var b = new byte[FooterSize];
        Magic.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), f.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), f.Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), f.OverlayStart);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), f.HeaderOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), f.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(40), f.HeaderRawSize);
        f.HeaderSha256.CopyTo(b, 48);
        return b;
    }

    public static Footer? DecodeFooter(ReadOnlySpan<byte> b)
    {
        if (b.Length != FooterSize || !b[..8].SequenceEqual(Magic)) return null;
        return new Footer(
            BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[32..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[40..]),
            b.Slice(48, 32).ToArray());
    }

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>Returns (certificateTableOffset, size) from the PE security directory, or (0,0) if unsigned.</summary>
    public static (long Offset, long Size) ReadSecurityDirectory(Stream pe)
    {
        var buf = new byte[4096];
        pe.Position = 0;
        var n = pe.Read(buf, 0, buf.Length);
        if (n < 0x40 || buf[0] != 'M' || buf[1] != 'Z') throw new InvalidDataException("Not a PE file.");
        var lfanew = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0x3C));
        if (lfanew <= 0 || lfanew > 3800) throw new InvalidDataException("Bad PE header offset.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(lfanew)) != 0x00004550) throw new InvalidDataException("Bad PE signature.");
        var opt = lfanew + 4 + 20;
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(opt));
        var dirs = magic == 0x20B ? opt + 112 : opt + 96;
        var sec = dirs + 4 * 8;
        return (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(sec)), BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(sec + 4)));
    }

    public static Encoding Utf8NoBom { get; } = new UTF8Encoding(false);
}
