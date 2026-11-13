using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace FluentSetup.Builder.Core.Prerequisites;

/// <summary>A Microsoft installer the builder downloaded for <c>"auto": "bundle"</c>.</summary>
public sealed record CachedInstaller(string Path, string Url, string Sha256, long Size, DateTimeOffset Downloaded, bool SignatureChecked);

/// <summary>
/// Downloads official prerequisite installers for bundling and keeps them for a day under the user's local application
/// data (<c>FluentSetup\cache\installers</c>), so repeated builds do not download again and offline builds can use the
/// last copy. On Windows every file must carry a valid Authenticode signature from the expected publisher.
/// </summary>
public static class InstallerCache
{
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(1);

    public static string Root
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = Path.Combine(Path.GetTempPath(), "FluentSetup");
            return Path.Combine(local, "FluentSetup", "cache", "installers");
        }
    }

    private sealed record Meta(string Url, string Sha256, long Size, DateTimeOffset Downloaded, bool SignatureChecked);

    /// <summary>The cached installer, downloading it when there is no copy from the last day (or <paramref name="refresh"/>).</summary>
    /// <exception cref="InvalidDataException">The download failed and there is no cached copy, or the signature is wrong.</exception>
    public static CachedInstaller Get(OfficialInstaller installer, bool refresh, Action<string> log, CancellationToken ct)
    {
        var dir = Path.Combine(Root, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(installer.Url)))[..16].ToLowerInvariant());
        var file = Path.Combine(dir, installer.FileName);
        var metaFile = Path.Combine(dir, "meta.json");
        var cached = Load(file, metaFile);
        if (cached is not null && !refresh && DateTimeOffset.UtcNow - cached.Downloaded < Fresh) return cached;

        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Guid.NewGuid():N}.download");
        try
        {
            log($"Downloading {installer.FileName} from {installer.Url}");
            using (var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }) { Timeout = TimeSpan.FromMinutes(10) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("FluentSetup/" + (typeof(InstallerCache).Assembly.GetName().Version?.ToString(3) ?? "1"));
                using var request = new HttpRequestMessage(HttpMethod.Get, installer.Url);
                using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException($"{installer.Url} redirected to a non-https address.");
                response.EnsureSuccessStatusCode();
                using var body = response.Content.ReadAsStream(ct);
                using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                var buffer = new byte[1 << 16];
                long total = 0;
                int n;
                while ((n = body.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    total += n;
                    if (total > 1L << 30) throw new InvalidDataException($"{installer.Url} is larger than 1 GB.");
                    output.Write(buffer, 0, n);
                }
            }
            var signatureChecked = false;
            if (OperatingSystem.IsWindows())
            {
                var signer = Authenticode.TrustedSigner(temp);
                if (!string.Equals(signer, installer.Signer, StringComparison.Ordinal))
                    throw new InvalidDataException($"{installer.FileName} from {installer.Url} is not signed by {installer.Signer} (signer: {(string.IsNullOrEmpty(signer) ? "none or not trusted" : signer)}).");
                signatureChecked = true;
            }
            else
            {
                log($"The signature of {installer.FileName} cannot be checked on this OS; it came over https from Microsoft's link.");
            }
            string sha;
            long size;
            using (var fs = File.OpenRead(temp))
            {
                size = fs.Length;
                sha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            }
            File.Move(temp, file, overwrite: true);
            var meta = new Meta(installer.Url, sha, size, DateTimeOffset.UtcNow, signatureChecked);
            File.WriteAllText(metaFile, JsonSerializer.Serialize(meta));
            return new CachedInstaller(file, installer.Url, sha, size, meta.Downloaded, signatureChecked);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            if (cached is not null)
            {
                log($"Could not download {installer.FileName} ({ex.Message}); using the copy from {cached.Downloaded.LocalDateTime:g}.");
                return cached;
            }
            throw new InvalidDataException($"Could not download {installer.FileName} from {installer.Url}: {ex.Message}", ex);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private static CachedInstaller? Load(string file, string metaFile)
    {
        try
        {
            if (!File.Exists(file) || !File.Exists(metaFile)) return null;
            var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaFile));
            if (meta is null || new FileInfo(file).Length != meta.Size) return null;
            using var fs = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(fs)).Equals(meta.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            return new CachedInstaller(file, meta.Url, meta.Sha256, meta.Size, meta.Downloaded, meta.SignatureChecked);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Authenticode checks through WinVerifyTrust (Windows only).</summary>
public static partial class Authenticode
{
    /// <summary>Simple name of the certificate that signed the file if the signature is valid and its chain trusted; "" otherwise.</summary>
    [SupportedOSPlatform("windows")]
    public static string TrustedSigner(string path)
    {
        if (!Verify(path)) return "";
        try
        {
#pragma warning disable SYSLIB0057 // the signer certificate of a signed file has no X509CertificateLoader equivalent
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (CryptographicException)
        {
            return "";
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool Verify(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");  // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = Marshal.StringToCoTaskMemUni(path) };
        var filePtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,              // WTD_UI_NONE
                fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1,           // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x00001000,    // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.dwStateAction = 2;          // WTD_STATEACTION_CLOSE
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            Marshal.FreeCoTaskMem(filePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [LibraryImport("wintrust.dll", SetLastError = false)]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
}
