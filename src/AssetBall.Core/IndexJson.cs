using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AssetBall.Core;

/// <summary>Explicit JSON mapping avoids reflection-based serialization, including on IL2CPP.</summary>
public static class IndexJson
{
    public const int MaxBytes = 64 * 1024 * 1024;

    public static async Task<BallIndex> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
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
            var root = JObject.Parse(new UTF8Encoding(false, true).GetString(memory.ToArray()),
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (Text(root, "format") != BallIndex.Format || Number(root, "version") != BallIndex.Version ||
                Text(root, "hashAlgorithm") != "sha256") throw new InvalidDataException("Unsupported Index format.");
            var ball = root["ball"] as JObject ?? throw new InvalidDataException("Missing ball.");
            var assets = root["assets"] as JArray ?? throw new InvalidDataException("Missing assets.");
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
        // Json.NET's formatted output uses platform newlines; canonicalize them for reproducibility.
        var bytes = Encoding.UTF8.GetBytes(root.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n");
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Index exceeds 64 MiB limit.");
        return bytes;
    }

    public static async Task<BallIndex> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static string Text(JToken token, string key) => token is JObject obj && obj[key]?.Type == JTokenType.String
        ? (string)obj[key]! : throw new InvalidDataException($"Missing string: {key}");
    private static long Number(JToken token, string key) => token is JObject obj && obj[key]?.Type == JTokenType.Integer
        ? (long)obj[key]! : throw new InvalidDataException($"Missing integer: {key}");
}
