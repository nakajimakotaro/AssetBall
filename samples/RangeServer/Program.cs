using AssetBall.Core;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project samples/RangeServer -- <ball-directory> [--urls http://127.0.0.1:5080]");
    return 1;
}
string root = Path.GetFullPath(args[0]);
if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
var builder = WebApplication.CreateBuilder(args.Skip(1).ToArray());
var app = builder.Build();
app.MapGet("/{file}", (string file, HttpContext context) =>
{
    bool isIndex = file == "index.json";
    bool isBall = file.StartsWith("ball-", StringComparison.Ordinal) && file.EndsWith(".bin", StringComparison.Ordinal) &&
        BallIndex.IsHash(file.Substring(5, file.Length - 9));
    if (!isIndex && !isBall) return Results.NotFound();
    string path = Path.Combine(root, file);
    if (!File.Exists(path)) return Results.NotFound();
    context.Response.Headers.CacheControl = isIndex ? "no-cache" : "public,max-age=31536000,immutable";
    return Results.File(path, isIndex ? "application/json" : "application/octet-stream", enableRangeProcessing: isBall);
});
await app.RunAsync();
return 0;
