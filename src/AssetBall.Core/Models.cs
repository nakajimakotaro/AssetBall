namespace AssetBall.Core;

public sealed class AssetEntry
{
    public string Path { get; }
    public long Offset { get; }
    public long Size { get; }
    public string Hash { get; }

    public AssetEntry(string path, long offset, long size, string hash)
    { Path = path; Offset = offset; Size = size; Hash = hash; }
}

public sealed class BallIndex
{
    public const string Format = "assetball";
    public const int Version = 1;
    public string BallFile { get; }
    public long Size { get; }
    public string Hash { get; }
    public IReadOnlyList<AssetEntry> Assets { get; }

    public BallIndex(string ballFile, long size, string hash, IEnumerable<AssetEntry> assets)
    {
        BallFile = ballFile; Size = size; Hash = hash;
        Assets = Array.AsReadOnly(assets.ToArray());
        Validate();
    }

    public void Validate()
    {
        if (!IsHash(Hash) || Size < 0 || BallFile != FileName(Hash))
            throw new InvalidDataException("Invalid Ball metadata or content-addressed filename.");
        long end = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in Assets)
        {
            ValidatePath(a.Path);
            if (!paths.Add(a.Path) || a.Size < 0 || a.Offset != end || !IsHash(a.Hash))
                throw new InvalidDataException($"Invalid asset: {a.Path}");
            if (a.Size > long.MaxValue - end) throw new InvalidDataException("Offset overflow.");
            end += a.Size;
        }
        if (end != Size) throw new InvalidDataException("Ball size does not match asset layout.");
    }

    public static string FileName(string hash) => $"ball-{hash}.bin";
    public static bool IsHash(string? value) => value != null && value.Length == 64 &&
        value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl) || path.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
            throw new InvalidDataException($"Invalid asset path: {path}");
    }
}

public sealed class ByteRange
{
    public long Offset { get; }
    public long Length { get; }
    public long End => checked(Offset + Length);
    public ByteRange(long offset, long length)
    {
        if (offset < 0 || length <= 0 || offset > long.MaxValue - length)
            throw new ArgumentOutOfRangeException(nameof(length));
        Offset = offset; Length = length;
    }
}

public sealed class CopyRange
{
    public long SourceOffset { get; }
    public long TargetOffset { get; }
    public long Length { get; }
    public CopyRange(long sourceOffset, long targetOffset, long length)
    { SourceOffset = sourceOffset; TargetOffset = targetOffset; Length = length; }
}

public sealed class TransferPlan
{
    public IReadOnlyList<CopyRange> Copies { get; }
    public IReadOnlyList<ByteRange> Downloads { get; }
    public long ReusedBytes => Copies.Sum(x => x.Length);
    public long DownloadBytes => Downloads.Sum(x => x.Length);
    internal TransferPlan(List<CopyRange> copies, List<ByteRange> downloads)
    { Copies = copies.AsReadOnly(); Downloads = downloads.AsReadOnly(); }
}
