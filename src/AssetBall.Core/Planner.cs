namespace AssetBall.Core;

/// <summary>Pure layout and transfer algorithms; no filesystem or network access.</summary>
public static class Planner
{
    public static IReadOnlyList<AssetEntry> Layout(IEnumerable<AssetEntry> current, BallIndex? previous = null)
    {
        var input = new Dictionary<string, AssetEntry>(StringComparer.Ordinal);
        foreach (var asset in current)
        {
            BallIndex.ValidatePath(asset.Path);
            if (asset.Size < 0 || !BallIndex.IsHash(asset.Hash) || input.ContainsKey(asset.Path))
                throw new InvalidDataException($"Invalid or duplicate input: {asset.Path}");
            input.Add(asset.Path, asset);
        }
        var ordered = new List<AssetEntry>();
        if (previous != null)
            foreach (var old in previous.Assets)
                if (input.TryGetValue(old.Path, out var asset) && Same(old, asset))
                { ordered.Add(asset); input.Remove(old.Path); }
        ordered.AddRange(input.Values.OrderBy(x => x.Path, StringComparer.Ordinal));
        long offset = 0;
        var result = new List<AssetEntry>();
        foreach (var a in ordered)
        {
            result.Add(new AssetEntry(a.Path, offset, a.Size, a.Hash));
            offset = checked(offset + a.Size);
        }
        return result.AsReadOnly();
    }

    public static TransferPlan Compare(BallIndex? local, BallIndex remote)
    {
        var old = local?.Assets.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var copies = new List<CopyRange>();
        var downloads = new List<ByteRange>();
        foreach (var a in remote.Assets)
        {
            if (a.Size == 0) continue;
            if (old != null && old.TryGetValue(a.Path, out var b) && Same(a, b))
            {
                var last = copies.LastOrDefault();
                if (last != null && last.SourceOffset + last.Length == b.Offset &&
                    last.TargetOffset + last.Length == a.Offset)
                    copies[copies.Count - 1] = new CopyRange(last.SourceOffset, last.TargetOffset, last.Length + a.Size);
                else copies.Add(new CopyRange(b.Offset, a.Offset, a.Size));
            }
            else
            {
                var last = downloads.LastOrDefault();
                if (last != null && last.End == a.Offset)
                    downloads[downloads.Count - 1] = new ByteRange(last.Offset, last.Length + a.Size);
                else downloads.Add(new ByteRange(a.Offset, a.Size));
            }
        }
        return new TransferPlan(copies, downloads);
    }

    private static bool Same(AssetEntry a, AssetEntry b) => a.Size == b.Size && a.Hash == b.Hash;
}
