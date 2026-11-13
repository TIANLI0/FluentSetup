using System.Security.Cryptography;
using System.Text.Json;
using ZstdSharp;

namespace FluentSetup.Builder.Core.Packaging;

public sealed class PackageInfo
{
    public string Path { get; init; } = "";
    public long FileSize { get; init; }
    public bool Signed { get; set; }
    public long SignatureSize { get; set; }
    public long OverlayStart { get; set; }
    public PackageFormat.Footer? Footer { get; set; }
    public JsonDocument? Header { get; set; }
    public int PayloadFiles { get; set; }
    public long PayloadRawBytes { get; set; }
    public long PayloadStoredBytes { get; set; }
    public long FrontendStoredBytes { get; set; }
    public int FrontendFiles { get; set; }
    /// <summary>Official prerequisite installers stored in the package (redist set).</summary>
    public int BundledInstallers { get; set; }
    public List<string> Problems { get; } = [];
    public bool StreamsVerified { get; set; }
}

/// <summary>Reads and checks a Setup.exe produced by FluentSetup (used by "fluentsetup inspect" and build verification).</summary>
public static class PackageInspector
{
    public static PackageInfo Inspect(string path, bool verifyStreams)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = new PackageInfo { Path = path, FileSize = fs.Length };
        long dataEnd = fs.Length;
        var (secOffset, secSize) = PackageFormat.ReadSecurityDirectory(fs);
        if (secOffset != 0 && secSize != 0)
        {
            info.Signed = true;
            info.SignatureSize = secSize;
            if (secOffset + secSize > fs.Length) { info.Problems.Add("certificate table outside the file"); return info; }
            dataEnd = secOffset;
        }
        PackageFormat.Footer? footer = null;
        var buf = new byte[PackageFormat.FooterSize];
        for (var pad = 0; pad < 16 && dataEnd - pad >= PackageFormat.FooterSize; pad++)
        {
            fs.Position = dataEnd - pad - PackageFormat.FooterSize;
            fs.ReadExactly(buf);
            footer = PackageFormat.DecodeFooter(buf);
            if (footer is not null) { dataEnd -= pad; break; }
        }
        if (footer is null) { info.Problems.Add("no FluentSetup footer (not a FluentSetup package)"); return info; }
        info.Footer = footer;
        info.OverlayStart = (long)footer.OverlayStart;
        if (footer.Version != PackageFormat.FormatVersion) { info.Problems.Add($"unsupported format version {footer.Version}"); return info; }
        var overlaySize = dataEnd - PackageFormat.FooterSize - (long)footer.OverlayStart;
        if ((long)footer.HeaderSize > PackageFormat.MaxHeaderBytes || (long)footer.HeaderOffset + (long)footer.HeaderSize > overlaySize)
        {
            info.Problems.Add("header outside the package");
            return info;
        }
        var header = new byte[footer.HeaderSize];
        fs.Position = (long)(footer.OverlayStart + footer.HeaderOffset);
        fs.ReadExactly(header);
        if (!SHA256.HashData(header).AsSpan().SequenceEqual(footer.HeaderSha256)) { info.Problems.Add("header checksum mismatch"); return info; }
        var json = (footer.Flags & PackageFormat.FlagHeaderCompressed) != 0 ? new Decompressor().Unwrap(header).ToArray() : header;
        info.Header = JsonDocument.Parse(json);
        var root = info.Header.RootElement;

        void CheckSet(string name, bool? isPayload)
        {
            if (!root.TryGetProperty(name, out var set) || set.ValueKind != JsonValueKind.Object) return;
            var streams = set.GetProperty("streams").EnumerateArray().ToList();
            var files = set.GetProperty("files").EnumerateArray().ToList();
            long stored = 0;
            foreach (var s in streams)
            {
                var offset = s.GetProperty("offset").GetInt64();
                var size = s.GetProperty("size").GetInt64();
                stored += size;
                if (offset < 0 || size <= 0 || offset + size > (long)footer.HeaderOffset) { info.Problems.Add($"{name} stream outside the package"); continue; }
                if (!verifyStreams) continue;
                fs.Position = (long)footer.OverlayStart + offset;
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var chunk = new byte[1 << 20];
                var left = size;
                while (left > 0)
                {
                    var n = fs.Read(chunk, 0, (int)Math.Min(chunk.Length, left));
                    if (n == 0) break;
                    sha.AppendData(chunk, 0, n);
                    left -= n;
                }
                if (PackageFormat.Hex(sha.GetHashAndReset()) != s.GetProperty("sha256").GetString()) info.Problems.Add($"{name} stream checksum mismatch at offset {offset}");
            }
            if (isPayload is null)
            {
                info.BundledInstallers = files.Count;
            }
            else if (isPayload == true)
            {
                info.PayloadFiles = files.Count;
                info.PayloadRawBytes = files.Sum(f => f.GetProperty("size").GetInt64());
                info.PayloadStoredBytes = stored;
            }
            else
            {
                info.FrontendFiles = files.Count;
                info.FrontendStoredBytes = stored;
            }
        }
        CheckSet("payload", true);
        CheckSet("frontend", false);
        CheckSet("redist", null);
        info.StreamsVerified = verifyStreams;
        return info;
    }
}
