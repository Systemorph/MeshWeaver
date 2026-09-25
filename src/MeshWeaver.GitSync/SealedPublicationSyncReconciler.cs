using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// 🚨 <b>The SEAL is the trigger the green-build hook cannot be.</b> Pure decision, per sync
/// source, of what a sealed publication of this instance's identity means for that source.
///
/// <para><b>The two orderings that left memex.systemorph.com dark (2026-09-08).</b> The
/// <c>workflow_run</c> hook fires when a repository's build goes green — which is BEFORE its
/// publish-bake job seals the bundles for this identity. <see cref="SealedSyncGate"/> therefore
/// HOLDS the source ("not sealed for this instance; it advances when it is"), correctly — but
/// nothing re-fired when the seal landed minutes later, so "when it is" was never. And a source
/// whose config already claimed the sealed commit was not necessarily AT it: the importer had
/// answered <c>Skipped</c> against a content marker without reading the partition, while three
/// files the commit deleted were still in the mesh. In both shapes the bytes sealed for this
/// identity were declined on their source fingerprint, every type compiled from whatever the mesh
/// held, and the record of that compile dangled on the next restart.</para>
///
/// <para>So: a source AT the sealed commit whose types were nevertheless declined is re-imported at
/// that commit with the content-skip bypassed (<see cref="ImportConflictPolicy.Reconcile"/> — every
/// conflict protection stays armed), and a released bundle hold re-imports at the commit whose
/// sources were held. 🚨 Nothing else is imported (policy <c>module-sync-per-manifest-hash</c>): the
/// seal does not choose a source's commit. A source on another commit is usually AHEAD of the seal,
/// and a source with no commit yet is brought by its first import or its next green build — an
/// import at the seal would move the first backwards and race the second.</para>
/// </summary>
public static class SealedSyncReconcile
{
    /// <summary>What to do with one sync source.</summary>
    public enum Action
    {
        /// <summary>Nothing — the source is not this seal's business, or it is held; see the reason.</summary>
        None = 1,

        /// <summary>The source is behind the seal: import it at the sealed commit. No longer produced
        /// (policy <c>module-sync-per-manifest-hash</c>: the seal does not choose a source's commit);
        /// the member stays because the enum is public and persisted in log copy.</summary>
        ImportAtSealedCommit = 2,

        /// <summary>The source claims the sealed commit but its types were declined on their
        /// source fingerprint: re-import at that commit, bypassing the content-skip.</summary>
        ReconcileAtSealedCommit = 3,
    }

    /// <summary>The decision and its reason (log copy an operator can act on).
    /// <paramref name="SteadyState"/> is the STRUCTURED signal for "at the seal, nothing declined"
    /// — the one <see cref="Action.None"/> that is not a hold and is never recorded or logged.</summary>
    public sealed record Plan(Action Action, string? Commit, string Reason, bool SteadyState = false)
    {
        /// <summary>
        /// The STRUCTURED signal for "behind the seal, but this source already holds a final verdict
        /// on the sealed commit under its current configuration"
        /// (<see cref="GitHubSyncService.HasFinalVerdictAt"/>) — the other <see cref="Action.None"/>
        /// that is not a hold. 🚨 It must never be RECORDED as one: a hold clears the attempt pair,
        /// so writing it would licence the very re-attempt this plan withholds, and the source would
        /// alternate refuse → hold → refuse on every announcement (#4499).
        /// </summary>
        public bool Settled { get; init; }
    }

    /// <summary>
    /// Decides for one sync source of the repository a sealed source belongs to.
    /// </summary>
    /// <param name="sealedSource">The sealed publication (this identity's).</param>
    /// <param name="repo">The repository the seal attributes to.</param>
    /// <param name="spacePath">The Space the sync source configures.</param>
    /// <param name="config">The source's config, or null when unreadable.</param>
    /// <param name="sealedForThisIdentity">Everything sealed under this identity (the gate's input).</param>
    /// <param name="identity">This instance's framework identity — log copy only.</param>
    /// <param name="declinedTypePaths">NodeType paths whose bundle entry was declined on its source
    /// fingerprint during the sweep that read this seal.</param>
    public static Plan Decide(
        SealedSource sealedSource, RepoIdentity repo, string spacePath, GitHubSyncConfig? config,
        IReadOnlyList<SealedSource> sealedForThisIdentity, string identity,
        IReadOnlyCollection<string> declinedTypePaths)
        => DecideWithInventory(
            sealedSource, repo, spacePath, config, sealedForThisIdentity, identity, declinedTypePaths,
            PrebuiltBundleInventory.NotConfigured);

