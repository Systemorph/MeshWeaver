using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>Who keeps a partition's content current on THIS mesh.</summary>
public enum PartitionContentOwner
{
    /// <summary>The package installer. Its install record's <c>installedFiles</c> is therefore a
    /// true description of the partition, and an unattended apply may write into it.</summary>
    Installer = 1,

    /// <summary>A configured sync source (a <c>{partition}/_GitSync</c> naming a repository, or
    /// another <see cref="IPartitionSourceTracking"/> provider's equivalent). The installer is a
    /// SECOND writer there, and its record describes a mesh it no longer owns.</summary>
    SyncSource = 2,

    /// <summary>🚨 The question could not be answered — the seam faulted or did not emit inside its
    /// budget. NOT a pass: deliberately kept apart from <see cref="Installer"/> so a failed read can
    /// never be spelt like a real negative (AGENTS.md).</summary>
    Undetermined = 3,
}

/// <summary>What was decided, and the sentence a log line or a reminder quotes.</summary>
/// <param name="Owner">Who keeps the partition current.</param>
/// <param name="Partition">The partition the verdict is about.</param>
/// <param name="Because">Log copy — always populated, for every arm.</param>
public sealed record PartitionContentOwnershipVerdict(
    PartitionContentOwner Owner, string Partition, string Because)
{
    /// <summary>🚨 True ONLY for <see cref="PartitionContentOwner.Installer"/>. "I could not tell"
    /// is not "yes".</summary>
    public bool InstallerOwnsTheContent => Owner == PartitionContentOwner.Installer;
}

/// <summary>
/// 🚨 <b>ONE PARTITION, ONE BOOKKEEPING</b> (Systemorph/MeshWeaver#4355).
///
/// <para><b>The defect.</b> A partition can be BOTH a sync-governed space — a
/// <c>{partition}/_GitSync</c> whose imports <see cref="MeshWeaver.GitSync.SealedSyncGate"/> holds
/// to the commit sealed for the running framework identity — AND the target of a registry-installed
/// package with <c>autoUpdate: true</c>. The two writers keep SEPARATE books. The seal reconciler
/// rewrites the partition from git and does not touch the install record; the registry lane then
/// computes its next delta from that record, which no longer describes the mesh, and writes only
/// the files whose hash moved SINCE THE RECORD — leaving the ones it believes unchanged at the
/// git tree's content. The partition ends up a MIX of two commits that no CI ever compiled.</para>
///
/// <para><b>Measured on memex.systemorph.com, 2026-09-14.</b> <c>Store/_GitSync</c> was held at the
/// sealed Plugins commit <c>627fb3cd</c> (Store sources 1.10.3). At 14:23Z a pod restart's boot
/// sweep declined the Store bundles on their source fingerprint and
/// <c>ReconcileAtProvenCommitFromGitHub</c> re-imported the whole partition at <c>627fb3cd</c>
/// ("Re-imported Imported (200 node(s))", "Pruned 9 node(s)") — the install record
/// <c>Plugins/Store</c> still said 1.10.14. At 20:25Z the registry served 1.11.1; the delta against
/// that record found <c>Store/Core/Source/StoreTexts.cs</c> byte-identical between 1.10.14 and
/// 1.11.1, declared it unchanged and never wrote it, while <c>Catalog</c>, <c>Installer</c>,
/// <c>Maintenance</c>, <c>Order</c> and <c>Plugin</c> WERE written at 1.11.1. <c>Store/Catalog</c>
/// then recompiled against 1.10.3's <c>StoreTexts</c> and PARKED:
/// <c>CS1061 'StoreTexts' does not contain a definition for 'ExploreCta'</c>. Three clocks
/// disagreed about one partition, and every one of them read as healthy.</para>
///
/// <para><b>The invariant this type establishes.</b> <i>A partition has ONE content bookkeeping.</i>
/// Either the installer owns the content — in which case its record IS the description of the mesh
/// and an unattended apply may write — or a sync source does, in which case the installer is a
/// second writer whose record describes a mesh it does not control. The installer never
/// silently becomes the second writer, and it never diffs against a baseline it does not own.</para>
///
/// <para><b>Why the answer comes from <see cref="IPartitionSourceTracking"/> and not a new rule.</b>
/// That seam is already the platform's ONE definition of "this partition's content tracks an
/// external source", and the compile control plane already decides a closely related question from
/// it (MeshWeaver#3583: compiling the live source is honest only on a mesh that syncs it). The
/// reasoning transfers exactly — <i>writing</i> a package's content into a partition is honest only
/// where the installer is that partition's source of truth — and a second implementation of the
/// rule is how the two would come to disagree about a partition.</para>
///
/// <para><b>What this deliberately does NOT do.</b> It does not stop, defer or weaken the seal
/// reconcile (#4063's freeze class is about a sync that silently stops; this holds the OTHER
/// writer), and it never lets an unattended apply land sources the running identity has no proven
/// bundle for — it removes such a bypass rather than adding one. A mesh that registers no tracking
/// provider at all (a local mesh, CI's disposable meshes, the bake host) has no notion of a second
/// writer and keeps today's behaviour exactly.</para>
/// </summary>
public static class PartitionContentOwnership
{
    /// <summary>
    /// How long the tracking seam has to answer. Its own contract says it never stalls ("a partition
    /// with no sources emits false promptly"), so this bounds a seam that is BROKEN, not a slow one
    /// — and a bound that is hit yields <see cref="PartitionContentOwner.Undetermined"/>, never a
    /// default answer.
    /// </summary>
    public static readonly TimeSpan TrackingBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The decision, pure — so every arm is drivable without a mesh.
    /// </summary>
    /// <param name="partition">The partition being decided about.</param>
    /// <param name="tracked">What the seam answered: <c>true</c> at least one configured source
    /// tracks it, <c>false</c> none does, <c>null</c> the question was not answered.</param>
    /// <param name="providerCount">How many <see cref="IPartitionSourceTracking"/> implementations
    /// this mesh registers. Zero means this mesh has no notion of tracking at all.</param>
    public static PartitionContentOwnershipVerdict Decide(string partition, bool? tracked, int providerCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);

