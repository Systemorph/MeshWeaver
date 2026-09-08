namespace MeshWeaver.Mesh.Persistence;

/// <summary>
/// The volume kept fewer bytes than a write handed it and reported no error — the shape a FULL
/// share produces. Raised by <see cref="AtomicFileWrite.PublishBytesWith(string, byte[], Func{string, Stream})"/>
/// after the flush-to-disk and the length read-back disagree with the bytes written, and carried
/// through the compile pipeline as the reason a compile was REFUSED rather than published.
///
/// <para>An <see cref="IOException"/> by inheritance so every existing "the store faulted" catch
/// sees it; a distinct type so the one branch that must NOT treat an IO fault as "the other
/// writer won" (<c>AtomicFileWrite.PublishBytes</c>'s rename race) can tell them apart.</para>
/// </summary>
public sealed class ShortWriteException : IOException
{
    /// <summary>Creates the exception for a short write at <paramref name="path"/>.</summary>
    /// <param name="path">The staging path the bytes were written to.</param>
    /// <param name="expectedBytes">How many bytes were handed to the write.</param>
    /// <param name="landedBytes">How many the volume kept after flush and close; <c>-1</c> when
    /// the file is not there at all.</param>
    /// <param name="capacity">The volume's capacity at the time of the write, or <c>null</c> when
    /// it could not be read.</param>
    public ShortWriteException(string path, long expectedBytes, long landedBytes, VolumeCapacity? capacity)
        : base(Describe(path, expectedBytes, landedBytes, capacity))
    {
        Path = path;
        ExpectedBytes = expectedBytes;
        LandedBytes = landedBytes;
        Capacity = capacity;
    }

    /// <summary>The staging path the bytes were written to.</summary>
    public string Path { get; }

    /// <summary>How many bytes were handed to the write.</summary>
    public long ExpectedBytes { get; }

    /// <summary>How many bytes the volume kept; <c>-1</c> when the file is absent.</summary>
    public long LandedBytes { get; }

    /// <summary>The volume's capacity at the time of the write, when it could be read.</summary>
    public VolumeCapacity? Capacity { get; }

    private static string Describe(string path, long expectedBytes, long landedBytes, VolumeCapacity? capacity)
    {
        var landed = landedBytes < 0 ? "no file at all" : $"{landedBytes:N0} byte(s)";
        var volume = capacity is { } c
            ? $" The volume holding it has {c.FreeMiB:N0} MiB free of {c.TotalMiB:N0} MiB."
            : string.Empty;
        return $"Wrote {expectedBytes:N0} byte(s) to '{path}' but the volume kept {landed} — "
            + "a write on a full volume reports success and lands short, so these bytes were NOT "
            + $"published.{volume} Free space on the volume, then recompile.";
    }
}
