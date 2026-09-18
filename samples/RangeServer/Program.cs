using AssetBall.Core;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project samples/RangeServer -- <ball-directory> [--urls http://127.0.0.1:5080]");
    return 1;
}
string root = Path.GetFullPath(args[0]);
if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
// ローカルで差分取得を試す配信例。先頭引数を配信先とし、残りは ASP.NET Core の起動設定に渡す。
var builder = WebApplication.CreateBuilder(args.Skip(1).ToArray());
var app = builder.Build();
app.MapGet("/{file}", (string file, HttpContext context) =>
{
    // 配信対象を Index と内容ハッシュ付き Ball に限定し、作業中の一時ファイルなどを公開しない。
    bool isIndex = file == "index.json";
    bool isBall = file.StartsWith("ball-", StringComparison.Ordinal) && file.EndsWith(".bin", StringComparison.Ordinal) &&
        BallIndex.IsHash(file.Substring(5, file.Length - 9));
    if (!isIndex && !isBall) return Results.NotFound();
    string path = Path.Combine(root, file);
    if (!File.Exists(path)) return Results.NotFound();
    // 可変の Index は再検証、内容ごとに名前が変わる Ball は長期キャッシュする。
    context.Response.Headers.CacheControl = isIndex ? "no-cache" : "public,max-age=31536000,immutable";
    // Range の解析と 206 応答の生成は ASP.NET Core に任せ、Ball のバイト列をそのまま返す。
    return Results.File(path, isIndex ? "application/json" : "application/octet-stream", enableRangeProcessing: isBall);
});
await app.RunAsync();
return 0;
