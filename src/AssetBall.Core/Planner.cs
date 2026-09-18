namespace AssetBall.Core;

/// <summary>配置と差分転送の計画のみを担当する。ファイル・HTTP・S3 に依存せず、各 I/O 層で共用する。</summary>
public static class Planner
{
    /// <summary>現在のパス・サイズ・ハッシュから配置を決める。入力の Offset は使わずに再計算する。</summary>
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
        // 未変更分は前回の順序を保ち、残った変更・追加分を末尾へ集めて差分を連続させる。
        // 例: A|B|C の B を変更すると A|C|B'。削除されたパスは出力しない。
        var ordered = new List<AssetEntry>();
        if (previous != null)
            foreach (var old in previous.Assets)
                if (input.TryGetValue(old.Path, out var asset) && Same(old, asset))
                { ordered.Add(asset); input.Remove(old.Path); }
        // ファイル列挙順や実行環境によらず、同じ入力・前回 Index から同じ配置を再現する。
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

    /// <summary>新しい配置を組み立てるための計画。旧 Index がなければ、全データを取得対象にする。</summary>
    public static TransferPlan Compare(BallIndex? local, BallIndex remote)
    {
        var old = local?.Assets.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var copies = new List<CopyRange>();
        var downloads = new List<ByteRange>();
        foreach (var a in remote.Assets)
        {
            // 空アセットは Index に残すが、コピーや HTTP Range は必要ない。
            if (a.Size == 0) continue;
            if (old != null && old.TryGetValue(a.Path, out var b) && Same(a, b))
            {
                // 削除や移動で位置がずれるため、元と先の両方が連続する場合だけ一括コピーできる。
                var last = copies.LastOrDefault();
                if (last != null && last.SourceOffset + last.Length == b.Offset &&
                    last.TargetOffset + last.Length == a.Offset)
                    copies[copies.Count - 1] = new CopyRange(last.SourceOffset, last.TargetOffset, last.Length + a.Size);
                else copies.Add(new CopyRange(b.Offset, a.Offset, a.Size));
            }
            else
            {
                // 取得元は新 Ball なので、新配置で隣接していればアセット境界を越えて取得できる。
                var last = downloads.LastOrDefault();
                if (last != null && last.End == a.Offset)
                    downloads[downloads.Count - 1] = new ByteRange(last.Offset, last.Length + a.Size);
                else downloads.Add(new ByteRange(a.Offset, a.Size));
            }
        }
        return new TransferPlan(copies, downloads);
    }

    // 同じパスを照合した後で内容を比較する。別パス間の重複排除はせず、リネームは削除＋追加になる。
    private static bool Same(AssetEntry a, AssetEntry b) => a.Size == b.Size && a.Hash == b.Hash;
}
