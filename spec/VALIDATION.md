# Validation

English | [日本語](VALIDATION.ja.md)

## Results recorded in this workspace

Verified on 2026-09-18 using macOS arm64 / .NET SDK 10.0.401.

- Release build of all 8 projects in the solution: 0 warnings, 0 errors.
- xUnit: 43 passed, 0 failed, 0 skipped. S3 behavior was checked using a test SDK client.
- RangeServer and CLI connected over real loopback HTTP: `build / inspect / verify / update / diff / sync` succeeded.
- After an initial download of 14,336 bytes, an update containing changes, deletions, and additions reused 8,192 bytes and fetched 14 bytes in 1 Range request. The updated Ball's bytes and all hashes were verified.
- There were 2 Ball requests, one for the initial download and one for the update, both returning 206. An additional sync with no changes made 0 Ball requests.
- One illustrative planning benchmark run: 100,000 assets with a logical size of 104,857,600,000 bytes took 92 ms and allocated 58,972,344 bytes for metadata. The plan used 1 HTTP Range and 10,000 S3 parts, copying 94,352,965,632 bytes and uploading 10,504,634,368 bytes. This excludes actual data I/O.

These checks have not been run against a real AWS account, in Unity Editor / IL2CPP, or on Windows / Linux. The procedures below and CI support that additional validation.

## Automated tests

```sh
dotnet test AssetBall.sln -c Release
```

`tests/AssetBall.Tests` covers:

- Layout order, changes, additions, deletions, multiple generations, reverted content, and reproducibility.
- Plans that account for every byte exactly once across random update histories.
- Required JSON fields, supported versions, duplicates, paths, and integer overflow.
- Building, reuse, verification, corruption detection, empty Balls, and read boundaries using real files.
- Cancellation, concurrent writes, output paths within the input directory, and symbolic links.
- Initial downloads, incremental updates, unchanged content, deletions, empty Balls, and corrupt cache repair using HttpMessageHandler.
- Invalid HTTP status codes, Content-Range, compression, corruption, short or long responses, and preservation of the previous state after cancellation.
- Multipart size and count constraints, large offsets, fragmentation, and small copy sources.
- Initial uploads, updates using copies, dry runs, checksums, conditional publication, conflicts, and aborting on failure using a test implementation of the AWS SDK client.

AWS tests do not access external services, incur real bucket charges, or write to real buckets. These tests alone do not verify SDK HTTP communication, AWS permissions, or service behavior.

## Local HTTP validation

After generating `artifacts/v1` with the [README](../README.md) example, start RangeServer in a separate terminal:

```sh
dotnet run --project samples/RangeServer -- artifacts/v1 --urls http://127.0.0.1:5080
```

```sh
dotnet run --project src/AssetBall.Cli -- sync http://127.0.0.1:5080/index.json artifacts/client

# After modifying assets, update the same serving directory
dotnet run --project src/AssetBall.Cli -- update assets artifacts/v1/index.json artifacts/v1
dotnet run --project src/AssetBall.Cli -- sync http://127.0.0.1:5080/index.json artifacts/client
dotnet run --project src/AssetBall.Cli -- verify artifacts/client/index.json
```

Confirm that `206` responses cover the full range initially and only changed ranges afterward. Contiguous changes are combined into 1 Range request. The development server does not transform raw asset bytes.

## Validation against real S3

Use a general purpose bucket for testing and a dedicated prefix.

1. Prepare a file of at least 6 MiB to leave unchanged and a small file to modify.
2. Inspect the initial plan with `deploy --dry-run`, then run a normal `deploy`.
3. Fetch the Index and Ball through the delivery URL or an authenticated HttpClient, and run `verify`.
4. Modify only the small file. Confirm that the dry run's `CopyBytes` is at least the size of the large unchanged file, and that `UploadBytes` is close to the changed byte count.
5. After redeploying, update the client and confirm that the old Ball remains accessible and all hashes in the new Index match.
6. Run different updates concurrently and confirm that one conditional Index publication stops after detecting a conflict.
7. Cancel during upload and confirm that the Index still references the previous state and that the multipart upload is aborted through the normal cancellation path.

For failures with an unknown outcome, such as losing the response after a successful completion, the Index is not published; rerunning recovers the operation. A lifecycle rule can expire incomplete multipart uploads to handle process termination or failures of the abort operation itself.

## Unity validation

The expected API profile is Unity's .NET Standard 2.1.

1. Run `dotnet build src/AssetBall.Client -c Release`.
2. Place `src/AssetBall.Core/bin/Release/netstandard2.1/AssetBall.Core.dll` and `src/AssetBall.Client/bin/Release/netstandard2.1/AssetBall.Client.dll` in Unity's `Assets/Plugins/AssetBall/`.
3. Add `com.unity.nuget.newtonsoft-json` through Unity Package Manager, using a version that includes Newtonsoft.Json 13.x. Avoid duplicate DLLs with the same name.
4. Refer to the [Unity sample](../samples/Unity/AssetBallExample.cs). The caller supplies the storage directory.

The sample also compiles as a .NET Standard 2.1 project without a Unity dependency.

Check initial downloads, incremental updates, progress notifications, cancellation, application restarts, and asset reading in both the Mono Editor and an IL2CPP build for the target platform. Pass the storage directory from Unity and update the UI on the main thread. WebGL is not supported.
