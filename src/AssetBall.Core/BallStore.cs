using System.Security.Cryptography;

namespace AssetBall.Core;

/// <summary>Ball の生成・検証・公開を担うディスク I/O 層。内容ごとの Ball を保存し、index.json で公開世代を切り替える。</summary>
public static class BallStore
{
    public static async Task<BallIndex> BuildAsync(string inputDirectory, string outputDirectory,
        BallIndex? previous = null, string? previousBallPath = null, CancellationToken cancellationToken = default)
    {
        string input = Path.GetFullPath(inputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string output = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // 出力を次回の入力として取り込まないよう、入力配下への出力を拒否する。
        // 大文字小文字だけが異なる別名も、プラットフォームによらず保守的に拒否する。
        if (output.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output directory must be outside the input directory.");
        if (!Directory.Exists(input)) throw new DirectoryNotFoundException(input);
        RejectLink(input);
        var files = EnumerateFiles(input).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        // まずメタデータを収集して配置を決める。アセット本体はメモリに保持しない。
        var entries = new List<AssetEntry>();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.GetRelativePath(input, file).Replace(Path.DirectorySeparatorChar, '/');
            BallIndex.ValidatePath(path);
            using var stream = File.OpenRead(file);
            entries.Add(new AssetEntry(path, 0, stream.Length, await StreamIO.HashAsync(stream, cancellationToken).ConfigureAwait(false)));
            sources.Add(path, file);
        }
        var layout = Planner.Layout(entries, previous);
        var oldAssets = previous?.Assets.ToDictionary(x => x.Path, StringComparer.Ordinal);
        Directory.CreateDirectory(output);
        using var writerLock = AcquireWriter(output);
        string temp = Path.Combine(output, ".ball-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // 前回 Index だけでも順序は引き継げる。旧 Ball も指定された場合は、検証してから再利用する。
            if (previousBallPath != null)
            {
                if (previous == null) throw new ArgumentException("A previous Index is required for Ball reuse.");
                await VerifyAsync(previousBallPath, previous, cancellationToken).ConfigureAwait(false);
            }
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamIO.BufferSize, true))
            using (var old = previousBallPath == null ? null : File.OpenRead(previousBallPath))
            {
                foreach (var asset in layout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (old != null && oldAssets!.TryGetValue(asset.Path, out var prior) && prior.Hash == asset.Hash && prior.Size == asset.Size)
                    {
                        old.Position = prior.Offset;
                        await StreamIO.CopyExactlyAsync(old, target, asset.Size, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        RejectLink(sources[asset.Path]);
                        using var source = File.OpenRead(sources[asset.Path]);
                        if (source.Length != asset.Size) throw new IOException($"Input changed during build: {asset.Path}");
                        await StreamIO.CopyExactlyAsync(source, target, asset.Size, cancellationToken).ConfigureAwait(false);
                    }
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(true);
            }
            string hash;
            using (var stream = File.OpenRead(temp)) hash = await StreamIO.HashAsync(stream, cancellationToken).ConfigureAwait(false);
            var index = new BallIndex(BallIndex.FileName(hash), new FileInfo(temp).Length, hash, layout);
            // スキャン後に同じサイズの別内容へ変わった場合も、書き上がったデータのハッシュで検出する。
            await VerifyAsync(temp, index, cancellationToken).ConfigureAwait(false);
            await CommitAsync(temp, output, index, cancellationToken).ConfigureAwait(false);
            return index;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task VerifyAsync(string ballPath, BallIndex index, CancellationToken cancellationToken = default)
    {
        index.Validate();
        using var stream = File.OpenRead(ballPath);
        if (stream.Length != index.Size) throw new InvalidDataException("Ball length mismatch.");
        // Index が隙間のない配置であることを利用し、1 回の逐次読み取りで個別・全体のハッシュを検証する。
        using var ballHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[StreamIO.BufferSize];
        foreach (var asset in index.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = asset.Size;
            while (remaining > 0)
            {
                int count = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining), cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, count); ballHash.AppendData(buffer, 0, count);
                remaining -= count;
            }
            if (StreamIO.Hex(hash.GetHashAndReset()) != asset.Hash)
                throw new InvalidDataException($"Asset hash mismatch: {asset.Path}");
        }
        if (StreamIO.Hex(ballHash.GetHashAndReset()) != index.Hash) throw new InvalidDataException("Ball hash mismatch.");
    }

    /// <summary>検証済みの Ball からアセット範囲だけを公開する。返した Stream の破棄は呼び出し側が担う。</summary>
    public static Stream OpenAsset(string directory, BallIndex index, string assetPath)
    {
        var asset = index.Assets.FirstOrDefault(x => x.Path == assetPath) ?? throw new FileNotFoundException("Asset not found.", assetPath);
        var stream = File.OpenRead(Path.Combine(directory, index.BallFile));
        try { return new SliceStream(stream, asset.Offset, asset.Size); }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>ビルダーとクライアントで共用する書き込み排他。競合時は失敗する。ロックファイル自体は削除しない。</summary>
    public static FileStream AcquireWriter(string directory)
    {
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, ".assetball.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>検証済みの一時 Ball を公開する。呼び出し側で書き込みロックを保持しておくこと。</summary>
    public static async Task CommitAsync(string temporaryBall, string directory, BallIndex index, CancellationToken cancellationToken = default)
    {
        // 移動前にシリアライズし、不正またはサイズ超過の Index を公開手順に進めない。
        byte[] json = IndexJson.Serialize(index);
        string ballPath = Path.Combine(directory, index.BallFile);
        cancellationToken.ThrowIfCancellationRequested();
        // 同じ内容の Ball は再利用する。ただし既存ファイルが破損していれば検証済みのものに置き換える。
        if (File.Exists(ballPath))
        {
            try
            {
                await VerifyAsync(ballPath, index, cancellationToken).ConfigureAwait(false);
                File.Delete(temporaryBall);
            }
            catch (InvalidDataException) { File.Replace(temporaryBall, ballPath, null); }
        }
        else File.Move(temporaryBall, ballPath);
        // Ball を先に確定し、最後に同じディレクトリ内で Index を atomic replace する。
        // 公開前に失敗しても旧 Index は有効。旧 Ball は古い Index の読み手のために残す。
        string tempIndex = Path.Combine(directory, ".index-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(tempIndex, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(json, 0, json.Length, cancellationToken).ConfigureAwait(false);
                file.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            string indexPath = Path.Combine(directory, "index.json");
            if (File.Exists(indexPath)) File.Replace(tempIndex, indexPath, null);
            else File.Move(tempIndex, indexPath);
        }
        finally { if (File.Exists(tempIndex)) File.Delete(tempIndex); }
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(entry);
            if (Directory.Exists(entry))
            { foreach (string file in EnumerateFiles(entry)) yield return file; }
            else yield return entry;
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Symbolic links/reparse points are not supported: {path}");
    }
}
