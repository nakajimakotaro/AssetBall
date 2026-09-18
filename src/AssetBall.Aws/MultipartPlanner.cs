using AssetBall.Core;

namespace AssetBall.Aws;

public sealed record MultipartPart(int Number, long Offset, long Length, long? CopySourceOffset);

public sealed class MultipartPlan
{
    public IReadOnlyList<MultipartPart> Parts { get; }
    public long CopyBytes => Parts.Where(p => p.CopySourceOffset.HasValue).Sum(p => p.Length);
    public long UploadBytes => Parts.Where(p => !p.CopySourceOffset.HasValue).Sum(p => p.Length);
    public long ChangedBytes { get; }
    public long ReuploadedBytes => UploadBytes - ChangedBytes;
    internal MultipartPlan(List<MultipartPart> parts, long changedBytes)
    { Parts = parts.AsReadOnly(); ChangedBytes = changedBytes; }
}

/// <summary>Pure S3 constraint planning. Mixed/short fragments are uploaded from the local Ball.</summary>
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
        // S3 only allows ranged UploadPartCopy when the source object is larger than 5 MiB.
        IReadOnlyList<CopyRange> reusable = previous != null && previous.Size > MinPartSize
            ? transfer.Copies : Array.Empty<CopyRange>();
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
            // Reserve enough capacity in the remaining slots, rather than discarding all copies
            // when fragmentation would otherwise cause more than 10,000 parts.
            long minimum = Math.Max(MinPartSize, remaining - (MaxParts - parts.Count - 1L) * MaxPartSize);
            long length = Math.Min(Math.Max(partSize, minimum), remaining);
            long? source = null;
            if (copy != null && copy.TargetOffset <= position)
            {
                long available = copy.TargetOffset + copy.Length - position;
                if (available >= length || available >= minimum || position + available == size)
                {
                    length = Math.Min(length, available);
                    source = copy.SourceOffset + position - copy.TargetOffset;
                }
            }
            if (!source.HasValue)
            {
                // End an upload at the next useful copy boundary, provided this upload remains legal.
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
