using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentSetup.Builder.Core.Model;

namespace FluentSetup.Builder.Core.Packaging;

/// <summary>
/// Pre-signed uninstaller (<c>signing.signUninstaller</c>). The installer writes uninstall.exe itself: the stub, the WinUI
/// front-end streams and an uninstaller header (src/Install.Core/engine.cpp, WriteUninstaller). Those bytes are known
/// at build time, so the builder produces the same file, signs it, and stores in the setup header only what the
/// installer cannot reproduce: the uninstaller header bytes, the Authenticode certificate table and the PE checksum.
/// The installer appends them, patches the checksum and the security directory, and uses the result only if its
/// SHA-256 matches; otherwise it falls back to an unsigned uninstaller.
/// </summary>
internal static class UninstallerImage
{
    internal sealed record Image(byte[] Header, byte[] Certificates, uint Checksum, byte[] Sha256);

    /// <param name="stubBytes">The first overlayStart bytes of Setup.exe (the stub with its padding).</param>
    /// <param name="setupHeader">The setup header JSON (without uninstallerImage).</param>
    /// <param name="frontendStreams">The front-end stream files in header order.</param>
    public static Image Create(SetupProject project, string projectDir, byte[] stubBytes, long overlayStart, byte[] setupHeader,
                               IReadOnlyList<string> frontendStreams, string tempDir, Action<string> log)
    {
        var unsigned = Path.Combine(tempDir, "uninstall.exe");
        byte[] header;
        using (var output = new FileStream(unsigned, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            // Stub (with padding) as Container::CopyStubTo copies it: Authenticode directory and checksum cleared.
            if (stubBytes.Length != overlayStart) throw new InvalidDataException("Unexpected stub size.");
            var stub = (byte[])stubBytes.Clone();
            var (checksumOffset, securityOffset) = PeHeaderOffsets(stub);
            stub.AsSpan(checksumOffset, 4).Clear();
            stub.AsSpan(securityOffset, 8).Clear();
            output.Write(stub);
            Pad8(output);

            var root = JsonNode.Parse(setupHeader)!.AsObject();
            root["kind"] = "uninstaller";
            root["payload"] = new JsonObject { ["streams"] = new JsonArray(), ["files"] = new JsonArray(), ["directories"] = new JsonArray() };
            root.Remove("redist");
            root.Remove("uninstallerImage");
            if (root["frontend"] is JsonObject frontend && frontend["streams"] is JsonArray streams)
            {
                if (streams.Count != frontendStreams.Count) throw new InvalidDataException("Front-end streams do not match the header.");
                for (var i = 0; i < streams.Count; i++)
                {
                    streams[i]!["offset"] = output.Position - overlayStart;
                    using (var s = File.OpenRead(frontendStreams[i])) s.CopyTo(output);
                    Pad8(output);
                }
            }
            header = JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var headerOffset = output.Position - overlayStart;
            output.Write(header);
            Pad8(output);
            output.Write(PackageFormat.EncodeFooter(new PackageFormat.Footer(PackageFormat.FormatVersion, 0, (ulong)overlayStart, (ulong)headerOffset,
                (ulong)header.Length, (ulong)header.Length, SHA256.HashData(header))));
        }

        var before = File.ReadAllBytes(unsigned);
        Signer.Sign(project.Signing, projectDir, unsigned, $"{project.App.Name} Uninstall", log);
        var signed = File.ReadAllBytes(unsigned);
        var (checksumAt, securityAt) = PeHeaderOffsets(signed);
        var certOffset = BinaryPrimitives.ReadUInt32LittleEndian(signed.AsSpan(securityAt));
        var certSize = BinaryPrimitives.ReadUInt32LittleEndian(signed.AsSpan(securityAt + 4));
        // The signed file must be the unsigned one plus the certificate table, with only the checksum and the security
        // directory changed: that is what the installer reproduces.
        if (certOffset != before.Length || (long)certOffset + certSize != signed.Length || certSize < 8)
            throw new InvalidDataException("The signing tool changed uninstall.exe in a way setup cannot reproduce.");
        for (var i = 0; i < before.Length; i++)
        {
            var inPatchedField = (i >= checksumAt && i < checksumAt + 4) || (i >= securityAt && i < securityAt + 8);
            if (!inPatchedField && before[i] != signed[i]) throw new InvalidDataException("The signing tool changed uninstall.exe in a way setup cannot reproduce.");
        }
        return new Image(header, signed.AsSpan((int)certOffset, (int)certSize).ToArray(), BinaryPrimitives.ReadUInt32LittleEndian(signed.AsSpan(checksumAt)),
                         SHA256.HashData(signed));
    }

    /// <summary>File offsets of the optional header's CheckSum field and of the security (Authenticode) data directory.</summary>
    internal static (int Checksum, int Security) PeHeaderOffsets(ReadOnlySpan<byte> pe)
    {
        if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z') throw new InvalidDataException("Not a PE file.");
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(pe[0x3C..]);
        if (peOffset <= 0 || peOffset > pe.Length - 24 - 2 || BinaryPrimitives.ReadUInt32LittleEndian(pe[peOffset..]) != 0x00004550)
            throw new InvalidDataException("Not a PE file.");
        var optional = peOffset + 24;
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(pe[optional..]);
        var dataDirectories = magic switch { 0x20B => optional + 112, 0x10B => optional + 96, _ => throw new InvalidDataException("Unknown PE optional header.") };
        var security = dataDirectories + 4 * 8;
        if (security + 8 > pe.Length) throw new InvalidDataException("Truncated PE header.");
        return (optional + 64, security);
    }

    private static void Pad8(Stream s)
    {
        var pad = (int)((8 - s.Position % 8) % 8);
        if (pad > 0) s.Write(new byte[pad]);
    }
}
