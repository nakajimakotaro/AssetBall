using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AssetBall.Client;
using AssetBall.Core;

// This class can be called by Unity code, but has no UnityEngine dependency.
// Keep it alive for the lifetime of the downloads; dispose after tasks finish.
public sealed class AssetBallExample : IDisposable
{
    private readonly HttpClient http;
    private readonly AssetBallClient client;

    public AssetBallExample()
    {
        http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None });
        http.Timeout = Timeout.InfiniteTimeSpan;
        client = new AssetBallClient(http);
    }

    public Task<BallIndex> UpdateAsync(string indexUrl, string cacheDirectory,
        IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        return client.UpdateAsync(new Uri(indexUrl), cacheDirectory, progress, cancellationToken);
    }

    // The caller owns the returned stream. A Unity caller can pass it to
    // AssetBundle.LoadFromStreamAsync and keep it open until loading completes.
    public Stream OpenAsset(string cacheDirectory, BallIndex index, string path)
    {
        return client.OpenAsset(cacheDirectory, index, path);
    }

    public void Dispose() { http.Dispose(); }
}
