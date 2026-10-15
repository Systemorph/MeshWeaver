using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// A memory buffer that refuses to grow past a limit: the write that would exceed it throws
/// <see cref="InvalidDataException"/>, which faults the transfer copying into it. A registry
/// answer's size is the registry's CLAIM; a reader that bounds the bytes as they ARRIVE refuses an
/// oversized body while it is read, never after it has been held in memory whole.
/// </summary>
/// <param name="limit">The most bytes the buffer accepts.</param>
/// <param name="what">What the bytes are meant to be, for the refusal (e.g. "the release record").</param>
internal sealed class BoundedBuffer(long limit, string what) : MemoryStream
{
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        Guard(count);
        base.Write(buffer, offset, count);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Guard(buffer.Length);
        base.Write(buffer);
    }

    /// <inheritdoc />
    public override void WriteByte(byte value)
    {
        Guard(1);
        base.WriteByte(value);
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Guard(count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Guard(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    private void Guard(int count)
    {
        if (Length + count > limit)
            throw new InvalidDataException($"{what} is larger than {limit} byte(s), so it is refused as it arrives");
    }
}
