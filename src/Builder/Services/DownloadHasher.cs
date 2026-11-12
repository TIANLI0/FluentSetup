using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace FluentSetup.Builder.Services;

public readonly record struct DownloadProgress(long Done, long? Total);

/// <summary>
/// Downloads a prerequisite's installer once to fill in requirements.*.download.sha256 and size. https only (also after
/// redirects), nothing is written to disk, the user can cancel, and a stalled server ends the download.
/// </summary>
public static class DownloadHasher
{
    private const long MaxBytes = 4L * 1024 * 1024 * 1024;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,  // .NET never follows a redirect from https to http
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,  // large files: the stall timeout below ends dead transfers
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FluentSetup-Builder", "1.0"));
        return client;
    }

    public static Task<(string Sha256, long Size)> ComputeAsync(Uri url, IProgress<DownloadProgress>? progress, CancellationToken cancel) =>
        Task.Run(() => ComputeCoreAsync(url, progress, cancel), cancel);

    private static async Task<(string Sha256, long Size)> ComputeCoreAsync(Uri url, IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException(Loc.Get("Download_NeedHttps"));
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException(Loc.Get("Download_NeedHttps"));
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var stream = await response.Content.ReadAsStreamAsync(stall.Token);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long done = 0;
            var lastReport = Stopwatch.StartNew();
            int read;
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                read = await stream.ReadAsync(buffer, stall.Token);
                if (read == 0) break;
                sha.AppendData(buffer, 0, read);
                done += read;
                if (done > MaxBytes) throw new InvalidDataException(Loc.Get("Download_TooLarge"));
                if (lastReport.ElapsedMilliseconds >= 150)
                {
                    progress?.Report(new DownloadProgress(done, total));
                    lastReport.Restart();
                }
            }
            if (total is { } expected && expected != done) throw new IOException(Loc.Get("Download_Incomplete"));
            return (Convert.ToHexStringLower(sha.GetHashAndReset()), done);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.Get("Download_Stalled"));
        }
    }
}
