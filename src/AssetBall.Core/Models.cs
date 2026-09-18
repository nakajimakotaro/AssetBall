namespace AssetBall.Core;

/// <summary>アセットの識別子と Ball 内の位置。データ本体は持たず、範囲は [Offset, Offset + Size)。</summary>
public sealed class AssetEntry
{
    public string Path { get; }
    public long Offset { get; }
    public long Size { get; }
    public string Hash { get; }

    public AssetEntry(string path, long offset, long size, string hash)
    { Path = path; Offset = offset; Size = size; Hash = hash; }
}

/// <summary>1 世代の Ball を記述するメタデータ。Assets の順序が、そのまま Ball 内の配置順になる。</summary>
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
        // 入力コレクションから切り離し、構築時に検証した配置が後から変わらないようにする。
        Assets = Array.AsReadOnly(assets.ToArray());
        Validate();
    }

    public void Validate()
    {
        if (!IsHash(Hash) || Size < 0 || BallFile != FileName(Hash))
            throw new InvalidDataException("Invalid Ball metadata or content-addressed filename.");
        // Ball は生バイト列の連結なので、穴・重なり・パディングを認めない。
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

    // 内容ごとのファイル名にすることで、古い Index も対応する世代の Ball を参照できる。
    public static string FileName(string hash) => $"ball-{hash}.bin";
    public static bool IsHash(string? value) => value != null && value.Length == 64 &&
        value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

    // OS に依存しない相対パスを識別子にする。大文字小文字は区別し、Unicode 正規化はしない。
    public static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl) || path.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
            throw new InvalidDataException($"Invalid asset path: {path}");
    }
}

/// <summary>転送する非空の範囲。End は終端を含まないため、HTTP Range の終端には End - 1 を使う。</summary>
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

/// <summary>旧 Ball から新 Ball へのコピー。配置が変わるため、コピー元とコピー先の位置を分けて持つ。</summary>
public sealed class CopyRange
{
    public long SourceOffset { get; }
    public long TargetOffset { get; }
    public long Length { get; }
    public CopyRange(long sourceOffset, long targetOffset, long length)
    { SourceOffset = sourceOffset; TargetOffset = targetOffset; Length = length; }
}

/// <summary>旧 Ball から再利用する範囲と、新 Ball から取得する範囲。実際の I/O は呼び出し側が担当する。</summary>
public sealed class TransferPlan
{
    public IReadOnlyList<CopyRange> Copies { get; }
    public IReadOnlyList<ByteRange> Downloads { get; }
    public long ReusedBytes => Copies.Sum(x => x.Length);
    public long DownloadBytes => Downloads.Sum(x => x.Length);
    internal TransferPlan(List<CopyRange> copies, List<ByteRange> downloads)
    { Copies = copies.AsReadOnly(); Downloads = downloads.AsReadOnly(); }
}
