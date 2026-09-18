using AssetBall.Core;

namespace AssetBall.Aws;

/// <summary>新 Ball 内の Part 範囲。CopySourceOffset があれば旧 S3 オブジェクトからコピーし、なければアップロードする。</summary>
public sealed record MultipartPart(int Number, long Offset, long Length, long? CopySourceOffset);

public sealed class MultipartPlan
{
    public IReadOnlyList<MultipartPart> Parts { get; }
    public long CopyBytes => Parts.Where(p => p.CopySourceOffset.HasValue).Sum(p => p.Length);
    public long UploadBytes => Parts.Where(p => !p.CopySourceOffset.HasValue).Sum(p => p.Length);
    public long ChangedBytes { get; }
    // 未変更でも Part 制約によりコピーできなかった分。実際の変更量と転送量の差を可視化する。
    public long ReuploadedBytes => UploadBytes - ChangedBytes;
    internal MultipartPlan(List<MultipartPart> parts, long changedBytes)
    { Parts = parts.AsReadOnly(); ChangedBytes = changedBytes; }
}

/// <summary>Core の差分計画を S3 の Part 制約へ適合させる。短い断片やコピーと変更が混在する Part はローカルから送る。</summary>
public static class MultipartPlanner
{
    public const long MinPartSize = 5L * 1024 * 1024;
    public const long MaxPartSize = 5L * 1024 * 1024 * 1024;
    public const int MaxParts = 10000;
    public const long MaxObjectSize = MaxPartSize * MaxParts;

    public static MultipartPlan Create(BallIndex? previous, BallIndex next, long preferredPartSize = 64L * 1024 * 1024)
    {
        if (preferredPartSize < MinPartSize || preferredPartSize > MaxPartSize)
            throw new ArgumentOutOfRangeException(nameof(preferredPartSize));
        if (next.Size > MaxObjectSize) throw new InvalidDataException("Ball exceeds S3 multipart capacity.");
        var transfer = Planner.Compare(previous, next);
        // この計画では、範囲指定コピーのコピー元が 5 MiB より大きいという制約も考慮する。
        IReadOnlyList<CopyRange> reusable = previous != null && previous.Size > MinPartSize
            ? transfer.Copies : Array.Empty<CopyRange>();
        // 希望サイズは目安。大きな Ball では Part 数の上限に収まるよう引き上げる。
        long partSize = Math.Max(preferredPartSize, (next.Size + MaxParts - 1) / MaxParts);
        return new MultipartPlan(Plan(reusable, next.Size, partSize), transfer.DownloadBytes);
    }

    private static List<MultipartPart> Plan(IReadOnlyList<CopyRange> copies, long size, long partSize)
    {
        var parts = new List<MultipartPart>();
        long position = 0;
        int cursor = 0;
        while (position < size)
        {
            while (cursor < copies.Count && copies[cursor].TargetOffset + copies[cursor].Length <= position) cursor++;
            var copy = cursor < copies.Count ? copies[cursor] : null;
            long remaining = size - position;
            // 断片化で Part 数が増えても、残りの枠を最大サイズまで使えば収まる長さを今回確保する。
            long minimum = Math.Max(MinPartSize, remaining - (MaxParts - parts.Count - 1L) * MaxPartSize);
            long length = Math.Min(Math.Max(partSize, minimum), remaining);
            long? source = null;
            if (copy != null && copy.TargetOffset <= position)
            {
                long available = copy.TargetOffset + copy.Length - position;
                // 1 Part のコピー元は連続範囲に限る。末尾 Part だけは最小サイズ未満でもよい。
                if (available >= length || available >= minimum || position + available == size)
                {
                    length = Math.Min(length, available);
                    source = copy.SourceOffset + position - copy.TargetOffset;
                }
            }
            if (!source.HasValue)
            {
                // サイズ制約を満たせるなら次のコピー範囲の直前で区切り、その範囲を後続 Part で再利用する。
                for (int i = cursor; i < copies.Count; i++)
                {
                    var candidate = copies[i];
                    long distance = candidate.TargetOffset - position;
                    if (distance >= length) break;
                    if (distance >= minimum && (candidate.Length >= MinPartSize || candidate.TargetOffset + candidate.Length == size))
                    { length = distance; break; }
                }
            }
            parts.Add(new MultipartPart(parts.Count + 1, position, length, source));
            position += length;
        }
        return parts;
    }
}
