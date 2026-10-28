namespace MeshWeaver.Mesh;

/// <summary>
/// Whether a path lies at or under a subtree whose deletion — or whose MOVE away, which deletes it
/// from where it is — is in flight in this process. Implemented by the mesh's
/// <c>RecentlyDeletedRegistry</c>, whose scopes the storage write guard enforces.
///
/// <para>🚨 Why the OWNER asks it at COMMIT time, not only the store at flush time
/// (Systemorph/MeshWeaver#6953). The storage guard refuses every write under an open scope, but a
/// per-node hub commits a write to its in-memory node FIRST and stores it afterwards. A write that
/// committed while the scope was open was therefore refused only at its flush: the writer was told
/// it failed, while the owner kept serving it and its next save stored it anyway once the scope
/// closed — or, after a move that succeeded, lost it to the tombstone. Asked in its commit turn, a
/// write either lands before the scope opened or commits nothing: a cross-hub patch is refused, an
/// owner's own write is deferred until the scope lifts (as behind a version-guarded delete's seal).</para>
///
/// <para>It lives in <c>MeshWeaver.Data.Contract</c> for the reason
/// <see cref="SubtreeDeletionInFlightException"/> does: the layer that commits patches
/// (<c>MeshWeaver.Data</c>) and the layer that owns the scopes (<c>MeshWeaver.Mesh.Contract</c>)
/// both reference this assembly and the first does not reference the second. Optional: a host
/// without the registry registers no implementation and nothing is refused at commit.</para>
/// </summary>
public interface IActiveDeletionScopes
{
    /// <summary>
    /// True when <paramref name="path"/> equals, or lies under, the root of a deletion (or move)
    /// in flight; <paramref name="deletionRoot"/> then names that root.
    /// </summary>
    /// <param name="path">The node path a write is about to commit to.</param>
    /// <param name="deletionRoot">The root of the deletion in flight, or null.</param>
    /// <returns><c>true</c> when a write to <paramref name="path"/> must be refused now.</returns>
    bool IsUnderActiveDeletion(string? path, out string? deletionRoot);

    /// <summary>
    /// ADMITS a write that is about to commit to <paramref name="path"/>, or refuses it
    /// (Systemorph/MeshWeaver#7346). Refused — null, with <paramref name="deletionRoot"/> naming the
    /// deletion in flight — exactly when <see cref="IsUnderActiveDeletion"/> would answer true.
    /// Admitted, the returned handle carries the write from its commit to its STORAGE write: a
    /// deletion (or move) whose scope opens after this call waits for the write — whose storage
    /// write is let through the guard under that scope — instead of refusing it at its flush after
    /// the owner had already committed and served it. Dispose the handle exactly when the write's
    /// storage write has answered (success or failure), or at once when the write does not commit.
    ///
    /// <para>The default answers from <see cref="IsUnderActiveDeletion"/> alone and admits nothing
    /// to wait for: a host whose scopes carry no admissions keeps the commit-time refusal and
    /// nothing more.</para>
    /// </summary>
    /// <param name="path">The node path the write is about to commit to.</param>
    /// <param name="deletionRoot">The root of the deletion in flight when refused, otherwise null.</param>
    /// <returns>The admission to dispose at the storage write's answer, or null when refused.</returns>
    IDisposable? AdmitCommit(string? path, out string? deletionRoot)
        => IsUnderActiveDeletion(path, out deletionRoot)
            ? null
            : System.Reactive.Disposables.Disposable.Empty;
}
