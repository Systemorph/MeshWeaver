namespace MeshWeaver.Mesh.Persistence;

/// <summary>
/// The volume REFUSED a write because it is out of space — the operating system answered
/// <c>ENOSPC</c> ("No space left on device") or <c>EDQUOT</c> (a share quota). Raised by
/// <see cref="AtomicFileWrite.PublishBytesWith(string, byte[], Func{string, Stream})"/> in place of
/// the bare <see cref="IOException"/> the runtime throws, so the refusal NAMES its cause and carries
/// the volume's capacity at the moment of the write (#6940).
///
/// <para>The bare exception said only <c>No space left on device : '/data/assembly-cache/…/.tmp-….pdb.tmp'</c>
/// — the path of a staging file, which reads like a defect in the assembly store. This one says the
/// VOLUME is full, how full, and that nothing was published, so an operator reading a compile that
/// failed for it goes to the disk rather than to the source.</para>
///
/// <para>An <see cref="IOException"/> by inheritance, like <see cref="ShortWriteException"/> (its
/// sibling for a full volume that does NOT report the fault), so every existing "the store faulted"
/// branch still sees it.</para>
/// </summary>
public sealed class VolumeFullException : IOException
{
    /// <summary>Creates the refusal for a write to <paramref name="path"/>.</summary>
    /// <param name="path">The file the bytes were to be published under.</param>
    /// <param name="attemptedBytes">How many bytes the write carried.</param>
    /// <param name="capacity">The volume's capacity read after the refusal, or <c>null</c> when it
    /// could not be read.</param>
    /// <param name="inner">The runtime's own exception.</param>
    public VolumeFullException(string path, long attemptedBytes, VolumeCapacity? capacity, IOException inner)
        : base(Describe(path, attemptedBytes, capacity), inner)
    {
        Path = path;
        AttemptedBytes = attemptedBytes;
        Capacity = capacity;
    }

    /// <summary>The file the bytes were to be published under.</summary>
    public string Path { get; }

    /// <summary>How many bytes the write carried.</summary>
    public long AttemptedBytes { get; }

    /// <summary>The volume's capacity read after the refusal, when it could be read.</summary>
    public VolumeCapacity? Capacity { get; }

    // Linux ENOSPC/EDQUOT, macOS ENOSPC/EDQUOT, Windows ERROR_HANDLE_DISK_FULL/ERROR_DISK_FULL.
    private const int Enospc = 28;
    private const int LinuxEdquot = 122;
    private const int MacEdquot = 69;
    private const int WindowsHandleDiskFull = 0x27;
    private const int WindowsDiskFull = 0x70;

    /// <summary>
    /// Whether <paramref name="exception"/> is the operating system refusing a write for lack of
    /// space. On Unix the runtime carries the raw <c>errno</c> as the exception's
    /// <see cref="Exception.HResult"/>; on Windows the low word of the HRESULT is the Win32 code.
    /// </summary>
    /// <param name="exception">The exception a file write threw.</param>
    /// <returns><c>true</c> for an out-of-space refusal.</returns>
    public static bool IsOutOfSpace(IOException exception)
    {
        if (exception is VolumeFullException)
            return true;
        var code = exception.HResult;
        if (OperatingSystem.IsWindows())
            return (code & 0xFFFF) is WindowsHandleDiskFull or WindowsDiskFull;
        if (code == Enospc)
            return true;
        return OperatingSystem.IsMacOS() ? code == MacEdquot : code == LinuxEdquot;
    }

    private static string Describe(string path, long attemptedBytes, VolumeCapacity? capacity)
    {
        var volume = capacity is { } c
            ? $"{c.FreeMiB:N0} MiB free of {c.TotalMiB:N0} MiB"
            : "its capacity could not be read";
        return $"The volume holding '{path}' is OUT OF SPACE ({volume}) and refused a write of "
            + $"{attemptedBytes:N0} byte(s). Nothing was published. This is storage exhaustion on "
            + "that volume, not a defect in what was being written: free space on it, and the "
            + "next write succeeds.";
    }
}
