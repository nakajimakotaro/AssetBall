# AssetBall

多数のアセットを1つのファイルにまとめ、変更部分を HTTP Range Request で取得する仕組みの C# 参考実装です。

各プロジェクトの配信環境や運用に合わせて、設計やコードを参考にしながらカスタマイズすることを想定しています。アセットの配置・差分取得・S3 へのアップロードを実装しています。

設計の背景や仕組みの詳しい解説は、Qiita 記事をご覧ください。

**[Unityの1万超アセット配信を3時間→13分にした設計](https://qiita.com/harusann2/items/50d638e7a3c2d76e0531)**

## 仕組み

アセットを連結した **Ball** と、位置・サイズ・ハッシュを記録した **Index** を生成します。未変更アセットの順序を保ち、変更・追加されたアセットを末尾に集めることで、差分をまとめて取得しやすくします。

```text
初回:      A | B | C | D
B を更新:  A | C | D | B′
D を更新:  A | C | B′ | D′
```

クライアントは Index を比較し、必要な連続範囲を HTTP Range Request で取得します。S3 への更新では、未変更部分を `UploadPartCopy` で再利用します。

## コードの構成

| ディレクトリ | 内容 |
| --- | --- |
| [src/AssetBall.Core](src/AssetBall.Core) | Index、アセットの配置、差分計画、Ball の生成・検証 |
| [src/AssetBall.Client](src/AssetBall.Client) | HTTP による差分取得、ローカル更新、アセット読み出し |
| [src/AssetBall.Aws](src/AssetBall.Aws) | S3 へのアップロード・更新 |
| [src/AssetBall.Cli](src/AssetBall.Cli) | 生成・比較・取得・デプロイ用 CLI |
| [samples](samples) | ローカル配信サーバー、Unity 向け利用例 |

Core / Client は .NET Standard 2.1、AWS / CLI は .NET 10 を対象としています。

## 試す

.NET 10 SDK を用意し、リポジトリ直下で実行します。任意のファイルを `assets/` に配置してください。

```sh
# Ball と Index を生成
dotnet run --project src/AssetBall.Cli -- build assets artifacts/v1

# assets の一部を変更・追加・削除した後に更新
dotnet run --project src/AssetBall.Cli -- update assets artifacts/v1/index.json artifacts/v2

# 差分を確認
dotnet run --project src/AssetBall.Cli -- diff artifacts/v1/index.json artifacts/v2/index.json
```

出力は `index.json` と `ball-<UTC日時>-<SHA-256>.bin` です。日時は `20260918T072345.1234567Z` のような固定桁で、ファイル名順に日時順で並びます。ビルドごとに日時を付与します。その他のコマンドは `dotnet run --project src/AssetBall.Cli -- --help` で確認できます。

## カスタマイズについて

配信先や認証、キャッシュの保存方法・世代管理、アプリへの組み込みは、各現場の要件に合わせて調整してください。この実装はローカルにも Ball を保持し、アセットを Stream として読み出します。

Unity 向けには [利用例](samples/Unity/AssetBallExample.cs) を用意しています。実 AWS 環境や Unity Editor / IL2CPP / 実機での動作は未検証です。

- [ファイル形式・実装上の設計判断](spec/FORMAT.ja.md)
- [テスト・動作確認手順](spec/VALIDATION.ja.md)

## ライセンス

Copyright (c) 2026 AssetBall contributors

[GNU General Public License v3.0](LICENSE)（`GPL-3.0-only`）
