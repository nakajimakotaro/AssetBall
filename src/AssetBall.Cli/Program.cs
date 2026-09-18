using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using AssetBall.Aws;
using AssetBall.Client;
using AssetBall.Core;

internal static class Program
{
    private const string Help = """
    AssetBall — streaming asset packaging and differential delivery
      build   <input-dir> <output-dir>
      update  <input-dir> <previous-index.json> <output-dir>
      inspect <index.json>
      verify  <index.json> [ball-path]
      diff    <previous-index.json> <next-index.json>
      deploy  <input-dir> <work-dir> --bucket NAME [--prefix PATH] [--region REGION] [--dry-run]
      sync    <https://host/path/index.json> <cache-dir>
    Deploy uses the AWS SDK credential chain. --dry-run performs remote reads and local builds only.
    Output/work directories must be outside the input directory. Ctrl+C cancels safely.
    """;

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help") { Console.WriteLine(Help); return 0; }
            var ct = cancellation.Token;
            switch (args[0])
            {
                case "build":
                    Count(args, 3);
                    PrintIndex(await BallStore.BuildAsync(args[1], args[2], cancellationToken: ct));
                    break;
                case "update":
                    Count(args, 4);
                    var old = await IndexJson.ReadFileAsync(args[2], ct);
                    string oldBall = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!, old.BallFile);
                    PrintIndex(await BallStore.BuildAsync(args[1], args[3], old, oldBall, ct));
                    break;
                case "inspect":
                    Count(args, 2);
                    Console.Write(System.Text.Encoding.UTF8.GetString(IndexJson.Serialize(await IndexJson.ReadFileAsync(args[1], ct))));
                    break;
                case "verify":
                    if (args.Length is < 2 or > 3) throw new ArgumentException("verify requires an Index and an optional Ball path.");
                    var index = await IndexJson.ReadFileAsync(args[1], ct);
                    string ball = args.Length == 3 ? args[2] : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, index.BallFile);
                    await BallStore.VerifyAsync(ball, index, ct);
                    Console.WriteLine("OK: Ball and all asset hashes match.");
                    break;
                case "diff":
                    Count(args, 3);
                    Print(Planner.Compare(await IndexJson.ReadFileAsync(args[1], ct), await IndexJson.ReadFileAsync(args[2], ct)));
                    break;
                case "sync":
                    Count(args, 3);
                    using (var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.None })
                    using (var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
                        PrintIndex(await new AssetBallClient(http).UpdateAsync(new Uri(args[1]), args[2], cancellationToken: ct));
                    break;
                case "deploy":
                    if (args.Length < 5) throw new ArgumentException("deploy requires input, work directory and --bucket.");
                    var options = Options(args.Skip(3).ToArray());
                    if (!options.TryGetValue("--bucket", out var bucket)) throw new ArgumentException("--bucket is required.");
                    var config = new AmazonS3Config { RetryMode = RequestRetryMode.Standard, MaxErrorRetry = 3 };
                    if (options.TryGetValue("--region", out var region)) config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
                    using (var s3 = new AmazonS3Client(config))
                        Print(await new S3Deployer(s3).DeployDirectoryAsync(args[1], args[2], bucket,
                            options.GetValueOrDefault("--prefix", ""), options.ContainsKey("--dry-run"), ct));
                    break;
                default: throw new ArgumentException($"Unknown command: {args[0]}");
            }
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (ex is ArgumentException) Console.Error.WriteLine(Help);
            return 1;
        }
    }

    private static void Count(string[] args, int count)
    { if (args.Length != count) throw new ArgumentException($"Invalid arguments for {args[0]}."); }
    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    private static void PrintIndex(BallIndex index) => Print(new { index.BallFile, index.Size, Assets = index.Assets.Count, index.Hash });
    private static Dictionary<string, string> Options(string[] args)
    {
        var result = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (result.ContainsKey(key)) throw new ArgumentException($"Duplicate option: {key}");
            if (key == "--dry-run") { result.Add(key, "true"); continue; }
            if (key is not ("--bucket" or "--prefix" or "--region") || i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                throw new ArgumentException($"Invalid option: {key}");
            result.Add(key, args[++i]);
        }
        return result;
    }
}
