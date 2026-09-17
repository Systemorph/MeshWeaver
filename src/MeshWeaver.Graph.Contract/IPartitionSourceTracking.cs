namespace MeshWeaver.Graph;

/// <summary>
/// Whether a partition's content TRACKS an external source on this mesh — at least one CONFIGURED
/// sync source (a <c>{partition}/_GitSync</c> naming a repository, or another provider's
/// equivalent) brings the partition's files here and keeps them current.
///
/// <para><b>Who asks, and why it is a separate seam.</b> The compile control plane
/// (<c>NodeTypeCompilationHelpers</c>' compile watcher) asks it before it compiles a MODULE's
/// content locally: a module is delivered as a prebuilt bundle, and when the bundle is refused
/// (its source fingerprint disagrees with the files this mesh holds) or has not landed for this
/// framework identity, "compile the live source instead" is only honest on a mesh whose copy of
/// that source TRACKS the module's repository. On a partition nothing syncs, the "live source" is
/// whatever an install left behind — last month's files, or four of five — and compiling it
/// produces old code that reads as current (MeshWeaver#3583). The administration GUI asks the
/// richer <c>IPartitionSyncSourceProvider</c> (the same providers, one class per kind); this
/// contract is the one-bit answer the compiler layer can depend on without a reference to the
/// sync layer above it.</para>
///
/// <para><b>Contract.</b> Emits <see langword="true"/> when at least one source of this kind is
/// configured for the partition (a config node with its repository set — a config node that
/// exists but names no repository is NOT tracking anything), <see langword="false"/> otherwise;
/// re-emits when a source is added, removed or reconfigured; never stalls — a partition with no
/// sources emits <see langword="false"/> promptly. A mesh with NO implementation registered has no
/// notion of tracking at all (a local mesh, CI's disposable meshes, the bake host), and the
/// compile gate treats that as "compiling is legal here", exactly as it always was.</para>
/// </summary>
public interface IPartitionSourceTracking
{
    /// <summary>Live answer for <paramref name="partition"/> (the top-level path segment):
    /// whether at least one configured source of this kind tracks it.</summary>
    IObservable<bool> IsTracked(string partition);

    /// <summary>
    /// Whether at least one configured source of this kind WRITES this partition's content — i.e.
    /// imports repo → mesh. The narrower half of <see cref="IsTracked"/>, and the one an
    /// installer's "am I the only writer here?" question needs (MeshWeaver#4588).
    ///
    /// <para>🚨 <b>The two questions differ for an EXPORT-ONLY source, and each consumer needs its
    /// own.</b> A `mesh → repo` source makes the repository a mirror of the mesh and rejects
    /// imports, so nothing can overwrite or prune what an installer wrote: the installer IS the
    /// only writer, and holding an install there would be a false positive. The compile control
    /// plane's question is the opposite way round — with the mesh as the source of truth, its live
    /// source IS current, so that plane must keep reading <see cref="IsTracked"/> and must not be
    /// narrowed by this. One shared bit answering both is how they would come to disagree.</para>
    ///
    /// <para>The default is <see cref="IsTracked"/> — the conservative answer and the pre-#4588
    /// behaviour — so a provider that cannot tell the directions apart keeps treating every tracked
    /// partition as written. Same contract otherwise: live, re-emitting, never stalling.</para>
    /// </summary>
    /// <param name="partition">The partition (the top-level path segment).</param>
    /// <returns>A live observable; <see langword="true"/> while a source of this kind imports into
    /// the partition.</returns>
    IObservable<bool> ImportsContent(string partition) => IsTracked(partition);
}
