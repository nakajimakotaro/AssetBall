using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AssetBall.Core;

namespace AssetBall.Client;

public sealed class DownloadProgress
{
    public long DownloadedBytes { get; }
    public long TotalDownloadBytes { get; }
    public long ReusedBytes { get; }
    public DownloadProgress(long downloadedBytes, long totalDownloadBytes, long reusedBytes)
    { DownloadedBytes = downloadedBytes; TotalDownloadBytes = totalDownloadBytes; ReusedBytes = reusedBytes; }
}

/// <summary>HttpClient is owned by the caller. Use a handler with automatic decompression disabled.</summary>
public sealed class AssetBallClient
{
    private readonly HttpClient http;
    public AssetBallClient(HttpClient httpClient) => http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<BallIndex> FetchIndexAsync(Uri indexUri, CancellationToken cancellationToken = default)
    {
        ValidateUri(indexUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, indexUri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > IndexJson.MaxBytes) throw new InvalidDataException("Index too large.");
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await IndexJson.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BallIndex> UpdateAsync(Uri indexUri, string directory,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var remote = await FetchIndexAsync(indexUri, cancellationToken).ConfigureAwait(false);
        using var writerLock = BallStore.AcquireWriter(directory);
        BallIndex? local = null;
        string indexPath = Path.Combine(directory, "index.json");
        if (File.Exists(indexPath))
        {
            try
            {
                local = await IndexJson.ReadFileAsync(indexPath, cancellationToken).ConfigureAwait(false);
                await BallStore.VerifyAsync(Path.Combine(directory, local.BallFile), local, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is FileNotFoundException || ex is EndOfStreamException)
            { local = null; } // An untrusted cache must never be used as a copy source.
        }
        var plan = Planner.Compare(local, remote);
        long downloaded = 0;
        void Report() => progress?.Report(new DownloadProgress(downloaded, plan.DownloadBytes, plan.ReusedBytes));
        Report();
        if (local != null && IndexJson.Serialize(local).SequenceEqual(IndexJson.Serialize(remote)))
            return remote;
        string temporary = Path.Combine(directory, ".download-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, StreamIO.BufferSize, true))
            {
                target.SetLength(remote.Size);
                if (plan.Copies.Count > 0)
                {
                    using var source = File.OpenRead(Path.Combine(directory, local!.BallFile));
                    foreach (var copy in plan.Copies)
                    {
                        source.Position = copy.SourceOffset; target.Position = copy.TargetOffset;
                        await StreamIO.CopyExactlyAsync(source, target, copy.Length, cancellationToken).ConfigureAwait(false);
                    }
                }
                var ballUri = new Uri(indexUri, remote.BallFile);
                foreach (var range in plan.Downloads)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, ballUri);
                    request.Headers.Range = new RangeHeaderValue(range.Offset, range.End - 1);
                    request.Headers.AcceptEncoding.ParseAdd("identity");
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    var cr = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || cr?.Unit != "bytes" ||
                        cr.From != range.Offset || cr.To != range.End - 1 || cr.Length != remote.Size ||
                        response.Content.Headers.ContentLength is long length && length != range.Length ||
                        response.Content.Headers.ContentEncoding.Any(x => !string.Equals(x, "identity", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException($"Server returned an invalid Range response ({(int)response.StatusCode}).");
                    using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    target.Position = range.Offset;
                    await StreamIO.CopyExactlyAsync(source, target, range.Length, cancellationToken, n => { downloaded += n; Report(); }).ConfigureAwait(false);
                    var probe = new byte[1];
                    if (await source.ReadAsync(probe, 0, 1, cancellationToken).ConfigureAwait(false) != 0)
                        throw new InvalidDataException("Range response is longer than requested.");
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(true);
            }
            await BallStore.VerifyAsync(temporary, remote, cancellationToken).ConfigureAwait(false);
            await BallStore.CommitAsync(temporary, directory, remote, cancellationToken).ConfigureAwait(false);
            Report();
            return remote;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Stream OpenAsset(string directory, BallIndex index, string path) => BallStore.OpenAsset(directory, index, path);

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new ArgumentException("An absolute HTTP(S) Index URI is required.");
    }
}