    /// <summary>
    /// <see cref="Decide"/> plus the per-NodeType bundle inventory — the release half of
    /// adopt-then-sync per NodeType (MeshWeaver#3845 hole 4).
    ///
    /// <para>A source with NodeTypes held for their bundle
    /// (<see cref="GitHubSyncConfig.BundleHeldNodeTypes"/>) sits on an OLDER commit deliberately, and
    /// its attempt pair records a final verdict on the sealed one — so the settled shortcut below
    /// would answer "re-reading re-derives the same verdict", which is true of the COMMIT and false
    /// of the SHELF. A publication arriving is exactly the event that can change the answer, and this
    /// asks whether it did BEFORE anything is re-fetched: a hold releases when the inventory now
    /// carries a wanted fingerprint, or when it was judged under a framework identity this instance
    /// no longer runs (a roll makes the judgement void, not merely old). An unrelated publication
    /// costs nothing.</para>
    ///
    /// <para>A DISTINCT name, not an overload: an added overload makes every dependent's
    /// <c>cref</c> to the original ambiguous (CS0419 under warnings-as-errors — the shape that bit
    /// MeshWeaver.SocialMedia on 2026-09-04), which is why <c>ReconcileAtCommit</c> is spelled out
    /// beside <c>ReimportAtCommit</c> too.</para>
    /// </summary>
    /// <param name="sealedSource">The sealed publication (this identity's).</param>
    /// <param name="repo">The repository the seal attributes to.</param>
    /// <param name="spacePath">The Space the sync source configures.</param>
    /// <param name="config">The source's config, or null when unreadable.</param>
    /// <param name="sealedForThisIdentity">Everything sealed under this identity (the gate's input).</param>
    /// <param name="identity">This instance's framework identity — log copy, and the identity a held
    /// judgement is compared against.</param>
    /// <param name="declinedTypePaths">NodeType paths whose bundle entry was declined on its source
    /// fingerprint during the sweep that read this seal.</param>
    /// <param name="inventory">What bundles for this identity carry, per type.</param>
    public static Plan DecideWithInventory(
        SealedSource sealedSource, RepoIdentity repo, string spacePath, GitHubSyncConfig? config,
        IReadOnlyList<SealedSource> sealedForThisIdentity, string identity,
        IReadOnlyCollection<string> declinedTypePaths,
        PrebuiltBundleInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(sealedSource);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(sealedForThisIdentity);
        ArgumentNullException.ThrowIfNull(declinedTypePaths);

        if (!sealedSource.IsSealed || sealedSource.SourceCommit is not { Length: > 0 } commit)
            return new Plan(Action.None, null,
                $"'{sealedSource.Source}' is not sealed for identity {identity} ({sealedSource.Refusal ?? "no source commit"})");
        if (config is null)
            return new Plan(Action.None, commit, "config content could not be read");
        if (config.Direction == SyncDirection.ExportOnly)
            return new Plan(Action.None, commit, "direction is ExportOnly");

        var at = config.LastSyncCommitSha;
        if (!SameCommit(at, commit))
        {
            // 🚨 #3845 hole 4 — a hold the SHELF can release is not settled, whatever the attempt
            // pair says about the commit. Since policy module-sync-per-manifest-hash such a hold is
            // taken only on a `Modules:RequirePrebuilt` mesh, and the sources it held are those of
            // the commit the import ATTEMPTED — which is where the release re-imports, never the
            // sealed commit (the source may be ahead of it).
            if (ReleasesAHold(config, inventory, identity) is { } released)
            {
                var heldAt = config.LastAttemptedCommitSha is { Length: > 0 } attempted ? attempted : commit;
                return new Plan(Action.ReconcileAtSealedCommit, heldAt,
                    $"this source holds NodeType sources for a bundle that has now arrived ({released}) — "
                    + $"re-importing at {Short(heldAt)}, the commit whose sources were held, so they land on "
                    + "the bytes that match them");
            }

            // 🚨 THE SEAL NEVER CHOOSES A SOURCE'S COMMIT (policy module-sync-per-manifest-hash) —
            // not for a source that has one, and not for one that has none yet. A source on another
            // commit is usually AHEAD of the seal (its green builds advance it per module manifest
            // hash), so importing the sealed commit would move it BACKWARDS. A source with no commit
            // yet is brought by its first import (the configured branch, which the discovery scan
            // re-attempts while no commit is recorded) or its next green build — and an import at
            // the seal fired alongside those would RACE them, whichever landed last deciding the
            // tree. Not a hold, never recorded: the seal decides only whether each type adopts bytes
            // or compiles.
            return new Plan(Action.None, commit,
                at is { Length: > 0 }
                    ? $"'{sealedSource.Source}' is sealed at {Short(commit)} and the source sits at {Short(at)} — "
                      + "the seal does not choose a source's commit (policy module-sync-per-manifest-hash); "
                      + "green builds advance it, and each NodeType adopts a matching bundle or compiles"
                    : $"'{sealedSource.Source}' is sealed at {Short(commit)} and the source has landed no commit "
                      + "yet — its first import or its next green build brings it; the seal does not choose a "
                      + "source's commit (policy module-sync-per-manifest-hash)",
                SteadyState: true);
        }

        var declinedHere = declinedTypePaths
            .Where(p => p.StartsWith(spacePath + "/", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p, spacePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (declinedHere.Count == 0)
            return new Plan(Action.None, commit,
                $"'{sealedSource.Source}' is sealed at {Short(commit)} and the source is at it; nothing was declined",
                SteadyState: true);

        return new Plan(Action.ReconcileAtSealedCommit, commit,
            $"'{sealedSource.Source}' is sealed at {Short(commit)} and the source claims that commit, "
            + $"yet {declinedHere.Count} type(s) baked from it were declined on their source fingerprint "
            + $"({string.Join(", ", declinedHere.Take(5))}{(declinedHere.Count > 5 ? ", …" : "")}) — "
            + "the live sources have drifted from the commit they claim; re-importing at it");
    }

    /// <summary>
    /// Whether this reading of the shelf RELEASES a NodeType this source is holding — the sentence
    /// naming the first one it releases, or null.
    ///
    /// <para>Two releases, and they are different facts. A bundle for this identity now records the
    /// fingerprint a held type was waiting for: the import can land those sources onto bytes that
    /// match them. Or the hold was judged under ANOTHER framework identity — this instance has
    /// rolled, so the judgement is about bytes it no longer runs and must be taken again rather than
    /// trusted. An UNREADABLE inventory releases nothing: "cannot tell" is never "clear to proceed",
    /// and a re-attempt taken from it would be taken from a measurement that was not made.</para>
    ///
    /// <para>🚨 A hold taken by SHARING is not a trigger (review on #4595). Its own wanted
    /// fingerprint can already be on the shelf while the type it shares a source with is still
    /// waiting — releasing on it would re-import, re-hold the identical set, and do it again on
    /// every later publication. Only an independently held entry releases; a sharer is re-judged by
    /// the import the root's own release dispatches.</para>
    ///
    /// <para>🚨 And an entry written BEFORE that flag existed (#4595) says neither
    /// (<see cref="BundleHeldNodeType.HeldBySharing"/> is <c>null</c>), so neither reading is
    /// available for it (review on #4605): as "independent" a legacy sharer re-enters the futile
    /// loop, as "sharer" a legacy independent hold can never release on the arrival it waits for.
    /// Such an entry is a trigger only when the shelf now carries EVERY held entry's fingerprint —
    /// the one case where the re-import cannot be futile, because it clears the whole set — and that
    /// import rewrites the list with the flag, so the unknown state survives exactly one
    /// conclusion.</para>
    /// </summary>
    /// <param name="config">The source's configuration.</param>
    /// <param name="inventory">What bundles for this identity carry, per type.</param>
    /// <param name="identity">This instance's framework identity.</param>
    /// <returns>The release, as log copy, or null.</returns>
    internal static string? ReleasesAHold(
        GitHubSyncConfig? config, PrebuiltBundleInventory inventory, string identity)
    {
        if (config?.BundleHeldNodeTypes is not { IsEmpty: false } held || inventory is null)
            return null;
        if (held.FirstOrDefault(h => !string.Equals(h.Identity, identity, StringComparison.Ordinal))
            is { } rolled)
            return $"'{rolled.Path}' was held under framework identity {rolled.Identity}, which this "
                   + $"instance no longer runs — the judgement is re-taken against {identity}";
        if (!inventory.IsUsable)
            return null;
        if (held.FirstOrDefault(h => h.HeldBySharing == false
                                     && inventory.Carries(h.Path, h.WantedFingerprint)) is { } arrived)
            return $"'{arrived.Path}' waits for source fingerprint {arrived.WantedFingerprint}, which a "
                   + $"bundle for framework identity {identity} now records";

        // A legacy entry (no flag) is a trigger only when the shelf carries the WHOLE held set: the
        // re-import then clears every one of them, so it cannot be the futile re-hold the flag
        // exists to prevent — and it rewrites the list with the flag, which ends the unknown state.
        var unknown = held.Where(h => h.HeldBySharing is null).ToList();
        if (unknown.Count > 0 && held.All(h => inventory.Carries(h.Path, h.WantedFingerprint)))
            return $"'{unknown[0].Path}' was held before this instance recorded whether a hold is its "
                   + $"own or a sharer's, and a bundle for framework identity {identity} now records "
                   + $"the fingerprint of every held type — the re-import clears the whole set";
        return null;
    }

    private static bool SameCommit(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
        && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase))
        && Math.Min(a.Length, b.Length) >= 7;

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}

/// <summary>
/// The I/O half of <see cref="SealedSyncReconcile"/>, registered as the hosting layer's
/// <see cref="IPublicationSyncReconciler"/>: matches each sealed source to the sync configs of
/// its repository (the same match the webhook uses) and dispatches the imports the decision
/// names. Holds are recorded ON the config (<see cref="GitHubSyncConfig.LastSyncNote"/>) and
/// logged at Warning once per reason, never per delivery.
/// </summary>
internal sealed class SealedPublicationSyncReconciler(
    IMessageHub hub,
    GitHubWebhookProcessor webhooks,
    GitHubSyncService sync,
    ILogger<SealedPublicationSyncReconciler>? logger = null) : IPublicationSyncReconciler
{
    // Instance state on a mesh-scoped singleton: the last hold reason logged per config path, so
    // a hold that persists across sweeps is said once and a CHANGED reason is said again.
    private readonly ConcurrentDictionary<string, string> loggedHolds = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IObservable<int> Reconcile(
        string identity,
        IReadOnlyList<SealedSource> sealedForThisIdentity,
        IReadOnlyCollection<string>? declinedTypePaths)
        => Observable.Defer(() =>
        {
            var attributable = sealedForThisIdentity
                .Where(s => s.IsSealed && s.Repository is { Length: > 0 } && s.SourceCommit is { Length: > 0 })
                .ToList();
            if (attributable.Count == 0)
                return Observable.Return(0);

            // 🚨 #3845 hole 4 — the inventory is read ONCE per reconcile, on the FileSystem pool,
            // and ONLY when a matched config actually holds NodeTypes for a bundle: it is the one
            // fact that can release such a hold, and a publication announcement that releases
            // nothing must cost nothing (review on #4595 — reading it up front enumerated and
            // parsed the whole shelf on every announcement, contrary to this contract). DEFERRED and
            // shared: the first source that needs it triggers the read, every later source in the
            // pass sees the same reading, so two decisions cannot disagree about what is on the
            // shelf.
            //
            // 🚨 The connection is OWNED by the hub, never `RefCount()`
            // (`RootedRxConnectionRatchetGuard`): the chain resolves this hub's `IoPoolRegistry`
            // inside a `Defer`, so a connect queued on the pool an instant before the hub's ShutDown
            // would otherwise run against a closed scope. `AutoConnect(1)` keeps it LAZY — an
            // announcement where nothing is held never subscribes, so the shelf is never read — and
            // the handle is dropped as the one-shot terminates, so a pass leaves nothing behind.
            var inventory = Observable.Defer(() => Inventory(identity)).Replay(1)
                .AutoConnectOwnedBy(hub, nameof(SealedPublicationSyncReconciler));
            return attributable
                .Select(s => ReconcileSource(
                    s, identity, sealedForThisIdentity, declinedTypePaths, inventory))
                .Concat()
                .Sum();
        })
        .Catch<int, Exception>(ex =>
        {
            logger?.LogWarning(ex,
                "[SealedSync] reconciling the sync sources against the seals of identity {Identity} "
                + "failed — the sources stay where they are", identity);
            return Observable.Return(0);
        });

    /// <summary>
    /// What bundles for this identity carry, per NodeType — read on the FileSystem
    /// <see cref="IIoPool"/> like every other reader of the published root, and only when a
    /// configured source is holding something a bundle could release. A mesh with no pool registry,
    /// or one whose read faults, answers <see cref="PrebuiltBundleInventory.NotConfigured"/>, which
    /// releases nothing: "cannot tell" is never "clear to proceed".
    /// </summary>
    private IObservable<PrebuiltBundleInventory> Inventory(string identity)
        => Observable.Defer(() =>
        {
            var configuration = hub.ServiceProvider.GetService<IConfiguration>();
            var publishedRoot = configuration?[ShippedPrebuiltBundles.PublishedRootConfigKey];
            var imageDirectory = configuration?[ShippedPrebuiltBundles.DirectoryConfigKey]
                is { Length: > 0 } configured
                ? configured
                : ShippedPrebuiltBundles.DefaultDirectory;
            if (hub.ServiceProvider.GetService<IoPoolRegistry>() is not { } pools)
                return Observable.Return(PrebuiltBundleInventory.NotConfigured);
            return pools.Get(IoPoolNames.FileSystem)
                .InvokeBlocking(ct => PrebuiltBundleInventory.Read(
                    imageDirectory, publishedRoot, identity, logger, ct));
        })
        .Catch((Exception exception) =>
        {
            logger?.LogWarning(exception,
                "[SealedSync] the bundle inventory for identity {Identity} could not be read — no "
                + "bundle-keyed hold is released by this pass", identity);
            return Observable.Return(PrebuiltBundleInventory.NotConfigured);
        });

    private IObservable<int> ReconcileSource(
        SealedSource sealedSource, string identity,
        IReadOnlyList<SealedSource> sealedForThisIdentity, IReadOnlyCollection<string>? declinedTypePaths,
        IObservable<PrebuiltBundleInventory> inventory)
    {
        var repo = SealedSyncGate.Parse(sealedSource.Repository!);
        if (!repo.IsComplete)
            return Observable.Return(0);
        var commit = sealedSource.SourceCommit!;
        return webhooks
            .ConfigsTargeting(repo, $"seal of '{sealedSource.Source}' at {commit[..Math.Min(8, commit.Length)]}")
            // 🚨 The shelf is read only when a config this seal matches is actually HOLDING NodeTypes
            // for a bundle (review on #4595): the inventory is the one fact that can release such a
            // hold, so an announcement that can release nothing must not enumerate and parse it. The
            // reading is deferred and shared across the whole pass, so the first source that needs it
            // pays for it once.
            // 🚨 The shelf is ALSO needed when this pass has to measure drift itself (#4620): a
            // caller that passed `null` did not compare the partition against the commit's tree, and
            // the comparison is exactly `PrebuiltBundleInventory` against each type's live
            // `CurrentSourceFingerprint`. Reading it stays LAZY and shared for the whole pass, so a
            // boot sweep — which hands its own measurement in — still never triggers the read.
            .SelectMany(match => (HoldsAnyBundleHeldType(match) || declinedTypePaths is null
                    ? inventory
                    : Observable.Return(PrebuiltBundleInventory.NotConfigured))
                .Select(inventoryReading => (Match: match, Inventory: inventoryReading)))
            .SelectMany(read => DriftReadings(read.Match, read.Inventory, declinedTypePaths)
                .Select(drift => (read.Match, read.Inventory, Drift: drift)))
            .Select(read =>
            {
                var match = read.Match;
                var dispatched = 0;
                // 🚨 The census records a HOLD, so it must record the RELEASE from the same
                // evidence (#4063). The hold is a statement about the GATE's verdict — "this
                // identity has no publication of repository X at or after its last green build" —
                // not about whether an import has finished, and the two must not be conflated: the
                // imports below are dispatched, not awaited, and their own success or failure is
                // already reported on each Space's own node. So the release is recorded when the
                // gate let something through for this repository: an import was dispatched, or a
                // source is AT the seal with nothing declined. Either way the repository is no
                // longer frozen, which is the only thing the census claims.
                var gateLetSomethingThrough = false;
                var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
                foreach (var node in match.Configs)
                {
                    if (GitHubWebhookProcessor.ToPushTarget(node) is not { } target)
                        continue;
                    var config = node.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger);
                    // 🚨 The caller's measurement, or THIS pass's own (#4620). `declinedTypePaths`
                    // null means the caller did not compare the partition against the commit's
                    // tree; `read.Drift` is that comparison, taken above. Either way what reaches
                    // the pure decision is a MEASURED set — never an empty one standing in for a
                    // measurement nobody took, which is what made the detector unreachable.
                    var declinedForThisSpace = declinedTypePaths
                        ?? DriftedOf(read.Drift, target.SpacePath, repo, sealedSource, identity);
                    var plan = SealedSyncReconcile.DecideWithInventory(
                        sealedSource, repo, target.SpacePath, config, sealedForThisIdentity, identity,
                        declinedForThisSpace, read.Inventory);
                    switch (plan.Action)
                    {
                        case SealedSyncReconcile.Action.ImportAtSealedCommit:
                            gateLetSomethingThrough = true;
                            logger?.LogInformation("[SealedSync] {Space}: {Reason} — importing.", target.SpacePath, plan.Reason);
                            accessService.RunAsSystem(() => hub.UpdateToProvenCommitFromGitHub(
                                    target.SpacePath, target.UserId, commit, sourceId: target.SourceId))
                                .Subscribe(
                                    activity => logger?.LogInformation(
                                        "[SealedSync] seal-triggered import of {Space} at {Sha} completed ({Activity}).",
                                        target.SpacePath, commit, activity),
                                    ex => logger?.LogWarning(ex,
                                        "[SealedSync] seal-triggered import of {Space} at {Sha} failed.",
                                        target.SpacePath, commit));
                            dispatched++;
                            break;
                        case SealedSyncReconcile.Action.ReconcileAtSealedCommit:
                            gateLetSomethingThrough = true;
                            logger?.LogWarning("[SealedSync] {Space}: {Reason}", target.SpacePath, plan.Reason);
                            // The plan names the commit: the seal's, or — for a released bundle
                            // hold — the commit whose sources were held (never moved backwards).
                            var reconcileAt = plan.Commit ?? commit;
                            accessService.RunAsSystem(() => hub.ReconcileAtProvenCommitFromGitHub(
                                    target.SpacePath, target.UserId, reconcileAt, sourceId: target.SourceId))
                                .Subscribe(
                                    activity => logger?.LogInformation(
                                        "[SealedSync] reconciling import of {Space} at {Sha} completed ({Activity}).",
                                        target.SpacePath, reconcileAt, activity),
                                    ex => logger?.LogWarning(ex,
                                        "[SealedSync] reconciling import of {Space} at {Sha} failed.",
                                        target.SpacePath, reconcileAt));
                            dispatched++;
                            break;
                        default:
                            // A settled source passed the gate too — the seal is not what stops it.
                            if (plan.SteadyState || plan.Settled)
                                gateLetSomethingThrough = true;
                            RecordHold(node.Path, target, plan, config);
                            break;
                    }
                }
                if (gateLetSomethingThrough)
                    hub.ServiceProvider.GetService<SealedSyncCensus>()?.RecordRelease(repo.ToString());
                return dispatched;
            });
    }

