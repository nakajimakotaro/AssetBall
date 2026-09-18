using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AssetBall.Core;

namespace AssetBall.Client;

/// <summary>ネットワーク取得量とローカル再利用量を分けた進捗。検証などを含む更新処理全体の進捗ではない。</summary>
public sealed class DownloadProgress
{
    public long DownloadedBytes { get; }
    public long TotalDownloadBytes { get; }
    public long ReusedBytes { get; }
    public DownloadProgress(long downloadedBytes, long totalDownloadBytes, long reusedBytes)
    { DownloadedBytes = downloadedBytes; TotalDownloadBytes = totalDownloadBytes; ReusedBytes = reusedBytes; }
}

/// <summary>差分計画に従ってローカル Ball を更新する。HttpClient は呼び出し側が所有し、自動展開を無効にする。</summary>
public sealed class AssetBallClient
{
    private readonly HttpClient http;
    public AssetBallClient(HttpClient httpClient) => http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<BallIndex> FetchIndexAsync(Uri indexUri, CancellationToken cancellationToken = default)
    {
        ValidateUri(indexUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, indexUri);
        // Index は公開世代を指す可変ファイルなので、キャッシュには再検証を要求する。
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
        // ハッシュが一致したキャッシュだけをコピー元にする。破損・欠落時は初回取得と同じ計画で復旧する。
        if (File.Exists(indexPath))
        {
            try
            {
                local = await IndexJson.ReadFileAsync(indexPath, cancellationToken).ConfigureAwait(false);
                await BallStore.VerifyAsync(Path.Combine(directory, local.BallFile), local, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is FileNotFoundException || ex is EndOfStreamException)
            { local = null; }
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
            // 旧 Ball を直接書き換えず、別ファイルへ再構築する。失敗やキャンセルでも公開済みデータを保つ。
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
                // 検証済みの BallFile はハッシュを含む単一ファイル名。同じ配信ディレクトリの対象世代を取得する。
                var ballUri = new Uri(indexUri, remote.BallFile);
                foreach (var range in plan.Downloads)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, ballUri);
                    request.Headers.Range = new RangeHeaderValue(range.Offset, range.End - 1);
                    // オフセットとハッシュは生バイト列を基準にするため、圧縮による表現の変換を避ける。
                    request.Headers.AcceptEncoding.ParseAdd("identity");
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    // Range を無視した 200 や別範囲の応答を、新 Ball の一部分として書き込まない。
                    var cr = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || cr?.Unit != "bytes" ||
                        cr.From != range.Offset || cr.To != range.End - 1 || cr.Length != remote.Size ||
                        response.Content.Headers.ContentLength is long length && length != range.Length ||
                        response.Content.Headers.ContentEncoding.Any(x => !string.Equals(x, "identity", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException($"Server returned an invalid Range response ({(int)response.StatusCode}).");
                    using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    target.Position = range.Offset;
                    await StreamIO.CopyExactlyAsync(source, target, range.Length, cancellationToken, n => { downloaded += n; Report(); }).ConfigureAwait(false);
                    // Content-Length がなくても、指定範囲を超える本文を検出する。
                    var probe = new byte[1];
                    if (await source.ReadAsync(probe, 0, 1, cancellationToken).ConfigureAwait(false) != 0)
                        throw new InvalidDataException("Range response is longer than requested.");
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(true);
            }
            // HTTP ヘッダーの整合性だけでは内容を保証できない。全ハッシュを検証してから Index を切り替える。
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
