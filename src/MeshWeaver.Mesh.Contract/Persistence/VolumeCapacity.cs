namespace MeshWeaver.Mesh.Persistence;

/// <summary>
/// Free and total bytes of the volume that holds a path — the one reading a writer needs to name
/// the cause of a short write, and the one a <c>/health</c> check needs to say "the share is
/// nearly full" BEFORE a compile finds out the hard way.
/// </summary>
/// <param name="FreeBytes">Bytes available to this process on the volume.</param>
/// <param name="TotalBytes">The volume's size.</param>
public readonly record struct VolumeCapacity(long FreeBytes, long TotalBytes)
{
    private const long MiB = 1024 * 1024;

    /// <summary>Free space in MiB, rounded down.</summary>
    public long FreeMiB => FreeBytes / MiB;

    /// <summary>Total size in MiB, rounded down.</summary>
    public long TotalMiB => TotalBytes / MiB;

    /// <summary>
    /// Reads the capacity of the volume holding <paramref name="path"/>, or <c>null</c> when it
    /// cannot be read (an absent directory, a path with no mount behind it). A capacity that
    /// cannot be read is never a reason to refuse a write — the length read-back decides that —
    /// so this answers <c>null</c> rather than throwing.
    /// </summary>
    /// <param name="path">A file or directory on the volume. The nearest EXISTING ancestor is
    /// what the reading is taken on, so a path whose file does not exist yet still answers.</param>
    /// <returns>The capacity, or <c>null</c> when it could not be read.</returns>
    public static VolumeCapacity? Of(string path)
    {
        try
        {
            var probe = System.IO.Path.GetFullPath(path);
            while (!Directory.Exists(probe))
            {
                var parent = System.IO.Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent) || parent == probe)
                    return null;
                probe = parent;
            }
            var drive = new DriveInfo(probe);
            return new VolumeCapacity(drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