    /// <summary>
    /// Whether any config this seal matched is holding NodeTypes for a bundle — the question that
    /// decides whether the shelf is read at all (review on #4595). Read off the config nodes the
    /// match already carries, so it costs no I/O of its own.
    /// </summary>
    /// <param name="match">The configs this seal's repository matched.</param>
    /// <returns>True when a bundle inventory could release something.</returns>
    private bool HoldsAnyBundleHeldType(GitHubWebhookProcessor.RepoMatch match)
        => match.Configs.Any(node =>
            node.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger)
                is { BundleHeldNodeTypes: { IsEmpty: false } });

    /// <summary>
    /// This pass's OWN drift measurement, per Space the seal matched — empty (and costing nothing)
    /// when the caller handed one in (MeshWeaver#4620).
    ///
    /// <para>🚨 Only for a caller that passed <see langword="null"/>. The boot sweep measures drift
    /// as a by-product of its bundle-adoption walk and passes what it found, so it must not pay for
    /// a second reading; the publication-seal trigger measures nothing, and before this its empty
    /// set read as "the partition is clean".</para>
    /// </summary>
    /// <param name="match">The configs this seal's repository matched.</param>
    /// <param name="inventory">What bundles for this identity carry, per type.</param>
    /// <param name="declinedTypePaths">The caller's measurement, or null when it took none.</param>
    /// <returns>Space path → its reading; empty when the caller measured.</returns>
    private IObservable<ImmutableDictionary<string, SyncedPartitionDrift.Reading>> DriftReadings(
        GitHubWebhookProcessor.RepoMatch match,
        PrebuiltBundleInventory inventory,
        IReadOnlyCollection<string>? declinedTypePaths)
    {
        if (declinedTypePaths is not null)
            return Observable.Return(
                ImmutableDictionary<string, SyncedPartitionDrift.Reading>.Empty);

        var spaces = match.Configs
            .Select(GitHubWebhookProcessor.ToPushTarget)
            .Where(t => t is not null)
            .Select(t => t!.SpacePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        if (spaces.IsEmpty)
            return Observable.Return(
                ImmutableDictionary<string, SyncedPartitionDrift.Reading>.Empty);

        return Observable
            .Zip(spaces.Select(space => LiveDefinitions(space)
                .Select(live => (Space: space, Reading: SyncedPartitionDrift.Measure(live, inventory)))))
            .Take(1)
            .Select(readings => readings.ToImmutableDictionary(
                r => r.Space, r => r.Reading, StringComparer.Ordinal))
            .Catch((Exception exception) =>
            {
                // "Could not measure" must never arrive as "measured, clean": an empty dictionary
                // makes DriftedOf report NOT MEASURED for every Space, which is the abstain
                // direction and is said out loud there.
                logger?.LogWarning(exception,
                    "[SealedSync] the drift of {Count} synced partition(s) could not be measured — "
                    + "their sources are left where they are", spaces.Length);
                return Observable.Return(
                    ImmutableDictionary<string, SyncedPartitionDrift.Reading>.Empty);
            });
    }

    /// <summary>
    /// One partition's NodeType path → its LIVE definition.
    ///
    /// <para>🚨 The listing answers EXISTENCE and each node's own stream answers CONTENT — the
    /// same split <c>BundleKeyedHoldReading</c> makes, and for the same reason: a query answer can
    /// be minutes old (CQRS), and this decides whether content moves. <c>.Complete()</c> because it
    /// is an ENUMERATION that gates a decision, not a search.</para>
    ///
    /// <para><b>The residual, stated where the assumption is made:</b> the listing is the read
    /// model's, so a NodeType it does not return is simply not compared. That is the abstain
    /// direction — it can only ever under-report drift, never invent it — and the next announcement
    /// re-measures. The importer's own prune snapshot declares the same read for the same reason.</para>
    /// </summary>
    /// <param name="space">The Space (partition) to read.</param>
    /// <returns>Path → definition, for every NodeType whose content could be typed.</returns>
    private IObservable<ImmutableDictionary<string, NodeTypeDefinition>> LiveDefinitions(string space)
    {
        if (hub.ServiceProvider.GetService<IMeshService>() is not { } meshService)
            return Observable.Return(ImmutableDictionary<string, NodeTypeDefinition>.Empty);
        return meshService
            .Query<MeshNode>(MeshQueryRequest
                .FromQuery($"path:{space} scope:descendants nodeType:{MeshNode.NodeTypePath}")
                .Complete())
            // 🚨 `Initial`, not merely the first emission (review on #4649). `.Complete()` removes
            // the paging limit; it does NOT promise that what arrives first is the snapshot. A
            // pre-initial empty emission would make this report "no type could be compared", the
            // measurement would ABSTAIN, and the mixed partition would go unseen — a clean-looking
            // answer from a read that never happened, which is the whole subject of #4620. Same
            // filter GitHubSyncService's own descendant read makes, for the same reason.
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Timeout(ReadBudget)
            .SelectMany(types =>
            {
                var paths = types.Items
                    .Select(n => n.Path)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToImmutableArray();
                if (paths.IsEmpty)
                    return Observable.Return(ImmutableDictionary<string, NodeTypeDefinition>.Empty);
                // 🚨 EVERY per-node read is BOUNDED, and a read that does not answer degrades to
                // "this type has no definition" rather than hanging (review on #4649). These zips
                // run inside PublicationSealArrivalService's single serialized pump, whose
                // `Concat()` means one pending reconcile blocks every later seal announcement —
                // and an outer `Catch` cannot rescue a stream that is merely still waiting. A
                // point read of a path the (eventually consistent) listing named can also find
                // nothing there and terminate on a routing NotFound, which is the storm-breaker
                // shape AGENTS.md names. Both degrade the same way: fewer types compared, a
                // smaller denominator, and `Measure` abstains rather than inventing drift.
                return Observable
                    .Zip(paths.Select(path => hub.GetWorkspace().GetMeshNodeStream(path)
                        .Take(1)
                        .Select(node => (Path: path,
                            Definition: node?.ContentAs<NodeTypeDefinition>(
                                hub.JsonSerializerOptions, logger)))
                        .Timeout(ReadBudget)
                        .Catch((Exception exception) =>
                        {
                            logger?.LogDebug(exception,
                                "[SealedSync] {Path}: its definition could not be read in time — "
                                + "not compared", path);
                            return Observable.Return((Path: path, Definition: (NodeTypeDefinition?)null));
                        })))
                    .Take(1)
                    .Select(pairs => pairs
                        .Where(p => p.Definition is not null)
                        .ToImmutableDictionary(p => p.Path, p => p.Definition!, StringComparer.Ordinal));
            });
    }

    /// <summary>
    /// The declined set for one Space out of this pass's own readings — and the one place the mix is
    /// NAMED (MeshWeaver#4620).
    ///
    /// <para>🚨 A mix nobody can detect is the worst of the three outcomes this issue weighed, so
    /// when drift is found the line names the PARTITION, the repository and commit its sync claims,
    /// the types whose live sources no bundle records, and — where the mesh can say so — the OTHER
    /// writer, which is the package whose install targets this partition and the ref it installed
    /// from. An operator reading it should not have to join three clocks by hand, which is what
    /// #4588 cost.</para>
    /// </summary>
    /// <param name="readings">This pass's readings, per Space.</param>
    /// <param name="space">The Space being decided.</param>
    /// <param name="repo">The repository the seal attributes to.</param>
    /// <param name="sealedSource">The sealed publication.</param>
    /// <param name="identity">This instance's framework identity.</param>
    /// <returns>The drifted type paths — empty when there are none OR when none could be measured,
    /// the latter said out loud.</returns>
    private IReadOnlyCollection<string> DriftedOf(
        ImmutableDictionary<string, SyncedPartitionDrift.Reading> readings, string space,
        RepoIdentity repo, SealedSource sealedSource, string identity)
    {
        if (!readings.TryGetValue(space, out var reading) || !reading.Measured)
        {
            // 🚨 Said at DEBUG, not Warning: "I could not compare" is the normal answer on a mesh
            // with no bundles for this identity, and a Warning on every announcement would be noise
            // that teaches operators to skim. It is still SAID, because an absent reading and a
            // clean one must not look the same in a log either.
            logger?.LogDebug(
                "[SealedSync] {Space}: drift NOT MEASURED against {Repo}@{Commit} — {Reason}",
                space, $"{repo.Owner}/{repo.Repo}", Short(sealedSource.SourceCommit),
                reading?.Reason ?? "no reading was taken for this Space");
            return [];
        }
        if (reading.Drifted.IsEmpty)
        {
            logger?.LogDebug(
                "[SealedSync] {Space}: agrees with {Repo}@{Commit} — {Reason}",
                space, $"{repo.Owner}/{repo.Repo}", Short(sealedSource.SourceCommit), reading.Reason);
            return reading.Drifted;
        }
        // 🚨 NAME THE PARTITION AND BOTH WRITERS — a mix nobody can detect is the worst of the
        // three outcomes #4620 weighed, and #4588 cost a session precisely because all three clocks
        // (the install record, the sync commit, the mesh content) read correct on their own and
        // nothing joined them.
        //
        // Writer ONE is named in full here, because the sync layer IS it: the repository, the
        // commit, the sealed publication and the framework identity.
        //
        // Writer TWO is named by WHERE TO READ IT, not by a guess. The installer's record —
        // `Plugins/<package>.installedFromRef` / `installedAtUtc` — is the only place that says
        // which ref another writer landed, and it lives in `MeshWeaver.PluginCatalog`. That
        // assembly is deliberately NOT referenced from here: the one-bit ownership seam
        // (`IPartitionSourceTracking`) exists so the layers below need no reference to this one,
        // and reaching the other way for a log line would trade a documented layering for a
        // sentence. Reading the manifest untyped instead would be the `.As<T>()` trap. So the line
        // says where the answer is and the operator reads one node.
        logger?.LogWarning(
            "[SealedSync] {Space}: TWO WRITERS, ONE PARTITION. This sync is one of them and claims "
            + "{Repo}@{Commit} (sealed publication '{Source}', framework identity {Identity}); "
            + "{Reason}. Those sources are therefore NOT that commit's, so something else wrote "
            + "them — the other writer and its ref are recorded on the package whose "
            + "`targetPartition` is '{Space}' (`Plugins/<package>` → `installedFromRef`, "
            + "`installedAtUtc`). Re-importing at the sealed commit; see "
            + "Doc/Architecture/OnePartitionOneBookkeeping.",
            space, $"{repo.Owner}/{repo.Repo}", Short(sealedSource.SourceCommit),
            sealedSource.Source, identity, reading.Reason, space);
        return reading.Drifted;
    }

    /// <summary>How long any ONE read this pass makes may take. It exists because these
    /// reads run inside the seal-arrival pump, whose Concat means a pending reconcile
    /// blocks every later announcement — so the point is that a bound EXISTS, not its
    /// value. Over it, the read is "not compared", never "nothing drifted".</summary>
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    /// <summary>A commit for log copy — the same eight characters every other line in
    /// this file shows, so two lines about one commit read as one commit.</summary>
    /// <param name="sha">The commit, or null.</param>
    /// <returns>The short form, or "(none)".</returns>
    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha[..Math.Min(8, sha.Length)];

    /// <summary>A hold is written onto the config and said ONCE at Warning per reason; a source
    /// that is simply at the seal with nothing declined is neither (it is the steady state); a
    /// SETTLED source (<see cref="SealedSyncReconcile.Plan.Settled"/>) is said once and never
    /// written.</summary>
    private void RecordHold(string configPath, GitHubWebhookProcessor.PushTarget target,
        SealedSyncReconcile.Plan plan, GitHubSyncConfig? config)
    {
        if (plan.SteadyState)
        {
            loggedHolds.TryRemove(configPath, out _);
            return;
        }
        if (loggedHolds.TryGetValue(configPath, out var previous)
            && string.Equals(previous, plan.Reason, StringComparison.Ordinal))
            return;
        loggedHolds[configPath] = plan.Reason;
        if (plan.Settled)
        {
            // 🚨 Said once per reason and NEVER written (#4499): the config already carries the
            // final verdict and its note, and recording a hold would clear the attempt pair that
            // licenses this very skip — turning one refusal per commit back into one per announcement.
            logger?.LogInformation("[SealedSync] {Space}: not re-attempted — {Reason}", target.SpacePath, plan.Reason);
            return;
        }
        logger?.LogWarning("[SealedSync] {Space}: HELD — {Reason}", target.SpacePath, plan.Reason);
        if (config is null || string.Equals(config.LastSyncNote, plan.Reason, StringComparison.Ordinal))
            return;
        sync.RecordHold(target.SpacePath, target.SourceId, plan.Reason)
            .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                "[SealedSync] {Space}: could not record the hold on its sync config", target.SpacePath));
    }
}
