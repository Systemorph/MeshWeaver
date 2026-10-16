namespace MeshWeaver.Mesh;

/// <summary>
/// The STORE refused a version-guarded delete's compare-and-delete
/// (<see cref="Services.IStorageAdapter.DeleteIfVersionAtMost"/>, #6856): the stored row is newer
/// than the version the owning hub confirmed, so a write landed after that check. Raised inside the
/// delete pipeline and answered as <see cref="NodeDeletionRejectionReason.Conflict"/>; it never
/// reaches a caller as an exception.
/// </summary>
internal sealed class DeleteVersionConflictException(string path, long expectedVersion, long storedVersion)
    : System.InvalidOperationException(
        $"'{path}' is stored at version {storedVersion}, newer than the confirmed version {expectedVersion}")
{
    /// <summary>The node whose delete was refused.</summary>
    public string Path { get; } = path;

    /// <summary>The version the owner confirmed and the delete named.</summary>
    public long ExpectedVersion { get; } = expectedVersion;

    /// <summary>The newer version the store holds.</summary>
    public long StoredVersion { get; } = storedVersion;
}
