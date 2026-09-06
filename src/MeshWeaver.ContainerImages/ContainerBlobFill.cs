using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// A write-only stream that TEES an upstream body: every byte goes to the caller's response AND to
/// a temporary file, hashed on the way past, committed to the cache only if the finished hash
/// equals the digest the entry is filed under.
///
/// <para>🚨 <b>Nothing is buffered.</b> This is the same single pass the mirror already made from
/// the upstream socket to the client — the fill adds one write per chunk and one hash update, and
/// never holds more than the copy's own chunk. A layer is hundreds of megabytes and must never
/// land on the heap.</para>
///
/// <para>🚨 <b>Nothing here is bounded separately either.</b> The whole fill — the lazy open, every
/// write, the flush and the move — happens INSIDE the <c>Blob</c> pool slot that
/// <see cref="UpstreamPassthroughResult"/> already holds for the transfer. Re-entering a second
/// pool from inside a slot this code already holds is the nested-gate deadlock
/// <see cref="MeshWeaver.Mesh.Threading.IoPoolNames"/> warns about; riding the existing slot is
/// both correct and the tighter bound, since the disk write cannot outlive the transfer that
/// drives it.</para>
///
/// <para>🚨 <b>The fill can never fail the pull.</b> If the temporary cannot be opened or written,
/// the fill DEGRADES: it stops caching, reports once at warning level, and keeps forwarding every
/// byte downstream. The mirror's contract is to serve the right bytes; the cache is an
/// optimisation on top of that and is not allowed to endanger it.</para>
///
/// <para>🚨 <b>An abandoned fill stores nothing.</b> A client that disconnects mid-layer, an
/// upstream that dies, an assertion that throws — all leave the temporary file, which
/// <see cref="DisposeAsync"/> deletes. And even if one somehow survived, the hash check would
/// refuse it: a partial body cannot hash to the whole body's digest. Two independent reasons a
/// truncated layer can never become a cache entry.</para>
/// </summary>
public sealed class ContainerBlobFill : Stream
{
    private readonly ContainerBlobCache owner;
    private readonly string digest;
    private readonly string? mediaType;
    private readonly string blobPath;
    private readonly string typePath;
    private readonly string temporaryPath;
    private readonly Stream downstream;
    private readonly ILogger logger;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private FileStream? file;
    private bool degraded;
    private bool committed;
    private long written;

    internal ContainerBlobFill(
        ContainerBlobCache owner,
        string digest,
        string? mediaType,
        string blobPath,
        string typePath,
        Stream downstream,
        ILogger logger)
    {
        this.owner = owner;
        this.digest = digest;
        this.mediaType = mediaType;
        this.blobPath = blobPath;
        this.typePath = typePath;
        this.downstream = downstream;
        this.logger = logger;
        temporaryPath = blobPath + "." + Guid.NewGuid().ToString("N")
                        + ContainerBlobCache.TemporarySuffix;
    }

    /// <summary>True once the bytes have been verified and filed. Read after
    /// <see cref="CommitAsync"/>.</summary>
    public bool Committed => committed;

    /// <summary>Bytes forwarded so far.</summary>
    public long BytesWritten => written;

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => written;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => downstream.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        downstream.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// The synchronous write, implemented synchronously.
    ///
    /// <para>🚨 NOT a block on <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.
    /// Bridging the sync override onto the async one would be sync-over-async on whatever thread
    /// happened to call it — the shape this repo bans outright — for no gain, since the work
    /// underneath is a pair of stream writes either way.</para>
    /// </summary>
    /// <param name="buffer">The bytes to forward.</param>
    /// <param name="offset">Offset into <paramref name="buffer"/>.</param>
    /// <param name="count">How many bytes to forward.</param>
    public override void Write(byte[] buffer, int offset, int count)
    {
        var span = buffer.AsSpan(offset, count);
        downstream.Write(span);
        written += count;

        if (degraded)
            return;
        if (file is null && !TryOpen())
            return;

        try
        {
            file!.Write(span);
            hash.AppendData(span);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Degrade(ex, "writing");
        }
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // 🚨 DOWNSTREAM FIRST, always. The caller's bytes are the contract; the cache copy is the
        // by-product. Writing the file first would put a disk stall in front of every byte the
        // client is waiting for.
        await downstream.WriteAsync(buffer, cancellationToken);
        written += buffer.Length;

        if (degraded)
            return;
        if (file is null && !TryOpen())
            return;

        try
        {
            await file!.WriteAsync(buffer, cancellationToken);
            hash.AppendData(buffer.Span);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Degrade(ex, "writing");
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>
    /// Finishes the fill: flushes, hashes, and files the entry under its digest — or discards it.
    ///
    /// <para>🚨 It NEVER throws. By the time this runs the caller already has every byte, so a
    /// failure here is about the cache alone and there is nothing left to fail: it is reported and
    /// the temporary is deleted.</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>True when the entry was stored.</returns>
    public async Task<bool> CommitAsync(CancellationToken cancellationToken)
    {
        if (file is null || degraded)
            return false;
        try
        {
            await file.FlushAsync(cancellationToken);
            await file.DisposeAsync();
            file = null;

            var actual = ContainerBlobCache.Format(hash.GetHashAndReset());
            if (!string.Equals(actual, digest, StringComparison.Ordinal))
            {
                owner.LogDigestMismatch(digest, actual);
                ContainerBlobCache.Delete(temporaryPath);
                return false;
            }

            committed = owner.Publish(temporaryPath, blobPath, typePath, mediaType, written);
            return committed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or OperationCanceledException)
        {
            Degrade(ex, "committing");
            return false;
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (file is not null)
        {
            await file.DisposeAsync();
            file = null;
        }
        hash.Dispose();
        // An uncommitted fill leaves nothing behind — an aborted pull must not litter the cache
        // directory with partial layers. NEVER disposes `downstream`: the response body belongs to
        // the request, not to this stream.
        if (!committed)
            ContainerBlobCache.Delete(temporaryPath);
        await base.DisposeAsync();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        file?.Dispose();
        file = null;
        hash.Dispose();
        if (!committed)
            ContainerBlobCache.Delete(temporaryPath);
    }

    private bool TryOpen()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
            file = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Degrade(ex, "opening");
            return false;
        }
    }

    private void Degrade(Exception ex, string phase)
    {
        degraded = true;
        try
        {
            file?.Dispose();
        }
        catch (IOException)
        {
            // Already broken; the delete below is what matters.
        }
        file = null;
        ContainerBlobCache.Delete(temporaryPath);
        logger.LogWarning(ex,
            "Container registry mirror: the cache fill for {Digest} failed while {Phase} its "
            + "temporary file. The pull is unaffected and continues streaming from the upstream; "
            + "this image simply is not cached.", digest, phase);
    }
}
