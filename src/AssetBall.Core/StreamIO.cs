using System.Security.Cryptography;

namespace AssetBall.Core;

/// <summary>固定サイズのバッファで処理し、Ball 全体のサイズに比例するメモリ確保を避ける共通 I/O。</summary>
public static class StreamIO
{
    public const int BufferSize = 128 * 1024;

    public static async Task<string> HashAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int count;
        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, count);
        return Hex(hash.GetHashAndReset());
    }

    // 通常の CopyTo と異なり、アセットや Range の境界で止める。途中の EOF は破損として扱う。
    public static async Task CopyExactlyAsync(Stream source, Stream target, long length,
        CancellationToken cancellationToken = default, Action<int>? onBytes = null)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        var buffer = new byte[BufferSize];
        while (length > 0)
        {
            int count = await source.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, length), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Unexpected end of asset data.");
            await target.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
            length -= count;
            onBytes?.Invoke(count);
        }
    }

    internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}

/// <summary>指定範囲を独立した読み取り専用 Stream として見せる。Seek と Read は範囲外へ出られない。</summary>
public sealed class SliceStream : Stream
{
    private readonly Stream inner;
    private readonly long start;
    private readonly long length;
    private readonly bool leaveOpen;
    // 利用者にはアセット先頭を 0 とする位置を見せ、読み取り時だけ Ball 内の絶対位置に変換する。
    private long position;
    private bool disposed;

    // 既定では元 Stream の所有権も引き受ける。同じ元 Stream を使う並行読み取りは想定しない。
    public SliceStream(Stream inner, long start, long length, bool leaveOpen = false)
    {
        if (!inner.CanSeek || !inner.CanRead || start < 0 || length < 0 || start > inner.Length - length)
            throw new ArgumentException("Invalid stream slice.");
        this.inner = inner; this.start = start; this.length = length; this.leaveOpen = leaveOpen;
        inner.Position = start;
    }

    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        CheckDisposed();
        inner.Position = start + position;
        int read = inner.Read(buffer, offset, (int)Math.Min(count, length - position));
        position += read;
        return read;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        CheckDisposed();
        inner.Position = start + position;
        int read = await inner.ReadAsync(buffer, offset, (int)Math.Min(count, length - position), cancellationToken).ConfigureAwait(false);
        position += read;
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        CheckDisposed();
        long next = checked((origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? position :
            origin == SeekOrigin.End ? length : throw new ArgumentOutOfRangeException(nameof(origin))) + offset);
        if (next < 0 || next > length) throw new IOException("Seek outside stream slice.");
        position = next;
        return position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing && !leaveOpen) inner.Dispose();
        disposed = true;
        base.Dispose(disposing);
    }
    private void CheckDisposed() { if (disposed) throw new ObjectDisposedException(nameof(SliceStream)); }
}
