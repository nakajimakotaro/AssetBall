using System.Text;
using AssetBall.Core;
using Xunit;

namespace AssetBall.Tests;

public sealed class CoreTests
{
    [Fact]
    public void LayoutPreservesUnchangedOrderAndAppendsChangedAssetsDeterministically()
    {
        var old = Fixtures.Index(new[] { Fixtures.Entry("c"), Fixtures.Entry("a"), Fixtures.Entry("b"), Fixtures.Entry("deleted") });
        var now = new[] { Fixtures.Entry("b", value: "changed"), Fixtures.Entry("new"), Fixtures.Entry("c"), Fixtures.Entry("a") };
        var next = Fixtures.Index(now, old);
        Assert.Equal(new[] { "a", "c", "b", "new" }, next.Assets.Select(a => a.Path));
        var plan = Planner.Compare(old, next);
        Assert.Equal(20, plan.ReusedBytes);
        Assert.Equal(20, plan.DownloadBytes);
        Assert.Equal(20, Assert.Single(plan.Downloads).Offset);
        Assert.Equal(2, plan.Copies.Count); // A deletion/changed asset creates a hole in the old Ball.
        Assert.Equal(IndexJson.Serialize(next), IndexJson.Serialize(Fixtures.Index(now.Reverse(), old)));
    }

    [Fact]
    public void MultipleGenerationsAndContentRevertsRemainCorrect()
    {
        var v1 = Fixtures.Index(new[] { Fixtures.Entry("a"), Fixtures.Entry("b"), Fixtures.Entry("c") });
        var v2 = Fixtures.Index(new[] { Fixtures.Entry("a", value: "v2"), Fixtures.Entry("b"), Fixtures.Entry("c") }, v1);
        var v3 = Fixtures.Index(new[] { Fixtures.Entry("a", value: "v2"), Fixtures.Entry("b", value: "v3"), Fixtures.Entry("c") }, v2);
        Assert.Single(Planner.Compare(v1, v3).Downloads);
        var v4 = Fixtures.Index(new[] { Fixtures.Entry("a"), Fixtures.Entry("b", value: "v3"), Fixtures.Entry("c") }, v3);
        Assert.Equal(10, Planner.Compare(v1, v4).DownloadBytes);
    }

