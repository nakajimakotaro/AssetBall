using System.Security.Cryptography;
using System.Text;
using AssetBall.Core;

namespace AssetBall.Tests;

internal sealed class Workspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "assetball-tests-" + Guid.NewGuid().ToString("N"));
    public Workspace() => Directory.CreateDirectory(Root);
    public string Dir(string name) { string path = Path.Combine(Root, name); Directory.CreateDirectory(path); return path; }
    public string Put(string directory, string name, string value)
    {
        string path = Path.Combine(Dir(directory), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value, new UTF8Encoding(false));
        return path;
    }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal static class Fixtures
{
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static AssetEntry Entry(string path, long size = 10, string value = "original") => new(path, 0, size, Hash(value));
    public static BallIndex Index(IEnumerable<AssetEntry> assets, BallIndex? previous = null)
    {
        var layout = Planner.Layout(assets, previous);
        string hash = Hash("synthetic index for pure planner tests");
        return new BallIndex(BallIndex.FileName(hash, DateTimeOffset.UnixEpoch), layout.Sum(a => a.Size), hash, layout);
    }
}
