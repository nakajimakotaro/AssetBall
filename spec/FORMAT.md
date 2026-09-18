# AssetBall format v1

English | [日本語](FORMAT.ja.md)

## Files

`index.json` is UTF-8 JSON (written without a BOM, using LF line endings). `version` identifies the format version, not a release number. Ball names use `ball-<UTC timestamp>-<SHA-256>.bin`, with a fixed-width timestamp in `yyyyMMddTHHmmss.fffffffZ` format, so ordinal filename sorting follows timestamp order. The timestamp is assigned after writing the Ball. The hash distinguishes different content generated at the same time. Ball bytes and layout are reproducible from the same input and previous Index; each build receives a new timestamp. Absolute paths are omitted.

```json
{
  "format": "assetball",
  "version": 1,
  "hashAlgorithm": "sha256",
  "ball": {
    "file": "ball-20260918T072345.1234567Z-e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855.bin",
    "size": 0,
    "hash": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
  },
  "assets": []
}
```

Each entry in `assets` requires `path`, `offset`, `size`, and `hash`. Numbers are nonnegative signed 64-bit integers. Ranges are `[offset, offset + size)`, expressed in HTTP as `bytes=offset-(offset+size-1)`. Zero-length assets do not produce Range requests.

A Ball contains no headers, padding, compression, or file names. The order of `assets` matches their order in the Ball. Each asset's `hash` is the SHA-256 of its raw bytes; `ball.hash` is the SHA-256 of the entire Ball's raw bytes. Both use 64 lowercase hexadecimal digits. An S3 multipart composite checksum is distinct from `ball.hash`.

## Validation

In addition to the [JSON Schema](index.schema.json), the implementation checks the following:

- `format` / `version` / `hashAlgorithm` must have supported values.
- `ball.file` must be a single `ball-*.bin` filename containing only letters, digits, `-`, `_`, and `.`. The timestamp and hash embedded in the name are not validated; content integrity is verified against `ball.hash`.
- Paths must be nonempty, relative, and `/`-separated. Empty components, `.`, `..`, backslashes, colons, and control characters are rejected.
- Paths must be unique using case-sensitive comparison. Unicode normalization is not performed.
- The first offset must be 0, and each subsequent offset must equal the end of the previous asset. Gaps, overlaps, and overflow are not allowed.
- The final end offset must equal `ball.size`.
- Missing required JSON fields, incorrect types, and duplicate properties are rejected. Unknown fields are ignored.
- Input and output indexes are limited to 64 MiB. Ball verification checks the actual size, every asset hash, and the overall hash.

Identical content at different paths is not deduplicated. All files are stored, so renaming a file is treated as a deletion and an addition.

## Layout and transfer algorithms

1. Compare inputs by path, SHA-256, and size.
2. Keep unchanged entries in their previous Index order.
3. Append changed and added entries, sorting paths with `StringComparer.Ordinal`.
4. Recalculate contiguous offsets starting at 0 and omit deleted entries.

`Planner.Layout` operates only on the previous Index and scan results, without I/O. `Planner.Compare` computes source offsets, destination offsets, copy lengths, and missing ranges from the old and new indexes. Local copy ranges are merged only when both source and destination are contiguous. Missing ranges are merged when their destinations are contiguous.

The S3 `MultipartPlanner` converts these shared copy ranges into parts that satisfy S3 constraints. It reports changed bytes separately from bytes that must be uploaded again because of those constraints. S3-specific constraints remain outside Core.

## Publication decisions

Overwriting a single fixed Ball name would create a window in which clients holding an old Index access the new Ball. For this reason, the builder generates Ball names containing a timestamp and hash. Ball generation, retrieval, and verification are completed before switching the Index.

Locally, the Index is replaced atomically within the same directory. Writes are serialized using an exclusive lock. Full durability of directory entries across an OS crash or power failure is not guaranteed. After an interruption, the Ball referenced by the Index can be verified and fetched again if corrupted.

On S3, after completing the upload to a new key, the Index is published through PutObject with `If-Match` (`If-None-Match: *` for the initial publication). A conflicting update cannot overwrite a newer Index. Previous Balls are retained, allowing downloads using old indexes to continue. Do not overwrite Ball objects through processes outside this implementation.

The local cache is stored as a Ball, whereas the original article stores individual files. This choice follows the implementation requirements for local Ball updates and arbitrary asset reading; the layout algorithm is shared.
