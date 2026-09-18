# 動作確認

[English](VALIDATION.md) | 日本語

## このワークスペースでの実行結果

2026-09-18、macOS arm64 / .NET SDK 10.0.401 で確認しました。

- ソリューション全8プロジェクトの Release ビルド: 警告0、エラー0。
- xUnit: 43件成功、失敗0、スキップ0。S3 はテスト用 SDK クライアントで確認。
- RangeServer と CLI を実際のループバック HTTP で接続: `build / inspect / verify / update / diff / sync` 成功。
- 初回14,336バイトの取得後、変更・削除・追加を含む更新で8,192バイトを再利用し、14バイトを1 Range で取得。更新後の Ball バイト列一致と全ハッシュを確認。
- Ball リクエストは初回・更新の2回で、どちらも206。追加の無変更 sync では Ball リクエスト0。
- 計画ベンチマークの1回の参考値: 10万アセット、論理サイズ104,857,600,000バイトに対し92 ms、メタデータ割り当て58,972,344バイト。HTTP 1 Range、S3 10,000 Parts、コピー94,352,965,632バイト、アップロード10,504,634,368バイト。実データの I/O は含まない。

実 AWS アカウント、Unity Editor / IL2CPP、Windows / Linux 上では未実行です。以下の手順と CI は、その追加確認用です。

## 自動テスト

```sh
dotnet test AssetBall.sln -c Release
```

`tests/AssetBall.Tests` で次を確認します。

- 配置順、変更・追加・削除、複数世代、内容の復帰、再現性。
- ランダムな更新履歴に対する全バイトの過不足ない計画。
- JSON の必須フィールド、対応版、重複、パス、整数オーバーフロー。
- 実ファイルでの生成、再利用、検証、破損検知、空 Ball、読み出し範囲。
- キャンセル、書き込み競合、入力内の出力先、シンボリックリンク。
- HttpMessageHandler を使った初回取得、差分、無変更、削除、空 Ball、破損キャッシュ修復。
- 不正な HTTP ステータス、Content-Range、圧縮、破損、短い／長い応答、キャンセル後の旧状態保持。
- Multipart のサイズ・数制約、大きなオフセット、断片化、小さいコピー元。
- AWS SDK クライアントのテスト用実装を使った初回・コピー更新、dry-run、チェックサム、条件付き公開、競合、失敗時の Abort。

AWS のテストは外部サービスにアクセスせず、実バケットへの課金や書き込みは発生しません。SDK の HTTP 通信や AWS の権限設定・サービス挙動をこのテストだけで保証するものではありません。

## ローカル HTTP の確認

[README](../README.ja.md) の例で `artifacts/v1` を生成後、別ターミナルで RangeServer を起動します。

```sh
dotnet run --project samples/RangeServer -- artifacts/v1 --urls http://127.0.0.1:5080
```

```sh
dotnet run --project src/AssetBall.Cli -- sync http://127.0.0.1:5080/index.json artifacts/client

# assets を変更後、同じ配信ディレクトリを更新
dotnet run --project src/AssetBall.Cli -- update assets artifacts/v1/index.json artifacts/v1
dotnet run --project src/AssetBall.Cli -- sync http://127.0.0.1:5080/index.json artifacts/client
dotnet run --project src/AssetBall.Cli -- verify artifacts/client/index.json
```

最初は全範囲、その後は変更範囲だけの `206` が返ることを確認します。連続する変更は1 Range にまとまります。開発サーバーはアセットの生バイト列を変換しません。

## 実 S3 での確認手順

テスト用の一般用途バケットと専用 prefix を指定してください。

1. 6 MiB 以上の未変更用ファイルと小さい変更用ファイルを用意する。
2. `deploy --dry-run` で初回計画を確認し、通常の `deploy` を実行する。
3. 配信 URL または認証済み HttpClient で Index と Ball を取得し、`verify` する。
4. 小さいファイルだけを変更。dry-run の `CopyBytes` が大きい未変更ファイルのサイズ以上になり、`UploadBytes` が変更量に近いことを確認する。
5. 再デプロイ後にクライアントを更新し、旧 Ball が引き続きアクセス可能で、新しい Index の全ハッシュが一致することを確認する。
6. 異なる更新を同時に実行し、一方の条件付き Index 公開が競合検出で停止することを確認する。
7. アップロード中にキャンセルし、Index が旧状態を指すこと、通常の中断経路で Multipart が Abort されることを確認する。

Complete 成功後に応答が失われるなど、結果が不明になる障害では Index を公開せず、再実行で回復します。プロセス強制終了時や Abort 自体の障害に備え、未完了 Multipart を期限付きで消す Lifecycle を設定できます。

## Unity での確認手順

Unity の .NET Standard 2.1 API プロファイルを想定します。

1. `dotnet build src/AssetBall.Client -c Release` を実行。
2. `src/AssetBall.Core/bin/Release/netstandard2.1/AssetBall.Core.dll` と `src/AssetBall.Client/bin/Release/netstandard2.1/AssetBall.Client.dll` を Unity の `Assets/Plugins/AssetBall/` に配置。
3. Unity Package Manager の `com.unity.nuget.newtonsoft-json`（Newtonsoft.Json 13.x を含むもの）を追加。同名 DLL を重複配置しないでください。
4. [Unity サンプル](../samples/Unity/AssetBallExample.cs) を参照。保存先は呼び出し側から渡します。

サンプルは Unity 非依存の .NET Standard 2.1 プロジェクトとしてもコンパイルされます。

Mono の Editor と対象プラットフォームの IL2CPP ビルドの両方で、初回取得、差分取得、進捗通知、キャンセル、アプリ再起動、アセット読み出しを確認してください。保存ディレクトリは Unity 側から渡し、UI 更新はメインスレッドで行います。WebGL は対象外です。
