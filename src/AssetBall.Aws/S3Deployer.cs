using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using AssetBall.Core;

namespace AssetBall.Aws;

// Index の内容と取得時点の ETag を組にし、公開時に他のデプロイとの競合を検出する。
public sealed record RemoteIndex(BallIndex Index, string ETag);
public sealed record DeploymentResult(BallIndex Index, MultipartPlan Plan, bool DryRun, string IndexKey);

/// <summary>S3 上で Ball を先に完成させ、条件付きで Index を公開する。S3 クライアントと再試行方針は呼び出し側が管理する。</summary>
public sealed class S3Deployer
{
    private readonly IAmazonS3 s3;
    public S3Deployer(IAmazonS3 s3) => this.s3 = s3 ?? throw new ArgumentNullException(nameof(s3));

    public async Task<RemoteIndex?> FetchIndexAsync(string bucket, string indexKey, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = indexKey }, cancellationToken);
            var index = await IndexJson.ReadAsync(response.ResponseStream, cancellationToken);
            if (string.IsNullOrEmpty(response.ETag)) throw new InvalidDataException("S3 Index response has no ETag.");
            return new RemoteIndex(index, response.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchKey") { return null; }
    }

    public async Task<DeploymentResult> DeployDirectoryAsync(string inputDirectory, string workDirectory,
        string bucket, string prefix = "", bool dryRun = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("A bucket is required.");
        prefix = prefix.Trim('/');
        if (prefix.Contains('\\') || prefix.Split('/').Any(p => p == "." || p == ".."))
            throw new ArgumentException("Invalid S3 prefix.");
        string Key(string name) => prefix.Length == 0 ? name : prefix + "/" + name;
        string indexKey = Key("index.json");
        var previous = await FetchIndexAsync(bucket, indexKey, cancellationToken);
        // 旧 Index の順序を引き継ぎつつ、ローカルには完全な新 Ball を作る。
        // S3 の制約で未変更分までアップロードする場合も、この Ball から必要な範囲を読み出せる。
        var index = await BallStore.BuildAsync(inputDirectory, workDirectory, previous?.Index, cancellationToken: cancellationToken);
        using var writerLock = BallStore.AcquireWriter(workDirectory);
        var plan = MultipartPlanner.Create(previous?.Index, index);
        var result = new DeploymentResult(index, plan, dryRun, indexKey);
        // dry-run でもリモートの読み取りとローカル生成は行い、S3 への書き込みだけを省く。
        if (dryRun) return result;

        string ballPath = Path.Combine(workDirectory, index.BallFile);
        await BallStore.VerifyAsync(ballPath, index, cancellationToken);
        // コピー元 Ball の ETag はコピー中の変更検出用。Index 公開の競合検出用 ETag とは別に扱う。
        string? sourceETag = null;
        if (plan.CopyBytes > 0)
        {
            var metadata = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            { BucketName = bucket, Key = Key(previous!.Index.BallFile) }, cancellationToken);
            if (metadata.Headers.ContentLength != previous!.Index.Size || string.IsNullOrEmpty(metadata.ETag))
                throw new InvalidDataException("Remote copy source does not match the previous Index.");
            sourceETag = metadata.ETag;
        }
        // 空 Ball は Part を作れないため、通常の PutObject で保存する。
        if (index.Size == 0)
        {
            using var empty = new MemoryStream(Array.Empty<byte>());
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = Key(index.BallFile), InputStream = empty,
                ContentType = "application/octet-stream", AutoCloseStream = false,
                Headers = { CacheControl = "public,max-age=31536000,immutable" }
            }, cancellationToken);
        }
        else
        {
            // COMPOSITE は Part ごとのチェックサムを合成した値で、Index に記録した Ball 全体の SHA-256 とは異なる。
            var initiation = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = bucket, Key = Key(index.BallFile), ContentType = "application/octet-stream",
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256, ChecksumType = ChecksumType.COMPOSITE,
                Headers = { CacheControl = "public,max-age=31536000,immutable" }
            }, cancellationToken);
            string uploadId = initiation.UploadId;
            try
            {
                var etags = new List<PartETag>();
                foreach (var part in plan.Parts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (part.CopySourceOffset is long source)
                    {
                        var response = await s3.CopyPartAsync(new CopyPartRequest
                        {
                            SourceBucket = bucket, SourceKey = Key(previous!.Index.BallFile),
                            DestinationBucket = bucket, DestinationKey = Key(index.BallFile), UploadId = uploadId,
                            PartNumber = part.Number, FirstByte = source, LastByte = source + part.Length - 1,
                            ETagToMatch = new List<string> { sourceETag! }
                        }, cancellationToken);
                        if (string.IsNullOrEmpty(response.ChecksumSHA256)) throw new InvalidDataException("S3 copy response has no SHA-256 checksum.");
                        etags.Add(new PartETag(part.Number, response.ETag) { ChecksumSHA256 = response.ChecksumSHA256 });
                    }
                    else
                    {
                        using var file = File.OpenRead(ballPath);
                        using var slice = new SliceStream(file, part.Offset, part.Length);
                        var response = await s3.UploadPartAsync(new UploadPartRequest
                        {
                            BucketName = bucket, Key = Key(index.BallFile), UploadId = uploadId,
                            PartNumber = part.Number, PartSize = part.Length, InputStream = slice,
                            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                            IsLastPart = part.Number == plan.Parts.Count
                        }, cancellationToken);
                        if (string.IsNullOrEmpty(response.ChecksumSHA256)) throw new InvalidDataException("S3 upload response has no SHA-256 checksum.");
                        etags.Add(new PartETag(part.Number, response.ETag) { ChecksumSHA256 = response.ChecksumSHA256 });
                    }
                }
                await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = bucket, Key = Key(index.BallFile), UploadId = uploadId,
                    PartETags = etags, ChecksumType = ChecksumType.COMPOSITE
                }, cancellationToken);
            }
            catch (Exception failure)
            {
                // 呼び出し元がキャンセル済みでも未完了 Part を片付けるため、後始末専用の期限付きトークンを使う。
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                    { BucketName = bucket, Key = Key(index.BallFile), UploadId = uploadId }, cleanup.Token);
                }
                catch (Exception abortFailure) { failure.Data["AbortMultipartUploadFailure"] = abortFailure; }
                throw;
            }
        }

        // Ball の完成後に公開世代を切り替える。取得時の ETag が変わっていれば競合として失敗させる。
        // 初回は Index が存在しないことを条件にする。旧 Ball は進行中の取得のために残す。
        using var body = new MemoryStream(IndexJson.Serialize(index), writable: false);
        try
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = indexKey, InputStream = body, ContentType = "application/json",
                AutoCloseStream = false, IfMatch = previous?.ETag, IfNoneMatch = previous == null ? "*" : null,
                Headers = { CacheControl = "no-cache" }
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed || ex.StatusCode == HttpStatusCode.Conflict)
        { throw new IOException("Another deployment changed the Index. Fetch the new state and deploy again.", ex); }
        return result;
    }
}
