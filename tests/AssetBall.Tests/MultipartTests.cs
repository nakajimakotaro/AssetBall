using AssetBall.Aws;
using AssetBall.Core;
using Xunit;

namespace AssetBall.Tests;

public sealed class MultipartTests
{
    [Fact]
    public void ReusesLargeExtentsAndUploadsChangedTail()
    {
        long min = MultipartPlanner.MinPartSize;
        var old = Fixtures.Index(new[] { Fixtures.Entry("a", min * 2), Fixtures.Entry("b", min) });
        var next = Fixtures.Index(new[] { Fixtures.Entry("a", min * 2), Fixtures.Entry("b", 3, "changed") }, old);
        var plan = MultipartPlanner.Create(old, next);
        Assert.Equal(min * 2, plan.CopyBytes);
        Assert.Equal(3, plan.UploadBytes);
        Assert.Equal(2, plan.Parts.Count);
        Assert.Equal(0, plan.Parts[0].CopySourceOffset);
        Assert.Null(plan.Parts[1].CopySourceOffset);
    }

    [Fact]
    public void TinyDiscontinuousCopiesAreReuploadedWithCorrectAccounting()
    {
        var old = Fixtures.Index(new[] { Fixtures.Entry("a", 2), Fixtures.Entry("b", 2), Fixtures.Entry("c", 2) });
        var next = Fixtures.Index(new[] { Fixtures.Entry("a", 2), Fixtures.Entry("c", 2), Fixtures.Entry("d", 2) }, old);
        var plan = MultipartPlanner.Create(old, next);
        Assert.Null(Assert.Single(plan.Parts).CopySourceOffset);
        Assert.Equal(6, plan.UploadBytes);
        Assert.Equal(2, plan.ChangedBytes);
        Assert.Equal(4, plan.ReuploadedBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5242880)]
    public void SmallSourceObjectsCannotUseRangedCopy(long size)
    {
        var old = Fixtures.Index(new[] { Fixtures.Entry("a", size) });
        var next = Fixtures.Index(new[] { Fixtures.Entry("a", size), Fixtures.Entry("b", 1) }, old);
        var plan = MultipartPlanner.Create(old, next);
        Assert.Equal(0, plan.CopyBytes);
        Assert.Equal(size, plan.ReuploadedBytes);
        Check(old, next, plan);
    }

    [Fact]
    public void ExactMaximumCapacityFitsTenThousandParts()
    {
        var index = Fixtures.Index(new[] { Fixtures.Entry("max", MultipartPlanner.MaxObjectSize) });
        var plan = MultipartPlanner.Create(null, index);
        Assert.Equal(MultipartPlanner.MaxParts, plan.Parts.Count);
        Assert.All(plan.Parts, p => Assert.Equal(MultipartPlanner.MaxPartSize, p.Length));
        Check(null, index, plan);
    }

    [Fact]
    public void TerabytePlanUsesLongOffsetsAndRespectsPartLimitWithoutAllocatingBall()
    {
        long size = 12L * 1024 * 1024 * 1024 * 1024;
        var index = Fixtures.Index(new[] { Fixtures.Entry("huge", size) });
        var plan = MultipartPlanner.Create(null, index, MultipartPlanner.MinPartSize);
        Check(null, index, plan);
        Assert.Equal(size, plan.UploadBytes);
        Assert.Throws<InvalidDataException>(() => MultipartPlanner.Create(null,
            Fixtures.Index(new[] { Fixtures.Entry("too-big", MultipartPlanner.MaxObjectSize + 1) })));
    }

    [Fact]
    public void ExtremeFragmentationFallsBackWithinS3Limits()
    {
        long max = MultipartPlanner.MaxPartSize;
        var source = Enumerable.Range(0, 24000).Select(i => Fixtures.Entry(i.ToString("D5"), max / 100)).ToArray();
        var old = Fixtures.Index(source);
        var next = Fixtures.Index(source.Where((_, i) => i % 2 == 0), old);
        var plan = MultipartPlanner.Create(old, next, MultipartPlanner.MinPartSize);
        Check(old, next, plan);
        Assert.True(plan.CopyBytes > next.Size / 2, "Part-count pressure should not discard most reusable data.");
    }

    [Fact]
    public void RandomizedPartsHaveLegalSizesAndOnlyCopyValidSourceBytes()
    {
        var random = new Random(922);
        long min = MultipartPlanner.MinPartSize;
        for (int run = 0; run < 200; run++)
        {
            var assets = Enumerable.Range(0, 50).Select(i => Fixtures.Entry(i.ToString("D3"), random.NextInt64(0, min * 4))).ToArray();
            var old = Fixtures.Index(assets);
            var current = assets.Where(_ => random.Next(4) != 0).Select(a => random.Next(4) == 0 ? Fixtures.Entry(a.Path, random.NextInt64(0, min * 2), "changed") : a);
            var next = Fixtures.Index(current, old);
            Check(old, next, MultipartPlanner.Create(old, next, min * 3));
        }
    }

    private static void Check(BallIndex? old, BallIndex next, MultipartPlan plan)
    {
        var copies = Planner.Compare(old, next).Copies;
        Assert.InRange(plan.Parts.Count, 0, MultipartPlanner.MaxParts);
        long position = 0;
        foreach (var part in plan.Parts)
        {
            Assert.Equal(position, part.Offset);
            Assert.InRange(part.Length, part.Number == plan.Parts.Count ? 1 : MultipartPlanner.MinPartSize, MultipartPlanner.MaxPartSize);
            if (part.CopySourceOffset is long source)
                Assert.Contains(copies, c => c.TargetOffset <= part.Offset && c.TargetOffset + c.Length >= part.Offset + part.Length &&
                    c.SourceOffset + part.Offset - c.TargetOffset == source);
            position += part.Length;
        }
        Assert.Equal(next.Size, position);
        Assert.Equal(next.Size, plan.UploadBytes + plan.CopyBytes);
        Assert.True(plan.ReuploadedBytes >= 0);
    }
}