    [Fact]
    public void RandomizedHistoryPlansCoverEveryByteExactlyOnce()
    {
        var random = new Random(321);
        var history = new List<BallIndex>();
        var current = new Dictionary<string, AssetEntry>();
        BallIndex? prior = null;
        for (int generation = 0; generation < 100; generation++)
        {
            for (int i = 0; i < 12; i++)
            {
                string path = "file-" + random.Next(30);
                if (random.Next(4) == 0) current.Remove(path);
                else current[path] = Fixtures.Entry(path, random.Next(20), random.Next(10).ToString());
            }
            var next = Fixtures.Index(current.Values, prior);
            foreach (var old in history)
            {
                var plan = Planner.Compare(old, next);
                var spans = plan.Copies.Select(c => (c.TargetOffset, c.Length))
                    .Concat(plan.Downloads.Select(r => (r.Offset, r.Length))).OrderBy(x => x.Item1);
                long end = 0;
                foreach (var span in spans) { Assert.Equal(end, span.Item1); end += span.Length; }
                Assert.Equal(next.Size, end);
                long expected = next.Assets.Where(a => !old.Assets.Any(b => b.Path == a.Path && b.Hash == a.Hash && b.Size == a.Size)).Sum(a => a.Size);
                Assert.Equal(expected, plan.DownloadBytes);
            }
            history.Add(next); prior = next;
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("a//b")]
    [InlineData("C:/foo")]
    [InlineData("a\\b")]
    [InlineData("a/./b")]
    public void UnsafePathsAreRejected(string path) => Assert.Throws<InvalidDataException>(() => Fixtures.Index(new[] { Fixtures.Entry(path) }));

    [Fact]
    public void InvalidLayoutDuplicatesAndOverflowAreRejected()
    {
        string hash = Fixtures.Hash("x");
        Assert.Throws<InvalidDataException>(() => new BallIndex(BallIndex.FileName(hash), 2, hash, new[] { new AssetEntry("a", 1, 2, hash) }));
        Assert.Throws<InvalidDataException>(() => Fixtures.Index(new[] { Fixtures.Entry("a"), Fixtures.Entry("a") }));
        Assert.Throws<OverflowException>(() => Planner.Layout(new[] { Fixtures.Entry("a", long.MaxValue), Fixtures.Entry("b", 1) }));
        Assert.Throws<InvalidDataException>(() => new BallIndex("../ball.bin", 0, hash, Array.Empty<AssetEntry>()));
    }

    [Fact]
    public async Task JsonRoundTripAndStrictRequiredFields()
    {
        var index = Fixtures.Index(new[] { Fixtures.Entry("日本語/asset", 0) });
        var bytes = IndexJson.Serialize(index);
        Assert.Equal(bytes, IndexJson.Serialize(await IndexJson.ReadAsync(new MemoryStream(bytes))));
        string json = Encoding.UTF8.GetString(bytes);
        foreach (string invalid in new[]
        {
            "{}", "null", json.Replace("\"version\": 1", "\"version\": 2"),
            json.Replace("\"version\": 1", "\"version\": 1, \"version\": 1"),
            json.Replace("\"offset\": 0", "\"offset\": 0.1"), json.Replace("sha256", "md5"),
            json.Replace("\"size\": 0", "\"size\": 9999999999999999999999999999")
        }) await Assert.ThrowsAsync<InvalidDataException>(() => IndexJson.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(invalid))));
    }

    [Fact]
    public async Task BuildUpdateVerifyAndAssetSlicesWorkEndToEnd()
    {
        using var w = new Workspace();
        w.Put("input", "a", "aaaa"); w.Put("input", "b", "bbbb"); w.Put("input", "empty", "");
        var first = await BallStore.BuildAsync(w.Dir("input"), w.Dir("v1"));
        var repeat = await BallStore.BuildAsync(w.Dir("input"), w.Dir("repeat"));
        Assert.Equal(IndexJson.Serialize(first), IndexJson.Serialize(repeat));
        w.Put("input", "a", "new-a"); w.Put("input", "new", "hello");
        var second = await BallStore.BuildAsync(w.Dir("input"), w.Dir("v2"), first, Path.Combine(w.Dir("v1"), first.BallFile));
        Assert.Equal(new[] { "b", "empty", "a", "new" }, second.Assets.Select(x => x.Path));
        await BallStore.VerifyAsync(Path.Combine(w.Dir("v2"), second.BallFile), second);
        using var asset = BallStore.OpenAsset(w.Dir("v2"), second, "a");
        Assert.Equal(5, asset.Length);
        using var reader = new StreamReader(asset);
        Assert.Equal("new-a", await reader.ReadToEndAsync());
        Assert.Equal(-1, asset.ReadByte());
        Assert.Throws<IOException>(() => asset.Seek(1, SeekOrigin.End));
        asset.Position = 0;
        Assert.Equal('n', asset.ReadByte());
    }

    [Fact]
    public async Task EmptyBallDeletionOnlyAndCorruption()
    {
        using var w = new Workspace();
        w.Put("input", "x", "bytes");
        var first = await BallStore.BuildAsync(w.Dir("input"), w.Dir("out"));
        File.Delete(Path.Combine(w.Dir("input"), "x"));
        var empty = await BallStore.BuildAsync(w.Dir("input"), w.Dir("out"), first, Path.Combine(w.Dir("out"), first.BallFile));
        Assert.Equal(0, empty.Size);
        Assert.Empty(Planner.Compare(first, empty).Downloads);
        await BallStore.VerifyAsync(Path.Combine(w.Dir("out"), empty.BallFile), empty);
        string path = Path.Combine(w.Dir("out"), first.BallFile);
        await File.WriteAllTextAsync(path, "wrong");
        await Assert.ThrowsAsync<InvalidDataException>(() => BallStore.VerifyAsync(path, first));
    }

    [Fact]
    public async Task CancellationAndWriterCollisionPreservePublishedIndex()
    {
        using var w = new Workspace();
        w.Put("input", "x", "bytes");
        await BallStore.BuildAsync(w.Dir("input"), w.Dir("out"));
        byte[] original = File.ReadAllBytes(Path.Combine(w.Dir("out"), "index.json"));
        w.Put("input", "x", "changed");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BallStore.BuildAsync(w.Dir("input"), w.Dir("out"), cancellationToken: new CancellationToken(true)));
        using (BallStore.AcquireWriter(w.Dir("out")))
            await Assert.ThrowsAsync<IOException>(() => BallStore.BuildAsync(w.Dir("input"), w.Dir("out")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(w.Dir("out"), "index.json")));
        await Assert.ThrowsAsync<ArgumentException>(() => BallStore.BuildAsync(w.Dir("input"), Path.Combine(w.Dir("input"), "out")));
    }

    [Fact]
    public async Task SymlinksAreRejected()
    {
        if (OperatingSystem.IsWindows()) return; // Creating links may require Windows developer mode.
        using var w = new Workspace();
        string outside = w.Put("outside", "x", "secret");
        File.CreateSymbolicLink(Path.Combine(w.Dir("input"), "link"), outside);
        await Assert.ThrowsAsync<IOException>(() => BallStore.BuildAsync(w.Dir("input"), w.Dir("out")));
    }
}
