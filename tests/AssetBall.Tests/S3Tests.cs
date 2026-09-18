using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AssetBall.Aws;
using AssetBall.Core;
using Xunit;

namespace AssetBall.Tests;

public sealed class S3Tests
{
    private sealed class FakeS3 : AmazonS3Client
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public List<string> Events { get; } = new();
        public string Failure = "";
        private readonly SortedDictionary<int, byte[]> parts = new();
        public FakeS3() : base(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://127.0.0.1:1" }) { }
        private static string ETag(byte[] bytes) => "\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) + "\"";
        private void Fail(string stage)
        {
            if (Failure == stage) throw new IOException("Injected " + stage + " failure");
            if (Failure == "cancel" && stage == "upload") throw new OperationCanceledException(new CancellationToken(true));
        }
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            Events.Add("get");
            if (Failure == "missing-bucket") throw new AmazonS3Exception("missing bucket") { ErrorCode = "NoSuchBucket", StatusCode = HttpStatusCode.NotFound };
            if (!Objects.TryGetValue(request.Key, out var bytes))
                throw new AmazonS3Exception("missing") { ErrorCode = "NoSuchKey", StatusCode = HttpStatusCode.NotFound };
            return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(bytes), ETag = ETag(bytes) });
        }
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            Events.Add("head");
            byte[] bytes = Objects[request.Key];
            return Task.FromResult(new GetObjectMetadataResponse { ETag = ETag(bytes), Headers = { ContentLength = bytes.LongLength } });
        }
        public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Events.Add("initiate"); Fail("initiate"); parts.Clear();
            Assert.Equal(ChecksumAlgorithm.SHA256, request.ChecksumAlgorithm);
            Assert.Equal(ChecksumType.COMPOSITE, request.ChecksumType);
            return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "test-upload" });
        }
        public override async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken = default)
        {
            Events.Add("upload"); Fail("upload");
            using var buffer = new MemoryStream();
            await request.InputStream.CopyToAsync(buffer, cancellationToken);
            Assert.Equal(request.PartSize, buffer.Length);
            Assert.True(request.IsLastPart == true || buffer.Length >= MultipartPlanner.MinPartSize);
            parts.Add(request.PartNumber!.Value, buffer.ToArray());
            Assert.Equal(ChecksumAlgorithm.SHA256, request.ChecksumAlgorithm);
            return new UploadPartResponse { ETag = ETag(buffer.ToArray()), PartNumber = request.PartNumber,
                ChecksumSHA256 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(buffer.ToArray())) };
        }
        public override Task<CopyPartResponse> CopyPartAsync(CopyPartRequest request, CancellationToken cancellationToken = default)
        {
            Events.Add("copy"); Fail("copy");
            byte[] source = Objects[request.SourceKey];
            Assert.True(source.LongLength > MultipartPlanner.MinPartSize);
            Assert.Equal(ETag(source), Assert.Single(request.ETagToMatch));
            byte[] bytes = source[(int)request.FirstByte!.Value..((int)request.LastByte!.Value + 1)];
            parts.Add(request.PartNumber!.Value, bytes);
            return Task.FromResult(new CopyPartResponse { ETag = ETag(bytes), PartNumber = request.PartNumber,
                ChecksumSHA256 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(bytes)) });
        }
        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Events.Add("complete"); Fail("complete");
            Assert.Equal(parts.Keys, request.PartETags.Select(p => p.PartNumber!.Value));
            Assert.All(request.PartETags, part => Assert.Equal(
                Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(parts[part.PartNumber!.Value])), part.ChecksumSHA256));
            Objects[request.Key] = parts.Values.SelectMany(x => x).ToArray();
            return Task.FromResult(new CompleteMultipartUploadResponse());
        }
        public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Events.Add("abort"); Assert.False(cancellationToken.IsCancellationRequested); parts.Clear();
            return Task.FromResult(new AbortMultipartUploadResponse());
        }
        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            bool index = request.Key.EndsWith("index.json");
            Events.Add(index ? "publish" : "put-ball");
            if (index)
            {
                if (Failure == "conflict") throw new AmazonS3Exception("conflict") { StatusCode = HttpStatusCode.PreconditionFailed };
                Fail("publish");
                if (Objects.TryGetValue(request.Key, out var old)) Assert.Equal(ETag(old), request.IfMatch);
                else Assert.Equal("*", request.IfNoneMatch);
            }
            using var buffer = new MemoryStream();
            await request.InputStream.CopyToAsync(buffer, cancellationToken);
            Objects[request.Key] = buffer.ToArray();
            if (index)
            {
                var parsed = await IndexJson.ReadAsync(new MemoryStream(buffer.ToArray()));
                string prefix = request.Key[..^"index.json".Length];
                Assert.True(Objects.ContainsKey(prefix + parsed.BallFile), "Index must never precede its Ball.");
                Assert.Equal(parsed.Size, Objects[prefix + parsed.BallFile].LongLength);
            }
            return new PutObjectResponse();
        }
    }

    [Fact]
    public async Task InitialDeploymentThenCopyAndUploadProducesExactBall()
    {
        using var w = new Workspace();
        await File.WriteAllBytesAsync(Path.Combine(w.Dir("input"), "a-large"), new byte[6 * 1024 * 1024]);
        w.Put("input", "b", "before");
        using var s3 = new FakeS3();
        var deployer = new S3Deployer(s3);
        var first = await deployer.DeployDirectoryAsync(w.Dir("input"), w.Dir("work"), "test", "release");
        Assert.Equal(new[] { "get", "initiate", "upload", "complete", "publish" }, s3.Events);
        s3.Events.Clear(); w.Put("input", "b", "after");
        var second = await deployer.DeployDirectoryAsync(w.Dir("input"), w.Dir("work"), "test", "release");
        Assert.Equal(new[] { "get", "head", "initiate", "copy", "upload", "complete", "publish" }, s3.Events);
        Assert.Equal(6 * 1024 * 1024, second.Plan.CopyBytes);
        Assert.Equal(5, second.Plan.UploadBytes);
        Assert.Equal(File.ReadAllBytes(Path.Combine(w.Dir("work"), second.Index.BallFile)), s3.Objects["release/" + second.Index.BallFile]);
        Assert.True(s3.Objects.ContainsKey("release/" + first.Index.BallFile));
        s3.Events.Clear();
        await deployer.DeployDirectoryAsync(w.Dir("input"), w.Dir("work"), "test", "release");
        Assert.Equal(new[] { "get", "head", "publish" }, s3.Events);
    }

    [Fact]
    public async Task DryRunDoesNotWriteToS3()
    {
        using var w = new Workspace(); w.Put("input", "a", "abc");
        using var s3 = new FakeS3();
        var result = await new S3Deployer(s3).DeployDirectoryAsync(w.Dir("input"), w.Dir("work"), "test", dryRun: true);
        Assert.True(result.DryRun);
        Assert.Equal(3, result.Plan.UploadBytes);
        Assert.Equal(new[] { "get" }, s3.Events);
        Assert.Empty(s3.Objects);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("initiate")]
    [InlineData("publish")]
    [InlineData("conflict")]
    public async Task FailureNeverPublishesAnIncompleteUpdate(string stage)
    {
        using var w = new Workspace(); w.Put("input", "a", "abc");
        using var s3 = new FakeS3();
        var deployer = new S3Deployer(s3);
        await deployer.DeployDirectoryAsync(w.Dir("input"), w.Dir("work"), "test");
        byte[] previous = s3.Objects["index.json"];
        w.Put("input", "a", "changed"); s3.Events.Clear(); s3.Failure = stage;
        var failure = await Record.ExceptionAsync(() => deployer.DeployDirectoryAsync(w.Dir("input"), w.Dir("work"), "test"));
        Assert.NotNull(failure);
        Assert.Equal(previous, s3.Objects["index.json"]);
        Assert.Equal(stage is "upload" or "complete" or "cancel", s3.Events.Contains("abort"));
        if (stage == "conflict") Assert.Contains("Another deployment", failure.Message);
    }

    [Fact]
    public async Task CopyFailureAbortsAndPreservesOldIndex()
    {
        using var w = new Workspace();
        await File.WriteAllBytesAsync(Path.Combine(w.Dir("input"), "a"), new byte[6 * 1024 * 1024]);
        w.Put("input", "b", "abc");
        using var s3 = new FakeS3(); var deployer = new S3Deployer(s3);
        await deployer.DeployDirectoryAsync(w.Dir("input"), w.Dir("work"), "test");
        byte[] before = s3.Objects["index.json"];
        w.Put("input", "b", "changed"); s3.Failure = "copy"; s3.Events.Clear();
        await Assert.ThrowsAsync<IOException>(() => deployer.DeployDirectoryAsync(w.Dir("input"), w.Dir("work"), "test"));
        Assert.Contains("abort", s3.Events); Assert.DoesNotContain("publish", s3.Events);
        Assert.Equal(before, s3.Objects["index.json"]);
    }

    [Fact]
    public async Task EmptyBallUsesPutAndMissingBucketIsNotTreatedAsFirstDeployment()
    {
        using var w = new Workspace(); using var s3 = new FakeS3();
        var deployer = new S3Deployer(s3);
        await deployer.DeployDirectoryAsync(w.Dir("input"), w.Dir("work"), "test");
        Assert.Equal(new[] { "get", "put-ball", "publish" }, s3.Events);
        s3.Failure = "missing-bucket";
        await Assert.ThrowsAsync<AmazonS3Exception>(() => deployer.FetchIndexAsync("missing", "index.json"));
    }
}
