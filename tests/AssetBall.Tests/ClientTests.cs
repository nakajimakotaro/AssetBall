using System.Net;
using System.Net.Http.Headers;
using AssetBall.Client;
using AssetBall.Core;
using Xunit;

namespace AssetBall.Tests;

public sealed class ClientTests
{
    private sealed class Server : HttpMessageHandler
    {
        public BallIndex Index = null!;
        public byte[] Ball = null!;
        public string Fault = "";
        public List<(long Start, long End)> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.EndsWith("index.json"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(IndexJson.Serialize(Index)) });
            Assert.EndsWith(Index.BallFile, request.RequestUri.AbsolutePath);
            Assert.Contains(request.Headers.AcceptEncoding, e => e.Value == "identity");
            var range = Assert.Single(request.Headers.Range!.Ranges);
            long start = range.From!.Value, end = range.To!.Value;
            Requests.Add((start, end));
            byte[] bytes = Ball[(int)start..((int)end + 1)];
            if (Fault == "corrupt") bytes[0] ^= 255;
            if (Fault == "truncated") bytes = bytes[..^1];
            if (Fault == "overlong") bytes = bytes.Concat(new byte[] { 1 }).ToArray();
            var response = new HttpResponseMessage(Fault == "ignored" ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(Fault == "range" ? start + 1 : start, end, Ball.Length);
            if (Fault == "encoded") response.Content.Headers.ContentEncoding.Add("gzip");
            if (Fault == "truncated" || Fault == "overlong") response.Content.Headers.ContentLength = end - start + 1;
            if (Fault == "cancel") throw new OperationCanceledException();
            return Task.FromResult(response);
        }
        public void Set(string directory, BallIndex index)
        { Index = index; Ball = File.ReadAllBytes(Path.Combine(directory, index.BallFile)); Requests.Clear(); }
    }

    private sealed class ProgressRecorder : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Values { get; } = new();
        public void Report(DownloadProgress value) => Values.Add(value);
    }

    [Fact]
    public async Task FirstDownloadDeltaNoOpDeletionAndReadAsset()
    {
        using var w = new Workspace();
        w.Put("input", "a", "aaa"); w.Put("input", "b", "bbb"); w.Put("input", "c", "ccc");
        var first = await BallStore.BuildAsync(w.Dir("input"), w.Dir("server"));
        using var server = new Server(); server.Set(w.Dir("server"), first);
        using var http = new HttpClient(server);
        var client = new AssetBallClient(http);
        var uri = new Uri("https://assets.example/releases/index.json");
        await client.UpdateAsync(uri, w.Dir("cache"));
        Assert.Equal((0L, 8L), Assert.Single(server.Requests));
        w.Put("input", "a", "AAAA"); w.Put("input", "d", "dd"); File.Delete(Path.Combine(w.Dir("input"), "c"));
        var next = await BallStore.BuildAsync(w.Dir("input"), w.Dir("server"), first);
        server.Set(w.Dir("server"), next);
        var progress = new ProgressRecorder();
        var result = await client.UpdateAsync(uri, w.Dir("cache"), progress);
        Assert.Equal((3L, 8L), Assert.Single(server.Requests));
        Assert.Equal(6, progress.Values.Last().DownloadedBytes);
        Assert.Equal(3, progress.Values.Last().ReusedBytes);
        using (var stream = client.OpenAsset(w.Dir("cache"), result, "a"))
        using (var reader = new StreamReader(stream)) Assert.Equal("AAAA", await reader.ReadToEndAsync());
        server.Requests.Clear();
        await client.UpdateAsync(uri, w.Dir("cache"));
        Assert.Empty(server.Requests);
        File.Delete(Path.Combine(w.Dir("input"), "a")); File.Delete(Path.Combine(w.Dir("input"), "d"));
        var deleted = await BallStore.BuildAsync(w.Dir("input"), w.Dir("server"), next);
        server.Set(w.Dir("server"), deleted);
        await client.UpdateAsync(uri, w.Dir("cache"));
        Assert.Empty(server.Requests);
        await BallStore.VerifyAsync(Path.Combine(w.Dir("cache"), deleted.BallFile), deleted);
    }

    [Theory]
    [InlineData("ignored")]
    [InlineData("range")]
    [InlineData("encoded")]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("overlong")]
    [InlineData("cancel")]
    public async Task FailedUpdatesLeaveThePreviousStateReadable(string fault)
    {
        using var w = new Workspace();
        w.Put("input", "a", "first");
        var first = await BallStore.BuildAsync(w.Dir("input"), w.Dir("server"));
        using var server = new Server(); server.Set(w.Dir("server"), first);
        using var http = new HttpClient(server);
        var client = new AssetBallClient(http);
        var uri = new Uri("https://assets.example/index.json");
        await client.UpdateAsync(uri, w.Dir("cache"));
        byte[] indexBefore = File.ReadAllBytes(Path.Combine(w.Dir("cache"), "index.json"));
        w.Put("input", "a", "second");
        var next = await BallStore.BuildAsync(w.Dir("input"), w.Dir("server"), first);
        server.Set(w.Dir("server"), next); server.Fault = fault;
        var failure = await Record.ExceptionAsync(() => client.UpdateAsync(uri, w.Dir("cache")));
        Assert.NotNull(failure);
        Assert.True(failure is IOException or InvalidDataException or OperationCanceledException, failure.ToString());
        Assert.Equal(indexBefore, File.ReadAllBytes(Path.Combine(w.Dir("cache"), "index.json")));
        await BallStore.VerifyAsync(Path.Combine(w.Dir("cache"), first.BallFile), first);
        Assert.Empty(Directory.GetFiles(w.Dir("cache"), "*.tmp"));
    }

    [Fact]
    public async Task CorruptCacheIsDownloadedAndRepaired()
    {
        using var w = new Workspace();
        w.Put("input", "a", "original");
        var index = await BallStore.BuildAsync(w.Dir("input"), w.Dir("server"));
        using var server = new Server(); server.Set(w.Dir("server"), index);
        using var http = new HttpClient(server);
        var client = new AssetBallClient(http);
        var uri = new Uri("https://assets.example/index.json");
        await client.UpdateAsync(uri, w.Dir("cache"));
        await File.WriteAllTextAsync(Path.Combine(w.Dir("cache"), index.BallFile), "corrupt!");
        server.Requests.Clear();
        await client.UpdateAsync(uri, w.Dir("cache"));
        Assert.Single(server.Requests);
        await BallStore.VerifyAsync(Path.Combine(w.Dir("cache"), index.BallFile), index);
    }

    [Fact]
    public async Task EmptyRemoteNeedsNoBallRequest()
    {
        using var w = new Workspace();
        var index = await BallStore.BuildAsync(w.Dir("input"), w.Dir("server"));
        using var server = new Server(); server.Set(w.Dir("server"), index);
        using var http = new HttpClient(server);
        await new AssetBallClient(http).UpdateAsync(new Uri("https://example.org/index.json"), w.Dir("cache"));
        Assert.Empty(server.Requests);
        await BallStore.VerifyAsync(Path.Combine(w.Dir("cache"), index.BallFile), index);
    }
}
