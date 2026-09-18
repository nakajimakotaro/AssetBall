using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AssetBall.Core;

/// <summary>JSON とモデルの境界。フィールドを明示的に対応付け、IL2CPP でもモデルのリフレクションに依存しない。</summary>
public static class IndexJson
{
    public const int MaxBytes = 64 * 1024 * 1024;

    public static async Task<BallIndex> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        // Content-Length のない HTTP 応答でも、読み取り中に上限を適用する。
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaxBytes) throw new InvalidDataException("Index exceeds 64 MiB limit.");
            memory.Write(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // 不正 UTF-8 と重複キーを拒否し、同じ JSON が異なる意味に解釈される曖昧さを避ける。
            var root = JObject.Parse(new UTF8Encoding(false, true).GetString(memory.ToArray()),
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (Text(root, "format") != BallIndex.Format || Number(root, "version") != BallIndex.Version ||
                Text(root, "hashAlgorithm") != "sha256") throw new InvalidDataException("Unsupported Index format.");
            var ball = root["ball"] as JObject ?? throw new InvalidDataException("Missing ball.");
            var assets = root["assets"] as JArray ?? throw new InvalidDataException("Missing assets.");
            // ここでは必須フィールドの型を検査し、配置全体の整合性は BallIndex の構築時に検査する。
            // 未知フィールドは読み飛ばすので、対応形式内の拡張を許容できる。
            return new BallIndex(Text(ball, "file"), Number(ball, "size"), Text(ball, "hash"),
                assets.Select(a => new AssetEntry(Text(a, "path"), Number(a, "offset"), Number(a, "size"), Text(a, "hash"))));
        }
        catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is OverflowException ||
                                   ex is ArgumentException || ex is InvalidOperationException)
        { throw new InvalidDataException("Invalid Index JSON.", ex); }
    }

    public static byte[] Serialize(BallIndex index)
    {
        index.Validate();
        var root = new JObject
        {
            ["format"] = BallIndex.Format, ["version"] = BallIndex.Version, ["hashAlgorithm"] = "sha256",
            ["ball"] = new JObject { ["file"] = index.BallFile, ["size"] = index.Size, ["hash"] = index.Hash },
            ["assets"] = new JArray(index.Assets.Select(a => new JObject
            { ["path"] = a.Path, ["offset"] = a.Offset, ["size"] = a.Size, ["hash"] = a.Hash }))
        };
        // Json.NET の整形結果は OS の改行に依存するため、LF に統一して出力を再現可能にする。
        var bytes = Encoding.UTF8.GetBytes(root.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n");
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Index exceeds 64 MiB limit.");
        return bytes;
    }

    public static async Task<BallIndex> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        // 読み取り中でも公開側が Index を置き換えられるよう、削除共有を許可する。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static string Text(JToken token, string key) => token is JObject obj && obj[key]?.Type == JTokenType.String
        ? (string)obj[key]! : throw new InvalidDataException($"Missing string: {key}");
    private static long Number(JToken token, string key) => token is JObject obj && obj[key]?.Type == JTokenType.Integer
        ? (long)obj[key]! : throw new InvalidDataException($"Missing integer: {key}");
}
