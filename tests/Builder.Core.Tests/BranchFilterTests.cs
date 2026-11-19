using System.Security.Cryptography;
using FluentSetup.Builder.Core.Packaging;
using Xunit;

namespace FluentSetup.Builder.Core.Tests;

/// <summary>
/// The branch filters: exact inverses, independent of chunk sizes, and byte-identical to setup's decoder
/// (tests/Native/differential_tests.cpp checks the same vectors against the same digests).
/// </summary>
public class BranchFilterTests
{
    // Same generator as the native test: pseudo-random bytes with many E8/E9 (x86) or BL words (arm64).
    internal static byte[] Vector(BranchFilterKind kind, int size)
    {
        var b = new byte[size];
        for (var k = 0; k < size; k++)
        {
            b[k] = (byte)(unchecked((uint)k * 2654435761u) >> 24);
            if (kind == BranchFilterKind.X86)
            {
                if (k % 11 == 0) b[k] = 0xE8;
                else if (k % 17 == 0) b[k] = 0xE9;
            }
        }
        if (kind == BranchFilterKind.Arm64)
        {
            for (var j = 0; 4 * j + 3 < size; j += 3) b[4 * j + 3] = (byte)(0x94 | (b[4 * j + 3] & 3));
        }
        return b;
    }

    private static byte[] Run(BranchFilterKind kind, bool encode, byte[] input, int[] chunks)
    {
        var state = new BranchFilterState(kind, encode);
        var output = new List<byte>(input.Length);
        var buffer = new byte[input.Length + 8];
        int carry = 0, at = 0, c = 0;
        while (at < input.Length || carry > 0)
        {
            var n = Math.Min(chunks[c++ % chunks.Length], input.Length - at);
            input.AsSpan(at, n).CopyTo(buffer.AsSpan(carry));
            at += n;
            var avail = carry + n;
            var ready = state.Process(buffer.AsSpan(0, avail), at == input.Length);
            output.AddRange(buffer.AsSpan(0, ready).ToArray());
            buffer.AsSpan(ready, avail - ready).CopyTo(buffer);
            carry = avail - ready;
        }
        return [.. output];
    }

    [Theory]
    [InlineData(BranchFilterKind.X86, 200003, "973ab2325b99d1c69eaae695978bb4780fcb37ad06f59fa013d6019988c32c66")]
    [InlineData(BranchFilterKind.Arm64, 200002, "af50bf804762dd7362203c8d99f49bbf5893b7d4f42650869178d314a4202d12")]
    public void FiltersAreExactInversesInAnyChunks(BranchFilterKind kind, int size, string expected)
    {
        var input = Vector(kind, size);
        var whole = Run(kind, true, input, [int.MaxValue]);
        Assert.NotEqual(input, whole);  // the vector does have branches
        Assert.Equal(whole, Run(kind, true, input, [1, 2, 3, 5, 7, 4096, 1 << 20]));
        Assert.Equal(whole, Run(kind, true, input, [65536]));
        Assert.Equal(input, Run(kind, false, whole, [int.MaxValue]));
        Assert.Equal(input, Run(kind, false, whole, [3, 1, 4, 1, 5, 9, 2, 6]));
        var digest = Convert.ToHexStringLower(SHA256.HashData(whole));
        Assert.True(digest == expected, $"encoded digest {digest}");
    }

    [Fact]
    public void OnlyX86AndArm64ProgramsAreFiltered()
    {
        using var dir = new TempDir();
        Assert.Equal(BranchFilterKind.None, BranchFilterState.For(dir.File("a.txt", "MZ but not a program")));
        Assert.Equal(BranchFilterKind.None, BranchFilterState.For(Path.Combine(dir.Path, "missing.dll")));
        // A managed DLL built for AnyCPU carries the I386 machine type: x86.
        Assert.Equal(BranchFilterKind.X86, BranchFilterState.For(typeof(BranchFilterState).Assembly.Location));
    }
}