        // 🚨 No sync layer ⇒ no second writer, and this must stay the pre-#4355 behaviour rather
        // than a hold: a local mesh, a CI mesh and the bake host all register none, and holding
        // there would stop every install on every such deployment.
        if (providerCount == 0)
            return new PartitionContentOwnershipVerdict(PartitionContentOwner.Installer, partition,
                $"this mesh registers no partition-source tracking, so nothing but the installer "
                + $"writes '{partition}'");

        if (tracked is null)
            return new PartitionContentOwnershipVerdict(PartitionContentOwner.Undetermined, partition,
                $"whether '{partition}' tracks a source could not be established "
                + $"({providerCount} provider(s) asked; none answered inside {TrackingBudget.TotalSeconds:0}s "
                + "or the read faulted) — an unobserved partition is not an unsynced one");

        return tracked.Value
            ? new PartitionContentOwnershipVerdict(PartitionContentOwner.SyncSource, partition,
                $"'{partition}' tracks a configured sync source, which keeps its content at the "
                + "commit sealed for this instance; the install record is not a description of that "
                + "partition and the installer is not its writer")
            : new PartitionContentOwnershipVerdict(PartitionContentOwner.Installer, partition,
                $"no configured sync source tracks '{partition}', so the installer is its only writer");
    }

    /// <summary>
    /// The reactive half: asks every registered <see cref="IPartitionSourceTracking"/> once, bounded,
    /// and turns the answers into a verdict. Cold, emits <b>exactly once</b>; Subscribe to run.
    ///
    /// <para>🚨 <b>Exactly once is load-bearing, and three separate things would otherwise break
    /// it</b> — each producing a caller whose <c>SelectMany</c> runs no arm at all, which is a
    /// SILENT SKIP rather than a verdict:</para>
    /// <list type="bullet">
    ///   <item><b>A leg that completes without emitting.</b> One empty leg completes the whole
    ///     <c>CombineLatest</c> with no value — and <c>Timeout</c>
    ///     does not fire on a sequence that COMPLETED. <c>DefaultIfEmpty</c> per leg turns "did not
    ///     answer" into a null ANSWER instead.</item>
    ///   <item><b>A provider that throws SYNCHRONOUSLY from <c>IsTracked</c>.</b> The call is a
    ///     method call: without a per-leg <c>Defer</c> it runs while the sequence is being
    ///     CONSTRUCTED, so the throw escapes every operator attached after it.</item>
    ///   <item><b>A provider that never answers.</b> Bounded per leg, not around the combination,
    ///     so one wedged provider yields null for ITSELF rather than discarding what the others
    ///     said.</item>
    /// </list>
    ///
    /// <para>Never faults — an unanswered seam is <see cref="PartitionContentOwner.Undetermined"/>,
    /// which each caller then resolves in ITS OWN conservative direction (an unattended apply holds;
    /// an install that must happen anyway stops trusting its delta baseline and installs in full).
    /// One shared "cannot tell" with two different, stated consequences is the point: neither caller
    /// may read it as "clear to proceed".</para>
    /// </summary>
    /// <param name="hub">The hub whose service provider carries the seam.</param>
    /// <param name="partition">The partition to decide about (a top-level path segment).</param>
    /// <returns>A cold observable emitting the verdict exactly once.</returns>
    public static IObservable<PartitionContentOwnershipVerdict> Observe(IMessageHub hub, string partition)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return Observable.Defer(() =>
        {
            var providers = hub.ServiceProvider.GetServices<IPartitionSourceTracking>().ToArray();
            if (providers.Length == 0)
                return Observable.Return(Decide(partition, tracked: null, providerCount: 0));
            return providers
                // 🚨 ImportsContent, not IsTracked (MeshWeaver#4588): the question here is "does
                // anything else WRITE this partition's content", and an export-only source
                // (mesh → repo, imports rejected) writes nothing — holding an install there would
                // be a false positive. The seam's default IS IsTracked, so a provider that cannot
                // tell the directions apart keeps the pre-#4588 answer.
                .Select(p => Observable.Defer(() => p.ImportsContent(partition))
                    .Take(1)
                    .Select(tracked => (bool?)tracked)
                    .DefaultIfEmpty(null)
                    .Timeout(TrackingBudget)
                    .Catch<bool?, Exception>(_ => Observable.Return<bool?>(null)))
                .CombineLatest()
                .Take(1)
                .Select(answers => Decide(partition, Fold(answers), providers.Length))
                // The outer arm covers what no leg can: resolving the services themselves.
                .Catch<PartitionContentOwnershipVerdict, Exception>(_ =>
                    Observable.Return(Decide(partition, tracked: null, providers.Length)));
        });
    }

    /// <summary>
    /// Several providers into one answer. A positive is DECISIVE — one seam saying "a source tracks
    /// this" is knowledge, whatever a second seam failed to say. Only when nothing said yes does it
    /// matter whether everyone answered: all answered and none tracked is a real negative; a silent
    /// one leaves the question open.
    /// </summary>
    /// <param name="answers">One entry per provider; null where that provider did not answer.</param>
    /// <returns><c>true</c>, <c>false</c>, or <c>null</c> for "not established".</returns>
    public static bool? Fold(IEnumerable<bool?> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var given = answers as IReadOnlyCollection<bool?> ?? answers.ToList();
        if (given.Any(a => a == true))
            return true;
        return given.All(a => a.HasValue) ? false : null;
    }

    /// <summary>
    /// <see cref="Observe(IMessageHub, string)"/> with the verdict said once at the level it
    /// deserves — a hold is a WARNING (an unattended lane is declining to do what it was asked), an
    /// installer-owned partition is a DEBUG detail nobody needs to read.
    /// </summary>
    /// <param name="hub">The hub whose service provider carries the seam.</param>
    /// <param name="partition">The partition to decide about.</param>
    /// <param name="packageId">The package the decision is being taken for — log copy.</param>
    /// <param name="logger">Where the verdict is said.</param>
    /// <returns>A cold observable emitting the verdict exactly once.</returns>
    public static IObservable<PartitionContentOwnershipVerdict> Observe(
        IMessageHub hub, string partition, string packageId, ILogger? logger)
        => Observe(hub, partition)
            .Do(verdict =>
            {
                if (verdict.InstallerOwnsTheContent)
                    logger?.LogDebug(
                        "Package {Id}: the installer owns the content of {Partition} — {Because}.",
                        packageId, verdict.Partition, verdict.Because);
                else
                    logger?.LogWarning(
                        "Package {Id}: the installer is NOT the owner of {Partition}'s content "
                        + "({Owner}) — {Because} (MeshWeaver#4355).",
                        packageId, verdict.Partition, verdict.Owner, verdict.Because);
            });
}
