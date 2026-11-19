using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>Branch filter of a payload file (header "filter"); see src/Archive/pefilter.h, which setup decodes with.</summary>
public enum BranchFilterKind { None, X86, Arm64 }

/// <summary>
/// The branch filters of <c>src/Archive/pefilter.h</c>, line for line: relative call/jump targets in machine code become
/// absolute positions, so calls of the same function repeat and compress better. x86: E8/E9 with a 4-byte operand in
/// [-2^24, 2^24); arm64: BL (imm26) on 4-byte aligned words. Only operand bits change and the change is a bijection, so
/// decoding restores any input exactly. Positions are file offsets: a file can be processed in chunks of any size.
/// </summary>
public sealed class BranchFilterState(BranchFilterKind kind, bool encode)
{
    private long _position;

    /// <summary>
    /// Filters <paramref name="data"/> (the file from the current position on) in place and returns how many leading
    /// bytes are final; the rest (at most 4 bytes) must be passed again, followed by more of the file. With
    /// <paramref name="last"/> everything is final.
    /// </summary>
    public int Process(Span<byte> data, bool last)
    {
        var done = kind switch
        {
            BranchFilterKind.X86 => X86(data, last),
            BranchFilterKind.Arm64 => Arm64(data, last),
            _ => data.Length,
        };
        _position += done;
        return done;
    }

    private int X86(Span<byte> data, bool last)
    {
        var i = 0;
        while (i < data.Length)
        {
            if (data[i] != 0xE8 && data[i] != 0xE9)
            {
                i++;
                continue;
            }
            if (i + 5 > data.Length) return last ? data.Length : i;
            var v = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 1)..]);
            if (unchecked(v + 0x01000000u) < 0x02000000u)
            {
                var next = unchecked((uint)(_position + i + 5));
                var w = unchecked(encode ? v + next : v - next) & 0x01FFFFFFu;
                if ((w & 0x01000000u) != 0) w |= 0xFE000000u;
                BinaryPrimitives.WriteUInt32LittleEndian(data[(i + 1)..], w);
            }
            i += 5;
        }
        return data.Length;
    }

    private int Arm64(Span<byte> data, bool last)
    {
        var words = data.Length & ~3;
        for (var i = 0; i < words; i += 4)
        {
            var w = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            if ((w & 0xFC000000u) != 0x94000000u) continue;
            var at = unchecked((uint)((_position + i) >> 2));
            BinaryPrimitives.WriteUInt32LittleEndian(data[i..], 0x94000000u | (unchecked(encode ? w + at : w - at) & 0x03FFFFFFu));
        }
        return last ? data.Length : words;
    }

    /// <summary>The filter for a file: x86 for x86/x64 programs and DLLs, arm64 for Arm64 ones, none otherwise.</summary>
    public static BranchFilterKind For(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            Span<byte> head = stackalloc byte[4096];
            var n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (n < 0x40 || head[0] != 'M' || head[1] != 'Z') return BranchFilterKind.None;
            var pe = BinaryPrimitives.ReadInt32LittleEndian(head[0x3C..]);
            if (pe <= 0 || pe + 6 > n || BinaryPrimitives.ReadUInt32LittleEndian(head[pe..]) != 0x00004550) return BranchFilterKind.None;
            return BinaryPrimitives.ReadUInt16LittleEndian(head[(pe + 4)..]) switch
            {
                0x8664 or 0x014C => BranchFilterKind.X86,
                0xAA64 => BranchFilterKind.Arm64,
                _ => BranchFilterKind.None,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BranchFilterKind.None;
        }
    }

    public static string? HeaderName(BranchFilterKind kind) => kind switch { BranchFilterKind.X86 => "x86", BranchFilterKind.Arm64 => "arm64", _ => null };
}

/// <summary>
/// Decodes what an encoder produced, as it is produced, and hashes the result: at the end it must equal the file's own
/// SHA-256, so a package never carries a filtered file that setup could not restore.
/// </summary>
internal sealed class BranchFilterCheck(BranchFilterKind kind) : IDisposable
{
    private readonly BranchFilterState _decoder = new(kind, encode: false);
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[] _buffer = new byte[64 * 1024];
    private int _carry;

    public void Add(ReadOnlySpan<byte> encoded, bool last)
    {
        if (_buffer.Length < _carry + encoded.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _carry + encoded.Length));
        encoded.CopyTo(_buffer.AsSpan(_carry));
        var avail = _carry + encoded.Length;
        var ready = _decoder.Process(_buffer.AsSpan(0, avail), last);
        _hash.AppendData(_buffer, 0, ready);
        _buffer.AsSpan(ready, avail - ready).CopyTo(_buffer);
        _carry = avail - ready;
    }

    public byte[] Finish() => _hash.GetHashAndReset();

    public void Dispose() => _hash.Dispose();
}
