namespace MeshWeaver.Hosting;

/// <summary>
/// 🚨 <b>A sealed publication and the instance's SOURCES are one unit</b> — this is the seam that
/// makes them so. <see cref="ShippedPrebuiltBundles.SeedPublishedRoot"/> reads what the registry
/// sealed for this identity and adopts the bytes; the sources those bytes were compiled from live
/// in the mesh, and they advance only through the git sync. When the two disagree the bytes are
/// (correctly) declined and every type compiles from whatever the mesh holds — which is how
/// memex.systemorph.com ran 25 shared Crm sources against a bake of 22 on 2026-09-08: three files
/// a repo commit had deleted were never pruned, so no bake could ever match again.
///
/// <para>The git-sync layer registers the implementation (it owns the sync configs and the
/// import); the hosting layer calls it once per publication sweep with what it measured. An
/// instance with no git sync registers nothing and the sweep proceeds as before.</para>
/// </summary>
public interface IPublicationSyncReconciler
{
    /// <summary>
    /// Brings every sync source that a sealed publication of this identity belongs to onto that
    /// publication's commit: a source behind the seal is imported at the sealed commit (the seal
    /// is the trigger the green-build hook could not be, because the hook fires BEFORE the seal);
    /// a source already AT the sealed commit whose types were nevertheless declined on their
    /// source fingerprint is re-imported at that commit with the content-skip bypassed, so
    /// deletions the previous import left behind are applied. Cold; emits the number of imports
    /// dispatched. Never faults the caller.
    /// </summary>
    /// <param name="identity">This process's framework identity.</param>
    /// <param name="sealedForThisIdentity">What the registry sealed under it.</param>
    /// <param name="declinedTypePaths">NodeType paths whose bundle entry was declined because its
    /// source fingerprint disagrees with the live sources — or <see langword="null"/> when this
    /// caller did NOT measure that.
    ///
    /// <para>🚨 <b><see langword="null"/> and EMPTY are different facts and must never be folded
    /// (MeshWeaver#4620).</b> Empty means "I compared, and nothing had drifted"; null means "I did
    /// not compare". The reconciler treats an at-the-seal source with an empty set as its STEADY
    /// STATE and moves nothing — so a caller that passes empty without looking makes the drift
    /// detector report a clean partition it never read. That is exactly what happened: the boot
    /// sweep passes what its adoption walk measured, and
    /// <c>PublicationSealArrivalService</c> — the only post-boot trigger — passed <c>[]</c>, so
    /// after boot the branch could only ever take the "nothing was declined" exit. Measured on
    /// memex.systemorph.com on 2026-09-17: two of nineteen GitSync partitions were holding one file
    /// from each of two trees, both with their sync recording success. Pass null and the reconciler
    /// measures for itself, where the bundle inventory already is.</para></param>
    IObservable<int> Reconcile(
        string identity,
        IReadOnlyList<SealedSource> sealedForThisIdentity,
        IReadOnlyCollection<string>? declinedTypePaths);
}
