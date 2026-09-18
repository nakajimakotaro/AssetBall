using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AssetBall.Client;
using AssetBall.Core;

// UnityEngine に依存しない利用例。保存先は Unity 側から渡し、通信中はこのインスタンスを保持する。
// HttpClient を更新ごとに作り直さず共有し、全タスクが終わってから Dispose する。
public sealed class AssetBallExample : IDisposable
{
    private readonly HttpClient http;
    private readonly AssetBallClient client;

    public AssetBallExample()
    {
        // Range の位置は生バイト列を基準にするため自動展開を無効化し、中断は呼び出し側のトークンで管理する。
        http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None });
        http.Timeout = Timeout.InfiniteTimeSpan;
        client = new AssetBallClient(http);
    }

    // UI を更新する Progress<T> は Unity のメインスレッドで生成し、通知先のコンテキストを捕捉させる。
    // 戻り値の Index は検証・公開が済んだ世代を表すため、そのまま OpenAsset に渡せる。
    public Task<BallIndex> UpdateAsync(string indexUrl, string cacheDirectory,
        IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
    {
        return client.UpdateAsync(new Uri(indexUrl), cacheDirectory, progress, cancellationToken);
    }

    // アセットを個別ファイルへ展開せず、Ball 内の範囲を Stream として読む。破棄は呼び出し側の責任。
    // AssetBundle.LoadFromStreamAsync に渡す場合は、Unity が必要とする間 Stream を開いたまま保持する。
    public Stream OpenAsset(string cacheDirectory, BallIndex index, string path)
    {
        return client.OpenAsset(cacheDirectory, index, path);
    }

    public void Dispose() { http.Dispose(); }
}
