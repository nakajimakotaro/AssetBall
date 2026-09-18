# AssetBall

English | [日本語](README.ja.md)

A C# reference implementation that bundles many assets into a single file and fetches changes using HTTP Range requests.

Use the design and code as a starting point, and customize them for your project's delivery infrastructure and operational needs. The implementation covers asset layout, incremental downloads, and uploads to S3.

For the design background and a detailed explanation, see the original Qiita article (in Japanese):

**[The design that reduced delivery time for 10,000+ Unity assets from 3 hours to 13 minutes](https://qiita.com/harusann2/items/50d638e7a3c2d76e0531)**

## How it works

AssetBall generates a **Ball** containing concatenated assets and an **Index** recording their offsets, sizes, and hashes. It preserves the order of unchanged assets and moves changed or added assets to the end, making it easier to fetch changes in contiguous ranges.

```text
Initial:   A | B | C | D
Update B:  A | C | D | B′
Update D:  A | C | B′ | D′
```

The client compares indexes and fetches the required contiguous ranges using HTTP Range requests. S3 updates reuse unchanged data through `UploadPartCopy`.

## Code structure

| Directory | Contents |
| --- | --- |
| [src/AssetBall.Core](src/AssetBall.Core) | Index, asset layout, update planning, Ball generation and verification |
| [src/AssetBall.Client](src/AssetBall.Client) | Incremental HTTP downloads, local updates, asset reading |
| [src/AssetBall.Aws](src/AssetBall.Aws) | S3 uploads and updates |
| [src/AssetBall.Cli](src/AssetBall.Cli) | CLI for building, comparing, fetching, and deploying |
| [samples](samples) | Local delivery server and Unity usage example |

Core / Client target .NET Standard 2.1. AWS / CLI target .NET 10.

## Try it

Install the .NET 10 SDK and run these commands from the repository root. Place any files you want to bundle in `assets/`.

```sh
# Generate a Ball and Index
dotnet run --project src/AssetBall.Cli -- build assets artifacts/v1

# Update after changing, adding, or deleting files in assets
dotnet run --project src/AssetBall.Cli -- update assets artifacts/v1/index.json artifacts/v2

# Inspect the differences
dotnet run --project src/AssetBall.Cli -- diff artifacts/v1/index.json artifacts/v2/index.json
```

The output files are `index.json` and `ball-<SHA-256>.bin`. Run `dotnet run --project src/AssetBall.Cli -- --help` for other commands.

## Customization

Adapt delivery endpoints, authentication, cache storage and version retention, and application integration to your requirements. This implementation also keeps a local Ball and exposes assets as streams.

A [Unity usage example](samples/Unity/AssetBallExample.cs) is included.
- [File format and implementation decisions](spec/FORMAT.md)
- [Tests and validation procedures](spec/VALIDATION.md)

## License

Copyright (c) 2026 AssetBall contributors

[GNU General Public License v3.0](LICENSE) (`GPL-3.0-only`)
