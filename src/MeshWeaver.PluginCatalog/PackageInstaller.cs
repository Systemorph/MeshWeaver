using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Installs a content package's folder into the mesh: parse the folder's files into MeshNodes,
/// rebase them under the package's target partition, and upsert them INCREMENTALLY via
/// <see cref="CreateOrUpdateNodeRequest"/> — never through the static-repo importer, whose
/// full-replace/prune semantics would wipe the rest of a shared partition (installing one agent
/// must not delete every other agent).
///
/// <para><b>Update only on real change.</b> Before upserting, the installer reads the partition's
/// current nodes and writes only the ones whose content (or a synced field) actually differs — an
/// unchanged re-install writes nothing and bumps no versions. This matters because the upsert stamps
/// <c>LastModified = UtcNow</c> unconditionally, so without this guard a re-install would churn every
/// node's version. For a Code package the live recompile is requested only when its source changed.
/// After the content lands, an install record (a <c>Package</c> node) is written under the
/// <see cref="InstalledPartition"/> registry. Reactive end-to-end; Subscribe to run.</para>
/// </summary>
public static class PackageInstaller
{
    /// <summary>Partition that holds the install records (one <c>Package</c> node per installed id).</summary>
    public const string InstalledPartition = "Plugins";

    /// <summary>The NodeType of an install record.</summary>
    public const string PackageNodeType = "Package";

    /// <summary>Bounded concurrency for the per-node upsert fan-out (mirrors <c>NodeCopyHelper</c>).</summary>
    public const int DefaultBatchSize = 8;

    /// <summary>The well-known id of a partition's access policy satellite (<c>{partition}/_Policy</c>).</summary>
    public const string PartitionPolicyId = "_Policy";

    /// <summary>
    /// Installs <paramref name="manifest"/>'s content <paramref name="files"/> into its target
    /// partition and records the install, writing only the nodes that actually changed.
    /// </summary>
    /// <param name="hub">The installing hub.</param>
    /// <param name="manifest">The package manifest to install.</param>
    /// <param name="files">The package folder's files.</param>
    /// <param name="installedFromRef">The git ref the files were read at.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="batchSize">Bounded concurrency for the per-node upsert fan-out.</param>
    /// <param name="authorizingUserId">The principal that AUTHORIZED this install, captured before
    /// the install's system impersonation. Only consulted for a COMMERCIAL package, which requires
    /// a global admin (#830); null means nobody authorized it (unattended provisioning), which is
    /// fine for a free package and refuses a priced one. See <see cref="PackageEntitlement"/>.</param>
    /// <returns>A cold observable of the install outcome; Subscribe to run.</returns>
    public static IObservable<InstallResult> Install(
        IMessageHub hub,
        PackageManifest manifest,
        IReadOnlyList<PackageFile> files,
        string installedFromRef,
        ILogger? logger = null,
        int batchSize = DefaultBatchSize,
        string? authorizingUserId = null)
    {
        // Installing a curated package is a platform action — the same footing as a GitSync import:
        // it writes partition ROOTS whose node types are dynamic (e.g. Store/Plugin — invisible to
        // the static-only PartitionWriteGuard check) and type/infrastructure nodes no user
        // principal may create. The SYSTEM impersonation is scoped around EACH write (inside
        // Upsert), never around the whole pipeline: the pipeline hops schedulers (visibility
        // barriers on a timer), and an ambient impersonation does not survive those hops.
        //
        // WHO may trigger it is decided HERE, before a single node is written — on the action, not
        // on the UI surface that happened to trigger it (#830): free packages need no permission,
        // commercial ones need a global admin. Every install path funnels through this method (and
        // InstallNodeRepoDelta, which carries the same gate), so the machine paths — the unattended
        // default install, the update watcher — are gated identically to a click.
        // Two gates, in order, both on the ACTION. Entitlement answers "may you" (#830); acceptance
        // answers "have you agreed to the terms" — different questions, neither substituting for
        // the other, and a licence that asks nothing costs a single null check.
        return PackageEntitlement.Authorize(hub, manifest, authorizingUserId, logger)
            .SelectMany(_ => LicenseAcceptanceGate.Require(hub, manifest, authorizingUserId, logger))
            .SelectMany(_ => InstallCore(
                hub, manifest, files, installedFromRef, logger, batchSize, authorizingUserId));
    }

    private static IObservable<InstallResult> InstallCore(
        IMessageHub hub,
        PackageManifest manifest,
        IReadOnlyList<PackageFile> files,
        string installedFromRef,
        ILogger? logger,
        int batchSize,
        string? authorizingUserId)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.PluginCatalog.PackageInstaller");

        if (manifest.Kind == PackageKind.Code)
            return InstallCode(hub, manifest, files, installedFromRef, logger, batchSize, authorizingUserId);

        if (manifest.Kind == PackageKind.NodeRepo)
            return InstallNodeRepo(hub, manifest, files, installedFromRef, logger, batchSize, authorizingUserId);

        var partition = manifest.TargetPartition;
        if (string.IsNullOrWhiteSpace(partition))
            return Observable.Throw<InstallResult>(new InvalidOperationException(
                $"Package '{manifest.Id}' has no targetPartition."));

        var sourceFolder = manifest.SourceFolder ?? manifest.Id;
        var parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions, hub.ServiceProvider.GetServices<IFileFormatParser>());

        var nodes = files
            .Where(f => !IsManifest(f.RelativePath))
            .Select(f => ParseNode(parsers, partition!, sourceFolder, f, logger, hub.JsonSerializerOptions))
            .Where(n => n is not null).Select(n => n!)
            .ToArray();

        if (nodes.Length == 0)
            return Observable.Throw<InstallResult>(new InvalidOperationException(
                $"Package '{manifest.Id}' has no installable content files."));

        if (RefuseIfStaticShadowed(hub, manifest, nodes, logger) is { } shadowed)
            return shadowed;

        var options = hub.JsonSerializerOptions;
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        // 🚨 PUBLISH THE PARTITION BEFORE ITS CONTENT (#1758). A content package writes no
        // partition ROOT of its own (every parsed node lands at `{partition}/{id}` or deeper), so
        // there is nothing here to race and the shape can go first outright: the package's own
        // root access satellites, then the manifest-declared shape, then everything else.
        //
        // It used to run after every content node. That is an ORDERING defect, not a permission
        // one: the partition becomes reachable as soon as anything in it lands, readers arrive, and
        // the permission fold correctly denies because the grants are simply not there yet.
        var ownAccess = nodes.Where(n => IsPartitionAccessSatellite(n.Path, partition)).ToArray();
        var content = nodes.Where(n => !IsPartitionAccessSatellite(n.Path, partition)).ToArray();
        return EnsureInstallPartitions(hub, partition, logger)
            .SelectMany(_ => ownAccess
                .Select(n => UpsertIfChanged(hub, persistence, n, options))
                .ToObservable().Concat().ToList())
            .SelectMany(accessWrites => EnsureDeclaredAccess(
                    hub, manifest, partition, logger, nodes.Select(n => n.Path))
                .Select(_ => accessWrites))
            .SelectMany(accessWrites => content
                .Select(n => UpsertIfChanged(hub, persistence, n, options))
                .ToObservable().Merge(batchSize).ToList()
                .Select(contentWrites => (IList<bool>)accessWrites.Concat(contentWrites).ToList()))
            .SelectMany(writes =>
            {
                var result = new InstallResult(nodes.Length, writes.Count(w => w));
                logger?.LogInformation(
                    "Installed package {Id} v{Version}: {Written} written, {Unchanged} unchanged into {Partition} @ {Ref}",
                    manifest.Id, manifest.Version, result.Written, result.Unchanged, partition, installedFromRef);
                // The declared access was published as a PHASE before the first content node landed
                // (see the note above). What stays here is its POST-CONDITION, read once and
                // reported LOUDLY — an install that reports success while its partition is
                // unreadable is exactly the failure this ordering exists to make impossible.
                return VerifyDeclaredAccess(hub, manifest, partition, logger)
                    .SelectMany(_ => WriteInstalledRecord(
                        hub, manifest, installedFromRef, nodes.Length, authorizingUserId: authorizingUserId))
                    .SelectMany(_ => WarmInstalledRoots(hub, manifest, nodes, logger))
                    // …then the package's committed binaries, into the warmed root's content
                    // collection — the half of "publish" that merging used to leave undone (#848).
                    .SelectMany(_ => SyncPackageContent(hub, partition, sourceFolder, files, nodes, logger))
                    .SelectMany(_ => RunInstallHooks(hub, partition!, logger))
                    .Select(_ => result);
            });
    }

    /// <summary>
    /// The reason this install must be REFUSED because one or more of its nodes would land at a
    /// path a registered <see cref="IStaticNodeProvider"/> already SERVES on this host — or
    /// <c>null</c> when there is no such collision (the overwhelmingly common case: zero I/O, one
    /// in-memory sweep of the static providers).
    ///
    /// <para>🚨 Why REFUSING beats writing (#1209). A statically-served path is not persistence-
    /// backed: the static claim wins every serve seam, so the per-node hub at that path is seeded
    /// from a node that is by design never persisted and emits one Full snapshot at v0, forever.
    /// An install into it therefore cannot succeed in any useful sense — the root's create is
    /// answered "node already exists" by the static entry, the fallback UPDATE lands on the
    /// static-served hub and is never reconciled or persisted, and the install's own post-write
    /// confirmation (<c>RootRetypeReconciled</c>) waits out its 30 s and throws a bare
    /// <see cref="TimeoutException"/> with nothing naming the cause. That is exactly how the
    /// <c>Agent</c>/<c>Skill</c> plugin packages failed on a host calling bare <c>.AddAI()</c>
    /// (2026-08-11): a deterministic 30 s hang per package, 0 nodes imported, no diagnostic.</para>
    ///
    /// <para>The check is deliberately EXACT-PATH, never prefix: a package writing <c>X/Child</c>
    /// while only <c>X</c> is served statically is a different (and separately guarded) situation,
    /// and a prefix rule would refuse legitimate installs. Static-only hosts — the ones that serve
    /// <c>Doc</c>/<c>Agent</c>/<c>Harness</c>/<c>Skill</c> from memory and install NO durable
    /// package there — see an empty collision set and are completely unaffected.</para>
    /// </summary>
    internal static string? StaticShadowedReason(
        IMessageHub hub, PackageManifest manifest, IEnumerable<MeshNode> nodes)
    {
        var collisions = nodes
            .Select(n => n.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Shortest path first so the package ROOT — the collision that matters and the one an
            // operator recognises — leads the list and supplies the detailed explanation.
            .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)
            .Select(p => (Path: p, Detail: hub.ServiceProvider.DescribeStaticServeCollision(p)))
            .Where(c => c.Detail is not null)
            .ToArray();
        if (collisions.Length == 0)
            return null;
        return $"Install of '{manifest.Id}' REFUSED: {collisions.Length} of its node path(s) are "
               + "already served by a static node provider on this host "
               + $"[{string.Join(", ", collisions.Select(c => c.Path))}]. {collisions[0].Detail}";
    }

    /// <summary>
    /// <see cref="StaticShadowedReason"/> as a terminal install outcome: the failing observable to
    /// return, or <c>null</c> to proceed. Fails LOUDLY and IMMEDIATELY — before any write — instead
    /// of writing into a shadowed path and timing out 30 s later somewhere downstream.
    /// </summary>
    private static IObservable<InstallResult>? RefuseIfStaticShadowed(
        IMessageHub hub, PackageManifest manifest, IEnumerable<MeshNode> nodes, ILogger? logger)
    {
        var reason = StaticShadowedReason(hub, manifest, nodes);
        if (reason is null)
            return null;
        logger?.LogError(
            "Package {Id}: static/durable path collision — refusing the install. {Reason}",
            manifest.Id, reason);
        return Observable.Throw<InstallResult>(new InvalidOperationException(reason));
    }

    /// <summary>
    /// Eagerly provisions the given partitions' backing stores (e.g. the Postgres schema + tables)
    /// via the standard <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/> — the same
    /// mechanism the static-repo importer and the Space-create path use. On a FRESH mesh nothing has
    /// ever written to the <see cref="InstalledPartition"/> records partition (it is not an
    /// OwnsPartition type, and the storage router no longer lazily creates schemas), so the very
    /// first catalog install would otherwise fault with Postgres <c>42P01</c> (relation does not
    /// exist). Idempotent, promise-cached in the providers; providers that need no per-partition
    /// provisioning no-op. Emits exactly once.
    /// </summary>
    private static IObservable<System.Reactive.Unit> EnsurePartitionsProvisioned(
        IMessageHub hub, params string?[] partitions)
    {
        var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToArray();
        var leaves = partitions
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(p => providers.Select(pr => pr.EnsurePartitionProvisioned(p)))
            .ToArray();
        return leaves.Length == 0
            ? Observable.Return(System.Reactive.Unit.Default)
            : Observable.Merge(leaves).ToList().Select(_ => System.Reactive.Unit.Default);
    }

    /// <summary>
    /// What every install path opens with: provision the target partition AND the install-RECORDS
    /// partition, then make the records partition READABLE (<see cref="EnsureRecordsPartitionReadable"/>).
    ///
    /// <para>🚨 The two belong together. Provisioning creates the schema the records land in;
    /// without the durable policy beside it that schema is filtered out of every query and the
    /// records are written into the dark (#1950). Pairing them here means a future install path
    /// cannot provision the records partition and forget its read surface — which is exactly how
    /// the records partition ended up the one partition the installer never published.</para>
    /// </summary>
    private static IObservable<System.Reactive.Unit> EnsureInstallPartitions(
        IMessageHub hub, string? targetPartition, ILogger? logger) =>
        EnsurePartitionsProvisioned(hub, targetPartition, InstalledPartition)
            .SelectMany(_ => EnsureRecordsPartitionReadable(hub, logger));

    /// <summary>
    /// 🚨 Makes the install-RECORDS partition (<see cref="InstalledPartition"/>) readable by
    /// writing its <c>PartitionAccessPolicy</c> DURABLY — provisioning the partition first, so the
    /// row has somewhere to land.
    ///
    /// <para><b>Why a durable node and not the static one it replaces (#1950).</b> The policy used
    /// to be an <c>AddMeshNodes</c> registration on <c>AddPluginCatalog</c> — an in-memory node
    /// with no row anywhere. The LIVE evaluator reads that happily, which is why every in-memory
    /// test passed. Postgres does not: a partition-scoped query is pre-filtered by
    /// <c>public.partition_access</c>, and those rows come from
    /// <c>rebuild_user_effective_permissions()</c> folding <c>mesh_nodes</c> for
    /// <c>node_type='PartitionAccessPolicy' AND id='_Policy'</c>. No row → nothing to project → no
    /// <c>partition_access</c> row for <c>plugins</c> → the whole schema drops out of EVERY
    /// partition query, for EVERY principal, platform admins included — while
    /// <c>get Plugins/&lt;id&gt;</c> by exact path still works, which is what made it read as a
    /// data bug. Measured on both production databases on 2026-08-20: the registry's
    /// <c>/api/plugins/bundles/index.json</c> served <c>{"bundles": []}</c> to a correctly-granted
    /// consumer, every module decided <c>SkipNoBundle</c>, and nothing logged anything. The live
    /// heal was this exact row plus a rebuild.</para>
    ///
    /// <para>The comment on the fold's PublicRead projection already says <i>"PackageInstaller
    /// writes exactly this"</i>. It did — for every partition it INSTALLS INTO, via
    /// <see cref="EnsureDeclaredAccess"/>. Its own records partition was the one that never got the
    /// same treatment.</para>
    ///
    /// <para><b>Create-only.</b> An existing policy is left completely alone — the platform must
    /// never widen or narrow a shape someone deliberately chose. One that withholds public read is
    /// reported, not overwritten: the records partition would then be invisible on purpose, which
    /// is worth a line in the log rather than a silent correction.</para>
    /// </summary>
    /// <param name="hub">The hub to provision and write through (the write runs as System).</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>Completes once the policy is present (or was already).</returns>
    public static IObservable<Unit> EnsureRecordsPartitionReadable(IMessageHub hub, ILogger? logger)
    {
        var policyPath = $"{InstalledPartition}/{PartitionPolicyId}";
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        return EnsurePartitionsProvisioned(hub, InstalledPartition)
            .SelectMany(_ => persistence is not null
                ? persistence.Read(policyPath, hub.JsonSerializerOptions).Take(1)
                : Observable.Return<MeshNode?>(null))
            .SelectMany(current =>
            {
                if (current is null)
                    return Upsert(hub, InstalledPartitionPolicy())
                        .Do(_ => logger?.LogInformation(
                            "[PackageInstaller] published the install-records partition read-only "
                            + "to everyone via {Path} — without a DURABLE policy node the SQL "
                            + "permission fold has nothing to project and {Partition} is invisible "
                            + "to every query (#1950)", policyPath, InstalledPartition))
                        .Select(_ => Unit.Default);

                if (!DeclaresPublicRead(current, hub))
                    logger?.LogWarning(
                        "[PackageInstaller] {Path} exists but does not declare PublicRead — the "
                        + "install records are readable only to identities holding an explicit "
                        + "grant on {Partition}, so catalog surfaces and the registry's bundle "
                        + "index will come up empty. Left as authored.",
                        policyPath, InstalledPartition);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>
    /// The read-only, world-readable policy of the install-RECORDS partition — the same shape every
    /// built-in catalog ships. The records are written exclusively as System, so no creator grant is
    /// ever minted, and a platform admin's <c>Admin/_Access</c> grant is scoped to the <c>Admin</c>
    /// partition: without this policy NO real signed-in principal holds Read on
    /// <see cref="InstalledPartition"/> and the installed-state query every catalog surface issues
    /// is denied for all of them, platform admins included (#811).
    ///
    /// <para><c>PublicRead</c> is safe — a <see cref="PackageManifest"/> carries no secrets — and the
    /// write caps keep the partition non-writable for every non-System identity (System bypasses the
    /// evaluator, so the installer's own record writes are unaffected).</para>
    ///
    /// <para>🚨 <b>ONE definition, two homes.</b> <c>AddPluginCatalog</c> registers this same node
    /// statically (for the live evaluator, and for the window before the durable write lands) and
    /// <see cref="EnsureRecordsPartitionReadable"/> writes it durably (for the SQL fold, which can
    /// only see rows). Sharing the definition is what keeps the two from ever disagreeing about the
    /// partition's access — a static node and a durable node at one path that said different things
    /// would be a worse bug than the one they exist to fix.</para>
    /// </summary>
    internal static MeshNode InstalledPartitionPolicy() =>
        new(PartitionPolicyId, InstalledPartition)
        {
            NodeType = PartitionAccessPolicyNodeType.NodeType,
            Name = "Access Policy",
            State = MeshNodeState.Active,
            Content = new PartitionAccessPolicy
            {
                PublicRead = true,
                Create = false,
                Update = false,
                Delete = false,
                Comment = false,
                Thread = false,
            },
        };

    /// <summary>
    /// The install record's <c>AutoUpdate</c> on a (re-)stamp. An EXISTING record's choice is
    /// carried forward verbatim — the record built here starts from the CATALOG manifest, which
    /// never carries the policy field, so without this an update re-stamp would silently reset an
    /// opted-in package back to reminder-only (breaking "our deployments always update" on the
    /// very first update). A FRESH install seeds from the deployment's
    /// <see cref="PluginCatalogOptions.AutoUpdateByDefault"/> (absent = the platform default:
    /// explicit opt-in, no unattended installs). Pure.
    /// </summary>
    // Internal for the BuildCompletionSubscriptionTest pin (InternalsVisibleTo).
    internal static bool SeedAutoUpdate(PackageManifest? existingRecord, PluginCatalogOptions? options) =>
        existingRecord?.AutoUpdate ?? options?.AutoUpdateByDefault ?? false;

    /// <summary>How long one root gets to activate before the warm gives up on it.</summary>
    private static readonly TimeSpan WarmTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The cover-grant DEADLOCK DETECTOR's budget: how long a warmed root whose partition this
    /// install deliberately left GATED is given to show its cover grant before the installer
    /// reports the gating pass as stalled. A detector budget, not a settle barrier — its whole
    /// product is a log line.
    ///
    /// <para>🚨 It exists because <b>warmed is not gated</b>. Warming completes as soon as the
    /// root NODE can be read; the gating pass that seeds the cover grants runs as a CONSEQUENCE of
    /// that activation, asynchronously. With nothing watching, the installer reports a clean
    /// install while the partition is still dark, and the first viewer to open
    /// <c>{package}/Subscribe</c> is denied — measured at 12–17 s, and the whole reason
    /// MeshWeaver.Education's disposable-mesh e2e fails non-deterministically with
    /// <c>Access denied: user 'e2e-admin' lacks Read permission on '{course}'</c> followed by a
    /// 180 s coupon timeout.</para>
    ///
    /// <para><b>Why 5 s, down from 30.</b> The 30 s was only ever PAID by the case that can never
    /// succeed. The wait ran on every in-package-typed root, including the partitions the installer
    /// itself publishes — and for those no cover grant is ever coming, so each such install burned
    /// the entire budget and then reported success. Measured in
    /// <c>MeshWeaver.PluginCatalog.Test</c> before this fix: 30.2–30.3 s per install
    /// (<c>RootTypedByAnInPackageNodeType_Installs</c>, both
    /// <c>StaleStampSelfTypedRoot…_RootServesItsTypesArea</c>,
    /// <c>DeferredNodeTypeReleases_AreRequestedAfterTheRootRecycle</c>) and 60.4 s for the one that
    /// installs twice (<c>SelfTypedRoot_ReinstallImmediately_WritesNothing</c>) — 211 s of a 325 s
    /// suite, and the same dead wall-clock on the production install path. Those roots are no
    /// longer waited on at all (<see cref="CoverGrantExpected"/>), so what remains is the case
    /// where a grant genuinely IS owed — and there it is observable in MILLISECONDS:
    /// <c>GatingDetectorTest.Detector_sees_a_cover_grant_that_lands</c> measures the real
    /// write→observe latency on a live mesh and it comes back in <b>44–55 ms</b> over repeated runs,
    /// a hundred-fold
    /// inside this budget. The hub is already up by the time this starts (phase 1 paid the
    /// activation under <see cref="WarmTimeout"/>), so the only outstanding work is one
    /// access-table write.</para>
    ///
    /// <para>The trade is deliberate: a gating pass slower than this budget is now REPORTED rather
    /// than waited out. Nothing relies on the waiting — the phase's result is discarded, and roots
    /// typed outside the package are skipped outright, so this step already "cannot change what is
    /// installed or who can read it" (see the call site). What it can do is say so, loudly.</para>
    /// </summary>
    private static readonly TimeSpan GatingDetectorBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many cover-grant waits may be in flight at once. Each is a LIVE mesh query held open
    /// until its grant lands, so an unbounded fan-out over a large install is a self-inflicted
    /// query storm — the shape that saturates the action block and takes the liveness probe with
    /// it. Small on purpose: the waits overlap, so the phase still finishes in one
    /// <see cref="GatingDetectorBudget"/> rather than one per root.
    /// </summary>
    private const int GatingWaitConcurrency = 4;

    /// <summary>
    /// The cover grant a gating node type writes at its partition root — the ONE observable proof
    /// that the partition has become readable rather than merely present.
    ///
    /// <para>Declared here as a well-known PATH rather than by asking the type, because core cannot
    /// introspect a plugin-side configuration lambda. A partition whose type does not gate simply
    /// never writes it, which this treats as "nothing to wait for", never as a failure.</para>
    /// </summary>
    internal static string CoverGrantPath(string partition) => $"{partition}/_Access/Public_Access";

    /// <summary>What the cover-grant deadlock detector saw for one installed root.</summary>
    internal enum GatingOutcome
    {
        /// <summary>Nothing is owed. This install PUBLISHED the partition itself (or there is no
        /// mesh service to ask), so no gating pass is going to write a cover grant and there is
        /// nothing to wait for — the shape the old code spent its whole budget on.</summary>
        NotExpected,

        /// <summary>The cover grant landed: the partition installs gated AND is readable.</summary>
        Landed,

        /// <summary>The budget expired on a partition that installs GATED — the one outcome this
        /// detector exists to catch. Reported LOUDLY; never fatal.</summary>
        Stalled,
    }

    /// <summary>
    /// Is a cover grant OWED for <paramref name="root"/> — i.e. did this install deliberately leave
    /// the partition gated, so that a plugin-side gating pass is the only thing that can make it
    /// readable? Pure, and the whole reason the detector no longer costs 30 s on every install.
    ///
    /// <para>🚨 <b>The discriminator is core's OWN decision, never the node type.</b>
    /// <c>Store/Plugin</c>, <c>PluginContent</c> and <c>AddPluginGating</c> live in the plugins
    /// repo; core cannot take a dependency on them, which is exactly why the grant is addressed as
    /// a well-known PATH (<see cref="CoverGrantPath"/>). But core does not need to ask the type:
    /// <see cref="EnsureDeclaredAccess"/> is the ONE access-establishment step of an install and it
    /// already states, per manifest, whether it published the partition or left it gated —
    /// <see cref="DeclaredAccessMarker"/> names the node it wrote and is <c>null</c> exactly when it
    /// wrote nothing (the COMMERCIAL branch: any non-zero price, or a contact-sales address). So:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Free / pre-installed</b> — the installer wrote <c>{partition}/_Policy ·
    ///     PublicRead = true</c>. The partition is readable through that policy whether or not any
    ///     gate ever covers it, and <see cref="VerifyDeclaredAccess"/> already re-reads that marker
    ///     and reports its absence as an ERROR. Waiting here adds nothing.</item>
    ///   <item><b>Free with declared public segments</b> — the installer wrote the root
    ///     <c>Public</c> Viewer grant, which IS this very cover grant. Waiting here waits on the
    ///     installer's own write, already verified one phase earlier by a storage read that does
    ///     not have to travel through the query index.</item>
    ///   <item><b>Commercial</b> — the installer wrote NOTHING on purpose: the partition lands
    ///     gated and only the entitlement machinery can open it. Its cover grant is the single
    ///     observable proof that a gating pass ran, and its absence means every viewer is denied,
    ///     including on the <c>Subscribe</c> cover that would sell the package. THIS is worth
    ///     detecting.</item>
    /// </list>
    ///
    /// <para>Restricted to the partition the manifest actually speaks for. A root this package
    /// wrote into but does not target is one core has made no access statement about, so it has no
    /// honest opinion to offer and stays silent rather than inventing a verdict.</para>
    /// </summary>
    internal static bool CoverGrantExpected(PackageManifest manifest, string? root) =>
        !string.IsNullOrWhiteSpace(root)
        && string.Equals(root, manifest.TargetPartition ?? manifest.Id, StringComparison.Ordinal)
        && DeclaredAccessMarker(manifest, root) is null;

    /// <summary>
    /// ACTIVATES each partition root this install just wrote, by opening its node stream once.
    ///
    /// <para>🚨 Without this a fresh install lands <b>DARK</b>. An install runs entirely as SYSTEM and
    /// never touches the installed partition's root hub; a node type's gating (which seeds the cover
    /// grants that make the partition readable) runs on HUB ACTIVATION; and a viewer cannot activate a
    /// hub they hold no Read on yet. So nobody can open what was just installed — observed as a fully
    /// installed Store with compiled types and 286 straight denials, and as the education install gate
    /// painting an empty "your exercises" grid. The Store's own <c>PluginHubWarmer</c> covers
    /// <c>Store/Plugin</c> roots once they appear, but nothing covers a partition core installs that
    /// is not a plugin — a course, for instance. The installer is the one component that always knows
    /// what it just wrote, so the first touch belongs here.</para>
    ///
    /// <para>SYSTEM, explicitly and inside the subscription: the install pipeline hops schedulers and
    /// an ambient impersonation does not survive the hop (the same reason <c>Upsert</c> scopes its own).
    /// Sequential (<c>Concat</c>), because each activation's gating pass writes its partition's access
    /// table and concurrent passes deadlock (40P01) on the shared effective-permissions rebuild.</para>
    ///
    /// <para>Best-effort by design: the content has already landed and been recorded, so a root that
    /// will not activate is logged and stepped over rather than failing an install that succeeded.</para>
    /// </summary>
    /// <summary>
    /// Runs every registered <see cref="IPartitionInstallHook"/> for the installed partition — the
    /// step that makes a package's content actually REACHABLE, not merely present.
    ///
    /// <para>Writing the nodes is only half an install: the registries that surface them (the agent
    /// picker, the skill menu) resolve from per-user source lists that nothing else updates. Without
    /// this, a package's agents sit in the mesh and no picker ever asks for them.</para>
    ///
    /// <para>Hooks are best-effort: the content is already committed by the time they run, so a
    /// failing hook is logged and never fails the install.</para>
    /// </summary>
    /// <summary>
    /// Watches <paramref name="root"/>'s cover grant for as long as
    /// <see cref="GatingDetectorBudget"/>, so an install that returns over a GATED partition either
    /// knows it is readable or SAYS that it is not.
    ///
    /// <para>🚨 <b>The defect this closes.</b> The wait used to run on every in-package-typed root
    /// while its own doc comment called the missing grant normal — "a partition whose node type
    /// does not gate never writes it" — so on the shape the code itself called normal the query
    /// could never emit, the install paid the whole 30 s, and the only trace was ONE Information
    /// line that read the same as the healthy case. Both halves are fixed here: the case that
    /// cannot succeed is not waited on (<see cref="CoverGrantExpected"/>), and the case that can is
    /// bounded by a detector budget and reported at WARNING when it expires.</para>
    ///
    /// <para><b>Never fatal.</b> The content is committed and recorded by the time this runs, and
    /// the caller discards the outcome — an unreadable partition is worth a loud diagnosis, never
    /// worth failing an install that succeeded. WARNING and not ERROR on purpose: the budget's
    /// expiry proves the grant is not there YET, not that it will never come, and
    /// <see cref="VerifyDeclaredAccess"/> owns the error-level verdict for the access core itself
    /// promised to write.</para>
    /// </summary>
    // Internal for the GatingDetectorTest pin (InternalsVisibleTo): a detector nothing exercises is
    // a detector nobody knows is broken — which is how this one came to log the wedged case and the
    // normal case with the same line and the same level.
    internal static IObservable<GatingOutcome> DetectGatingStall(
        IMessageHub hub, PackageManifest manifest, string root, ILogger? logger)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(GatingOutcome.NotExpected);

        if (!CoverGrantExpected(manifest, root))
        {
            logger?.LogDebug(
                "[PackageInstaller] no cover grant is owed for root {Root}: this install published "
                + "the partition itself ({Marker}), so nothing here is waiting on a gating pass",
                root, DeclaredAccessMarker(manifest, root) ?? "(a root this manifest does not target)");
            return Observable.Return(GatingOutcome.NotExpected);
        }

        var grant = CoverGrantPath(root);
        // 🚨 A QUERY, never an exact-path stream. The grant is OPTIONAL, and an exact-path
        // GetMeshNodeStream on an absent node does not wait — the owner answers an authoritative
        // routing NotFound that TERMINATES the stream, and that NotFound opens
        // MeshNodeStreamCache's storm-breaker window on the path. The breaker fast-fails WRITES
        // too, so this wait SUPPRESSED THE VERY WRITE IT WAS WAITING FOR: the gating pass's own
        // CreateOrUpdateNodeRequest for the grant then never completed, and the caller sat out its
        // full 60 s request budget. Observed as `[SYNC_STREAM] OnError … Owner={root}/_Access/
        // Public_Access` → `No node found …` → `CreateOrUpdateNodeRequest … target <unset>` →
        // TimeoutException → the provisioning plan failing in its create-home-root phase
        // (Systemorph/MeshWeaver#2229 item A, reproduced on MeshWeaver.Education CI).
        //
        // A query is empty-on-absent, so the watcher can be in place long before the node is
        // written, and it is LIVE — the grant landing emits. Nothing here reads Content, so the
        // index's lag costs at most one extra beat of waiting; existence is all this asks.
        // See Doc/Architecture/CqrsAndContentAccess.md → "An OPTIONAL node".
        // 🚨 Anchored at the EXACT grant, never a page of the _Access container's children.
        // A `scope:children … limit:N` listing can MISS a grant that exists — a partition with more
        // than N access entries puts it off the first page — and the detector would then fire for a
        // partition that is perfectly readable. That is the same user-visible symptom this method
        // was once fixed for (a false "no cover grant"), reintroduced by a rarer and harder-to-
        // diagnose route.
        //
        // `path:{grant}` with no `scope:` qualifier is QueryScope.Exact, whose contract for a path
        // that does not exist is documented in QueryParser: "answered ZERO ROWS with no error". So
        // it is empty-on-absent like any listing — no routing NotFound, no terminated stream, no
        // storm-breaker window (which is what made the original point read suppress the very write
        // it was waiting for) — while being incapable of paging past the one row it can return.
        //
        // No `select:` projection: MeshNode.Path is COMPUTED (`Namespace + "/" + Id`), so a
        // projection that omits either input yields an EMPTY path and the match below can never
        // succeed. At most one small AccessAssignment row is materialised, and nothing reads its
        // Content — this asks existence and nothing else.
        return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{grant} nodeType:{AccessAssignmentNodeType.NodeType}"))
            .Where(change => change.Items.Count > 0)
            .Take(1)
            .Select(_ => GatingOutcome.Landed)
            .Do(_ => logger?.LogInformation(
                "[PackageInstaller] root {Root} is gated and readable — its cover grant landed",
                root))
            .Timeout(GatingDetectorBudget)
            .Catch<GatingOutcome, Exception>(_ =>
            {
                logger?.LogWarning(
                    "[PackageInstaller] {Id} installed into {Root} GATED — it is commercial, so the "
                    + "installer published no access shape on purpose — but its cover grant {Grant} "
                    + "has not appeared after {Seconds}s. Until a gating pass writes it the "
                    + "partition DENIES every viewer, including on the Subscribe cover that would "
                    + "sell it. The install itself is complete; what is missing is the gating "
                    + "machinery (the Store's PluginGate) or its pass over this root.",
                    manifest.Id, root, grant, GatingDetectorBudget.TotalSeconds);
                return Observable.Return(GatingOutcome.Stalled);
            });
    }

    public static IObservable<Unit> RunInstallHooks(IMessageHub hub, string partition, ILogger? logger)
    {
        var hooks = hub.ServiceProvider.GetServices<IPartitionInstallHook>().ToArray();
        if (hooks.Length == 0 || string.IsNullOrWhiteSpace(partition))
            return Observable.Return(Unit.Default);

        return hooks
            .Select(hook => hook.OnPartitionInstalled(partition)
                .Catch<Unit, Exception>(exception =>
                {
                    logger?.LogWarning(exception,
                        "[PackageInstaller] install hook {Hook} failed for partition {Partition}",
                        hook.GetType().Name, partition);
                    return Observable.Return(Unit.Default);
                }))
            .ToObservable()
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastAsync();
    }

    /// <summary>The read-only role the declared-access grants and denies carry.</summary>
    private const string ViewerRole = "Viewer";

    /// <summary>
    /// The well-known PUBLIC child segment of a partition: <c>{partition}/Public/…</c> is always a
    /// public surface on a scoped-public package — the same convention the Store's
    /// <c>PluginGate</c> keys on, so the installer's denies and the plugin machinery's reconcile
    /// never fight over it.
    /// </summary>
    private const string WellKnownPublicSegment = "Public";

    /// <summary>The well-known folder holding a scope's <c>AccessAssignment</c> grants.</summary>
    private const string AccessFolder = AccessAssignmentGuard.AccessFolder;

    /// <summary>
    /// A node the PACKAGE itself ships that decides who may read its PARTITION ROOT — its own
    /// <c>{partition}/_Policy</c>, or a grant under <c>{partition}/_Access/</c>. Pure.
    ///
    /// <para>These land in the same phase as <see cref="EnsureDeclaredAccess"/> and BEFORE it, so
    /// create-only still means "the package's own shape wins" now that the phase runs before the
    /// package's content (#1758). Written the other way round, a package declaring itself free
    /// while SHIPPING a gated policy would be published wide open for the width of the install and
    /// only closed again when its own policy node landed — trading a denial window for an exposure
    /// window, which is the one trade this fix must never make.</para>
    ///
    /// <para>Deliberately the ROOT's satellites only. A child's shipped grant stays in the normal
    /// satellite stage: it can only ever land after the child it anchors on, and until that child
    /// exists there is nothing to expose.</para>
    /// </summary>
    internal static bool IsPartitionAccessSatellite(string path, string? partition) =>
        !string.IsNullOrWhiteSpace(partition)
        && (string.Equals(path, $"{partition}/{PartitionPolicyId}", StringComparison.Ordinal)
            || path.StartsWith($"{partition}/{AccessFolder}/", StringComparison.Ordinal));

    /// <summary>
    /// The one node whose presence PROVES <see cref="EnsureDeclaredAccess"/> did its job for this
    /// manifest — the fully-public shape's <c>{partition}/_Policy</c>, or the scoped shape's root
    /// <c>Public</c> grant. <c>null</c> when the manifest declares nothing to publish (a commercial
    /// package installs gated on purpose, so there is no marker and nothing to verify). Pure.
    /// </summary>
    internal static string? DeclaredAccessMarker(PackageManifest manifest, string? partition)
    {
        if (string.IsNullOrWhiteSpace(partition))
            return null;
        if (!manifest.PreInstalled && manifest.IsCommercial())
            return null;
        return !manifest.PreInstalled && DeclaredPublicSegments(manifest).Count > 0
            ? $"{partition}/{AccessFolder}/{WellKnownUsers.Public}_Access"
            : $"{partition}/{PartitionPolicyId}";
    }

    /// <summary>
    /// Re-reads the marker <see cref="EnsureDeclaredAccess"/> was supposed to leave behind and
    /// reports its ABSENCE as an error — the POST-CONDITION of the declared-access phase (#1758).
    ///
    /// <para>Deliberately a READ, never a second write pass: the scoped shape derives its child
    /// walk from a subtree query it also writes into, and a step that re-derives from nodes it has
    /// itself just written is how a reconcile comes to feed itself (#223, the 257,000-version
    /// <c>_Policy</c> storm). One call site writes; this one only looks, and it cannot schedule
    /// another pass because it never writes anything.</para>
    ///
    /// <para>Never fatal — the content is committed by the time this runs, and failing the install
    /// would trade an unreadable partition for no partition at all. LOUD is the point: before this,
    /// "published nothing" and "published correctly" were the same silent success. The poll is the
    /// same bounded shape as the install's other visibility barriers (the marker is written through
    /// the per-node hub, whose persist is debounced), and its expiry is the diagnosis, not a sleep.
    /// </para>
    /// </summary>
    private static IObservable<Unit> VerifyDeclaredAccess(
        IMessageHub hub, PackageManifest manifest, string? partition, ILogger? logger)
    {
        var marker = DeclaredAccessMarker(manifest, partition);
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (marker is null || persistence is null)
            return Observable.Return(Unit.Default);

        return Observable
            .Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => persistence.Exists(marker))
            .Where(exists => exists)
            .FirstAsync()
            .Timeout(DeclaredAccessVerifyBudget)
            .Select(_ => Unit.Default)
            .Catch((Exception exception) =>
            {
                logger?.LogError(exception,
                    "[PackageInstaller] {Id} finished installing into {Partition} but its declared "
                    + "access never converged — {Marker} is absent, so the partition is unreadable "
                    + "to everyone the manifest declares may read it.",
                    manifest.Id, partition, marker);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>Bound on the declared-access post-condition poll. Its expiry is a DIAGNOSIS (the
    /// error above), never a retry and never a delay anything waits on in the healthy case — the
    /// marker is normally already there on the first probe.</summary>
    private static readonly TimeSpan DeclaredAccessVerifyBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Establishes the access a package's MANIFEST declares — the ONE access-establishment step of
    /// an install, applied on every install path (content, node-repo full, node-repo delta) and
    /// re-asserted on boot so a lost policy self-heals. "Public pages public, protected pages
    /// protected, without any grants needed — just by getting the catalog" (#920):
    ///
    /// <list type="bullet">
    ///   <item><b>Pre-installed</b> (<see cref="PackageManifest.PreInstalled"/>) — platform
    ///     baseline, fully public: <c>PartitionAccessPolicy { PublicRead = true }</c> at
    ///     <c>{partition}/_Policy</c> (#902). Declared segments are irrelevant — everything is
    ///     readable.</item>
    ///   <item><b>Free</b> (<see cref="PackageManifest.Price"/> 0 or absent AND no
    ///     <see cref="PackageManifest.ContactEmail"/>) with no declared
    ///     <see cref="PackageManifest.PublicSegments"/> — the same fully-public policy: a free
    ///     package that a catalog hands out must be readable by everyone, signed in or not.</item>
    ///   <item><b>Free with declared <see cref="PackageManifest.PublicSegments"/></b> — public read
    ///     SCOPED to the declaration: Public+Anonymous Viewer grants at the partition root (the
    ///     cover and, by downward inheritance, the declared segments) plus Public+Anonymous Viewer
    ///     DENIES on every other child segment — the exact root-grant + per-child-deny shape the
    ///     Store's <c>CatalogGate</c> seeds for <c>/Store</c> (#200/#204). Underscore satellites
    ///     and the well-known <c>Public</c> segment follow the <c>PluginGate</c> conventions so the
    ///     two mechanisms converge instead of fighting.</item>
    ///   <item><b>Commercial</b> (<see cref="PackageEntitlement.IsCommercial"/>: any non-zero
    ///     <see cref="PackageManifest.Price"/> — positive = purchasable, negative = coupon-only —
    ///     or a <see cref="PackageManifest.ContactEmail"/>, i.e. sold contact-sales) — the installer
    ///     writes NOTHING: the partition lands gated, readable only via the entitlement machinery
    ///     (PluginGate / purchase / an admin-issued grant), which is exactly the point of asking to
    ///     be paid or to be called.</item>
    /// </list>
    ///
    /// <para><b>Why the installer owns it.</b> An installed package is written entirely under
    /// SYSTEM (so no creator grant is ever minted) into a partition no user holds a role on, and a
    /// platform admin's grant is scoped to the <c>Admin</c> partition. Without this step NOBODY can
    /// read what was just installed — which is exactly how the <c>Skill</c> catalog came to depend
    /// on a hand-placed <c>_Policy</c> node while <c>Agent</c> was simply unreachable (#902), and
    /// how every free plugin the unattended installer landed came up admin-only (#920: "Access
    /// denied … lacks Read permission on 'DoublePendulum/Live'"). The manifest is the package's
    /// whole statement of intent; the shape that makes it true must come from the install, not from
    /// an operator remembering to place nodes.</para>
    ///
    /// <para>Every node is CREATE-ONLY — an existing policy / grant / deny (shipped by the package
    /// itself, or tuned by an operator) is left completely alone, so this can never silently widen
    /// or narrow a deliberate choice, and a steady-state re-run writes nothing. Failure PROPAGATES:
    /// an install that could not establish its declared access has not installed properly, and
    /// swallowing that is the defect class this closes.</para>
    /// </summary>
    /// <param name="hub">The installing hub.</param>
    /// <param name="manifest">The package manifest whose declarations drive the shape.</param>
    /// <param name="partition">The installed partition (null/blank no-ops).</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="installedPaths">The node paths THIS install just wrote, when the caller has
    /// them. The scoped shape derives its child-segment walk from these UNIONED with a query of the
    /// partition's current children — the query alone can lag the writes it would have to see
    /// (CQRS), and the installer is the one component that always knows what it just wrote.</param>
    public static IObservable<Unit> EnsureDeclaredAccess(
        IMessageHub hub, PackageManifest manifest, string? partition, ILogger? logger,
        IEnumerable<string>? installedPaths = null)
    {
        if (string.IsNullOrWhiteSpace(partition))
            return Observable.Return(Unit.Default);

        // A commercial package — priced (positive = purchasable, negative = coupon-only) or
        // contact-sales — installs GATED: no public read of any kind, entitlement (PluginGate /
        // purchase / coupon / a grant issued after the sales conversation) is the only way in.
        // Pre-installed overrides both: platform baseline is public by definition.
        if (!manifest.PreInstalled && manifest.IsCommercial())
        {
            logger?.LogDebug(
                "[PackageInstaller] {Id} is commercial — {Partition} stays gated (entitlement only)",
                manifest.Id, partition);
            return Observable.Return(Unit.Default);
        }

        var declared = DeclaredPublicSegments(manifest);
        return !manifest.PreInstalled && declared.Count > 0
            ? EnsureScopedPublicRead(hub, manifest, partition!, declared, installedPaths, logger)
            : EnsurePartitionPublicRead(hub, manifest, partition!, logger);
    }

    /// <summary>
    /// The fully-public shape — <c>PartitionAccessPolicy { PublicRead = true }</c> at
    /// <c>{partition}/_Policy</c> (the shape every built-in catalog already ships: read-only
    /// publication, no secrets).
    ///
    /// <para><b>Create-only is not enough — a CONTRADICTING policy is healed.</b> The rest of this
    /// step is deliberately create-only so it can never narrow or widen a deliberate choice. A
    /// policy that says the OPPOSITE of what the manifest declares is not such a choice, though: it
    /// is a partition the declaration says everyone may read, left unreadable. That state is
    /// unreachable by any current code path but is exactly what an instance provisioned before #902
    /// carries — a legacy paywall policy (<c>{RedirectOnDenied}</c>, no <c>PublicRead</c>) plus
    /// Public/Anonymous Viewer DENIES on every child, the shape the Store's gate used to seed for
    /// every partition. Because a policy node was present, the boot repair pass
    /// (<c>InstalledPackageRepairService</c>) read it, skipped it, and re-skipped it on every boot
    /// since: the wrong shape could never heal itself, and the instance came up with its whole
    /// plugin baseline invisible — Store included, so there was not even a catalog to fix it from.
    /// Found live in production 2026-08-10, on an instance whose 8 pre-installed partitions carried 136 legacy
    /// denies while <c>memex</c>/<c>systemorph</c> — installed after #902 — were correct.</para>
    ///
    /// <para><b>🚨 The DENIES are the fingerprint, not the policy.</b> A gated policy on its own is
    /// never healed — a package is entitled to ship one, and
    /// <c>PackageShippedPolicy_Survives_CreateOnly</c> pins exactly that. What identifies the legacy
    /// damage is the pairing: a policy that withholds public read AND Public/Anonymous Viewer denies
    /// on the partition's children, which is the SCOPED shape
    /// (<see cref="EnsureScopedPublicRead"/>) applied with an empty declaration — every child gated,
    /// nothing left public. Current code cannot produce that (the scoped branch requires
    /// <c>declared.Count > 0</c>); only a pre-#902 installer could. So the heal triggers on the pair
    /// and leaves a shipped gated policy — which carries no such denies — completely alone.</para>
    ///
    /// <para><b>Order matters: denies first, policy last.</b> The denies are what actually hide the
    /// content (an explicit deny beats <c>PublicRead</c>), and the policy node is the marker that
    /// says "this partition is already in the declared shape". Writing the marker first would strand
    /// a half-swept partition permanently, so the sweep runs BEFORE the policy write and a failure
    /// simply leaves the old policy in place for the next boot to retry.</para>
    /// </summary>
    private static IObservable<Unit> EnsurePartitionPublicRead(
        IMessageHub hub, PackageManifest manifest, string partition, ILogger? logger)
    {
        var policyPath = $"{partition}/{PartitionPolicyId}";
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var existing = persistence is not null
            ? persistence.Read(policyPath, hub.JsonSerializerOptions).Take(1)
            : Observable.Return<MeshNode?>(null);

        return existing.SelectMany(current =>
        {
            // Already in the declared shape — the common case on every healthy instance. One read,
            // no write, no query.
            if (current is not null && DeclaresPublicRead(current, hub))
                return Observable.Return(Unit.Default);

            // No policy at all: the original create. Nothing to contradict, so no sweep.
            if (current is null)
                return Upsert(hub, PublicReadPolicy(partition, existingContent: null))
                    .Do(_ => logger?.LogInformation(
                        "[PackageInstaller] {Id} declares public content — published {Partition} "
                        + "read-only to everyone via {Path}", manifest.Id, partition, policyPath))
                    .Select(_ => Unit.Default);

            // A policy that withholds public read. Heal it ONLY together with the legacy denies that
            // identify it as the pre-#902 scoped gate; on their own it is a deliberate shipped
            // policy and stays untouched.
            return ContradictingDenies(hub, partition, logger).SelectMany(stale =>
            {
                if (stale.Count == 0)
                    return Observable.Return(Unit.Default);

                logger?.LogInformation(
                    "[PackageInstaller] {Id} declares {Partition} fully public but it carries the "
                    + "pre-#902 gate — retiring {Count} Public/Anonymous deny assignment(s) and "
                    + "healing {Path} to PublicRead",
                    manifest.Id, partition, stale.Count, policyPath);

                return Retire(hub, stale, partition, logger)
                    .SelectMany(_ => Upsert(hub, PublicReadPolicy(
                        partition,
                        current.ContentAs<PartitionAccessPolicy>(hub.JsonSerializerOptions))))
                    .Select(_ => Unit.Default);
            });
        });
    }

    /// <summary>
    /// The fully-public policy node, preserving every other field an existing policy carries (a
    /// <c>RedirectOnDenied</c> funnel stays wired) — <c>PublicRead</c> is the single field the
    /// declaration is about.
    /// </summary>
    private static MeshNode PublicReadPolicy(string partition, PartitionAccessPolicy? existingContent) =>
        new(PartitionPolicyId, partition)
        {
            NodeType = PartitionAccessPolicyNodeType.NodeType,
            Name = "Access Policy",
            State = MeshNodeState.Active,
            Content = (existingContent ?? new PartitionAccessPolicy()) with { PublicRead = true },
        };

    /// <summary>
    /// Whether an existing policy node already expresses the fully-public declaration. A node whose
    /// content will not deserialize is treated as NOT declaring it — the heal then rewrites it into
    /// the known-good shape, which is the safe direction for an unreadable policy.
    /// </summary>
    private static bool DeclaresPublicRead(MeshNode policy, IMessageHub hub) =>
        policy.ContentAs<PartitionAccessPolicy>(hub.JsonSerializerOptions) is { PublicRead: true };

    /// <summary>
    /// The Public/Anonymous Viewer DENIES inside <paramref name="partition"/> — the fingerprint of
    /// the pre-#902 scoped gate. Only the two well-known subjects
    /// (<see cref="WellKnownUsers.Public"/> / <see cref="WellKnownUsers.Anonymous"/>) with an
    /// entirely denied role set count: every deny naming a real user or group, and every grant, is
    /// invisible to this and therefore never at risk.
    ///
    /// <para>Read as SYSTEM — this runs inside an install pipeline or a boot pass, against a
    /// partition on which no user holds a role by construction. A listing failure yields NOTHING
    /// rather than throwing, which makes the caller leave the partition untouched: healing on an
    /// unknown deny set is the one outcome worse than not healing.</para>
    /// </summary>
    private static IObservable<IReadOnlyList<string>> ContradictingDenies(
        IMessageHub hub, string partition, ILogger? logger)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return<IReadOnlyList<string>>([]);

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using(
                () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
                _ => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{partition} scope:subtree "
                    + $"nodeType:{AccessAssignmentNodeType.NodeType} limit:{QueryLimit}")))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .Select(change => (IReadOnlyList<string>)change.Items
                .Where(node => IsWellKnownDeny(node, hub))
                .Select(node => node.Path)
                .ToList())
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "[PackageInstaller] listing access assignments of {Partition} failed — it is "
                    + "left exactly as it is", partition);
                return Observable.Return<IReadOnlyList<string>>([]);
            });
    }

    /// <summary>
    /// Deletes the named stale deny assignments, sequentially and as SYSTEM (the access table
    /// deadlocks under parallel writers, 40P01 — the same reason the scoped shape writes serially).
    ///
    /// <para>Failure-tolerant per node, like the rest of the repair path: one deny that cannot be
    /// removed still lets every other one go, and the survivor is NAMED rather than failing the
    /// boot — a partly-swept partition that says which node still gates it beats a boot that dies
    /// on a permission the operator can fix by hand.</para>
    /// </summary>
    private static IObservable<Unit> Retire(
        IMessageHub hub, IReadOnlyList<string> paths, string partition, ILogger? logger)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null || paths.Count == 0)
            return Observable.Return(Unit.Default);

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return paths
            .Select(path => Observable.Using(
                    () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
                    _ => meshService.DeleteNode(path))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "[PackageInstaller] could not retire stale deny {Path} — {Partition} stays "
                        + "partly gated", path, partition);
                    return Observable.Return(false);
                }))
            .ToObservable()
            .Concat()
            .ToList()
            .Select(_ => Unit.Default);
    }

    /// <summary>
    /// Whether an access assignment is a Public/Anonymous Viewer DENY — the exact shape
    /// <see cref="EnsureScopedPublicRead"/> writes for a gated child, and the one a fully-public
    /// declaration contradicts. Anything naming another subject, or granting rather than denying,
    /// is not one. Pure apart from the content deserialization.
    /// </summary>
    private static bool IsWellKnownDeny(MeshNode node, IMessageHub hub) =>
        node.ContentAs<AccessAssignment>(hub.JsonSerializerOptions) is { } assignment
        && (string.Equals(assignment.AccessObject, WellKnownUsers.Public, StringComparison.OrdinalIgnoreCase)
            || string.Equals(assignment.AccessObject, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase))
        && assignment.Roles is { Count: > 0 } roles
        && roles.All(role => role.Denied);

    /// <summary>
    /// The SCOPED-public shape for a free package with declared
    /// <see cref="PackageManifest.PublicSegments"/>: Public+Anonymous Viewer GRANTS at the
    /// partition root (grants inherit strictly downward, so the cover and the declared segments
    /// become readable by everyone) plus Public+Anonymous Viewer DENIES on every OTHER child
    /// segment. A deny only ever removes the role for the Public/Anonymous subjects — an admin or
    /// an entitled viewer holds their own grant and is never touched by it, which is what keeps
    /// "protected" meaning gated rather than blacked out.
    ///
    /// <para>The child walk follows the <c>PluginGate</c> conventions — underscore satellites are
    /// never gated here (their protection is the plugin machinery's <c>ProtectedSegments</c>
    /// concern) and the well-known <c>Public</c> segment is always public — so the installer's
    /// shape and the Store's reconcile converge on the same nodes instead of fighting. Segments
    /// come from the paths this install wrote UNIONED with the partition's current children (read
    /// as System); every node is create-only and the writes run SEQUENTIALLY (the access table
    /// deadlocks under parallel writers, 40P01).</para>
    /// </summary>
    private static IObservable<Unit> EnsureScopedPublicRead(
        IMessageHub hub, PackageManifest manifest, string partition,
        IReadOnlyCollection<string> declared, IEnumerable<string>? installedPaths, ILogger? logger)
    {
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var meshService = hub.ServiceProvider.GetService<IMeshService>();

        // The partition's current immediate children, read as SYSTEM (this runs inside an install
        // pipeline / a boot pass with no user identity; the freshly written partition has no user
        // grants yet by definition).
        var currentChildren = meshService is null
            ? Observable.Return<IReadOnlyList<string>>([])
            : Observable.Using(
                    () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
                    _ => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"path:{partition} scope:subtree limit:{QueryLimit}")))
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(30))
                .Select(change => (IReadOnlyList<string>)change.Items.Select(n => n.Path).ToList());

        return currentChildren.SelectMany(children =>
        {
            var gated = GatedChildRoots(
                children.Concat(installedPaths ?? []), partition, declared);

            // 🚨 DENIES FIRST, ROOT GRANTS LAST — the same rule EnsurePartitionPublicRead states
            // for its heal sweep, and for the same reason: the root grant is what OPENS the
            // partition and grants inherit strictly downward, so between the grant and a child's
            // deny that child is publicly readable. Ordered this way an interrupted publication
            // leaves the partition CLOSED, which is the safe half to fail on. It matters more now
            // that this whole step runs BEFORE the package's content (#1758): the denies are
            // established before the segments they gate even exist, so a gated child is born gated
            // instead of being reachable until its deny catches up.
            var shape = new List<MeshNode>();
            foreach (var child in gated)
            {
                shape.Add(ViewerAssignment(child, WellKnownUsers.Public, denied: true));
                shape.Add(ViewerAssignment(child, WellKnownUsers.Anonymous, denied: true));
            }
            shape.Add(ViewerAssignment(partition, WellKnownUsers.Public, denied: false));
            shape.Add(ViewerAssignment(partition, WellKnownUsers.Anonymous, denied: false));

            // Create-only, sequential; a failed write propagates (Upsert throws on !Success).
            return shape
                .Select(node =>
                {
                    var existing = persistence is not null
                        ? persistence.Read(node.Path, hub.JsonSerializerOptions).Take(1)
                        : Observable.Return<MeshNode?>(null);
                    return existing.SelectMany(current => current is not null
                        ? Observable.Return(0)
                        : Upsert(hub, node));
                })
                .ToObservable()
                .Concat()
                .Sum()
                .Do(written => logger?.LogInformation(
                    "[PackageInstaller] {Id} declares public segments [{Segments}] — {Partition} "
                    + "cover published, {Gated} other child(ren) gated ({Written} access node(s) "
                    + "written)",
                    manifest.Id, string.Join(", ", declared), partition, gated.Count, written))
                .Select(_ => Unit.Default);
        });
    }

    /// <summary>Query cap for the scoped-access child walk (mirrors the snapshot-query bound the
    /// Store's gates use).</summary>
    private const int QueryLimit = 10_000;

    /// <summary>
    /// The manifest's declared public segments, SANITIZED: one path segment each, inside the
    /// partition — blank entries, traversals and anything containing a slash are dropped;
    /// leading/trailing slashes are tolerated; duplicates collapse case-insensitively. Pure.
    /// </summary>
    internal static IReadOnlyCollection<string> DeclaredPublicSegments(PackageManifest manifest)
    {
        if (manifest.PublicSegments is not { Count: > 0 } segments)
            return [];
        var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in segments)
        {
            var segment = (raw ?? string.Empty).Trim().Trim('/');
            if (segment.Length == 0 || segment.Contains('/') || segment is "." or "..")
                continue;
            seen.Add(segment);
        }
        return seen;
    }

    /// <summary>
    /// The GATED child roots of a scoped-public partition: the immediate child segments present in
    /// <paramref name="paths"/> that are neither a declared public segment, nor the well-known
    /// <see cref="WellKnownPublicSegment"/>, nor an underscore satellite. Pure.
    /// </summary>
    internal static IReadOnlyList<string> GatedChildRoots(
        IEnumerable<string> paths, string partition, IReadOnlyCollection<string> declared)
    {
        var prefix = partition + "/";
        var roots = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (path is null || !path.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var rel = path[prefix.Length..];
            var firstSlash = rel.IndexOf('/');
            var segment = firstSlash < 0 ? rel : rel[..firstSlash];
            if (segment.Length == 0 || segment.StartsWith('_'))
                continue;                       // internal satellite — never gated by the installer
            if (string.Equals(segment, WellKnownPublicSegment, StringComparison.OrdinalIgnoreCase)
                || declared.Contains(segment, StringComparer.OrdinalIgnoreCase))
                continue;                       // a public surface — stays readable
            roots.Add(prefix + segment);
        }
        return roots.ToList();
    }

    /// <summary>
    /// A Viewer <c>AccessAssignment</c> for <paramref name="subject"/> at <paramref name="scope"/>
    /// — GRANT when <paramref name="denied"/> is false, DENY when true. The node lands at
    /// <c>{scope}/_Access/{subject}_Access</c> with <c>MainNode = {scope}</c> — the two placement
    /// rules the permission evaluator keys on (a grant with <c>mainNode</c> on the container
    /// stores cleanly and silently does nothing — the #204 trap). Pure.
    /// </summary>
    internal static MeshNode ViewerAssignment(string scope, string subject, bool denied) =>
        new($"{subject}_Access", $"{scope}/_Access")
        {
            NodeType = AccessAssignmentNodeType.NodeType,
            Name = denied ? $"{subject} — Viewer DENIED (gated)" : $"{subject} — Viewer",
            State = MeshNodeState.Active,
            MainNode = scope,
            Content = new AccessAssignment
            {
                AccessObject = subject,
                DisplayName = subject,
                Roles = [new RoleAssignment { Role = ViewerRole, Denied = denied }],
            },
        };

    /// <summary>
    /// Budget for the single READ that decides whether a self-typed root may be warmed yet. This
    /// is a snapshot of the type's current state, never a wait for a compile — the installer must
    /// not hold on Roslyn (the compile activity it just kicked runs on the same mesh), so an
    /// unanswered read simply means "don't warm this one".
    /// </summary>
    private static readonly TimeSpan RootTypeProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Backstop for <see cref="MayPublishIntoRoot"/> — the wait before the CONTENT publish touches
    /// a self-typed root. Unlike the warm's probe this genuinely waits for the in-package type's
    /// rebuild, because the publish is the point of the step, not an optimisation. The wait ends
    /// as soon as the type has a loadable build OR its rebuild has run and settled without one, so
    /// this cap is only ever reached when the type never even starts compiling.
    /// </summary>
    private static readonly TimeSpan RootTypeSettleTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long one liveness probe of the root waits before it counts as "still down".</summary>
    private static readonly TimeSpan RootPingTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Pause between liveness probes, so a fail-fast DeliveryFailure cannot spin the loop.</summary>
    private static readonly TimeSpan RootPingRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Overall budget for the root to come back after its recycle, before the publish goes ahead
    /// regardless. A recycle is milliseconds; this is the graceful sink for a wedged teardown, and
    /// is deliberately far below the hub's own request timeout so a miss is diagnosable rather than
    /// indistinguishable from the 60 s hang this wait exists to prevent.
    /// </summary>
    private static readonly TimeSpan RootReadyTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The path of the NodeType <paramref name="root"/> declares, but ONLY when that type is one
    /// this very package installs — those are the only types that can still be carrying the
    /// compile stamp the node repo COMMITTED at this point in the install. Read off the nodes we
    /// just wrote, never off the mesh: reading the root is the very activation these gates exist
    /// to defer. Null means "nothing about to be rebuilt — touching this root is safe".
    /// </summary>
    private static string? InPackageTypeOf(string root, IReadOnlyCollection<MeshNode> nodes)
    {
        var declaredType = nodes
            .FirstOrDefault(n => string.Equals(n.Path, root, StringComparison.Ordinal))?.NodeType;
        if (string.IsNullOrEmpty(declaredType))
            return null;
        return nodes.Any(n => n.Content is NodeTypeDefinition
                && string.Equals(n.Path, declaredType, StringComparison.OrdinalIgnoreCase))
            ? declaredType
            : null;
    }

    /// <summary>
    /// How many times the content publish re-asks a root that answered "I am recycling". Two
    /// recycles can hit one install (the installer's own and the framework's rebind watcher), so
    /// this only has to outlast those; it is a guard against an unforeseen recycle LOOP, never a
    /// budget to be widened. Each re-ask is gated on an observed teardown completing, so the
    /// attempts cannot spin.
    /// </summary>
    private const int RootRecycleReAsks = 4;

    /// <summary>
    /// Whether a publish failure is the framework's TRANSIENT recycle verdict — the node is
    /// coming back and the honest response is to ask again — rather than a real failure.
    /// Typed on <see cref="ErrorType.ShuttingDown"/> / <see cref="HubDisposingException"/>, never
    /// on message text, so an application error can never be mistaken for a recycle.
    /// </summary>
    private static bool IsRootRecycling(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is DeliveryFailureException { Failure.ErrorType: ErrorType.ShuttingDown })
                return true;
            if (HubDisposingException.IsHubDisposal(e))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Completes once the teardown that just rejected a publish has finished, so the re-ask lands
    /// on a fresh activation instead of racing the same dying instance. Event-driven: it observes
    /// that hub instance's <see cref="IMessageHub.DisposalCompleted"/>. When nothing is there to
    /// wait for — no local instance, or one that is not disposing (the recycle already finished,
    /// or the hub lives on another silo) — the re-ask proceeds immediately.
    /// </summary>
    private static IObservable<Unit> RootTeardownSettled(
        IMessageHub hub, string rootPath, ILogger? logger)
    {
        var live = hub.GetHostedHub(new Address(rootPath), HostedHubCreation.Never);
        if (live is null || !live.IsDisposing)
            return Observable.Return(Unit.Default);
        return live.DisposalCompleted
            .Take(1)
            .Timeout(RootRecycleTimeout)
            // 🚨 Continue, but never SILENTLY. Proceeding is right — the re-ask is worth attempting
            // even if the teardown never reported finishing — but the bound's own remark says it
            // "is only ever reached when a hub's teardown itself WEDGES". So expiring it is not a
            // slow path, it is the wedge, and this arm used to swallow the one moment that says so:
            // `.Catch(_ => Observable.Return(Unit.Default))` with no log at all.
            //
            // Thirty silent seconds per occurrence is exactly what #2446 is trying to account for
            // — "31 MB written in under a second, then minutes of idle" — and every OTHER bound in
            // this installer already names itself on expiry, so this was the one gap that made a
            // post-hoc log read incomplete. A wait nobody can see is a wait nobody can fix.
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[PackageInstaller] the teardown of {Root} did not report finishing within "
                    + "{Bound} — re-asking anyway, so the install continues. This bound is only "
                    + "reached when a teardown WEDGES, so an occurrence is a stalled disposal on "
                    + "that root, not a slow one, and it costs the install the full {Bound}",
                    rootPath, RootRecycleTimeout, RootRecycleTimeout);
                return Observable.Return(Unit.Default);
            })
            .DefaultIfEmpty(Unit.Default);
    }

    /// <summary>
    /// How long the install waits for a root recycle it issued to actually finish. Generous
    /// because it is only ever reached when a hub's teardown itself wedges; the normal case
    /// completes in a few milliseconds and the wait ends the instant it does.
    /// </summary>
    private static readonly TimeSpan RootRecycleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether the CONTENT publish may touch <paramref name="rootPath"/> yet — the gate in front
    /// of <see cref="SyncPackageContent"/>, and the second half of the warm's guard above.
    ///
    /// <para>🚨 The publish posts its <c>SyncContentFilesRequest</c> to the ROOT's address, and
    /// routing a message to a node that has no hub yet ACTIVATES one
    /// (<c>RoutingServiceBase.RouteImpl</c> → <c>CreateHub</c> →
    /// <c>IMeshNodeHubFactory.ResolveHubConfiguration</c>). So the publish is a root-activating
    /// touch exactly like the warm, running a few lines after it: the guard the warm gained is
    /// bypassed the moment a package carries a single byte of <c>content/**</c>. Same door, same
    /// mis-binding.</para>
    ///
    /// <para>Unlike the warm the publish is the POINT of the step, so this WAITS for the type's
    /// rebuild rather than giving up on the first read — a healthy self-typed package publishes
    /// normally, a fraction of a second later, into a root that then binds its own configuration.
    /// The wait ends the moment the type has a loadable build, or the moment a rebuild has RUN and
    /// settled without producing one (a committed stale stamp whose source no longer compiles).
    /// </para>
    ///
    /// <para>In that second case the answer is <c>false</c> and the publish is SKIPPED — because
    /// sending it is strictly worse than not sending it. Activating a root whose type is
    /// ABI-stale-with-a-failed-rebuild parks the enrichment on its framework-stale heal for the
    /// full slow-path budget, which is the same 60 s as the hub's request timeout: measured, the
    /// install burned 60.9 s and the sync was abandoned at the sender, its files landing (or not)
    /// on whichever side of that dead heat the machine happened to fall. A skip is deterministic,
    /// immediate, and recoverable — the assets are published by the next install once the type
    /// compiles, which for a package under auto-update is the very next poll. The log names the
    /// type so the cause is the package's compile error, not a mystery 404.</para>
    ///
    /// <para>🚨 <b>"A rebuild has run" is a RECORDED fact, not something this wait may insist on
    /// WITNESSING</b> (#1277 — the same shape as #1114/#1168, one caller further out). The
    /// <c>Compiled</c> latch below only turns true on an emission that shows the type
    /// Pending/Compiling. An install touches this gate TWICE — once from
    /// <see cref="SettleRetypedRoot"/> while the rebuild is still running, and once from
    /// <see cref="SyncPackageContent"/> a moment later — and by the second call the compile is
    /// over. With nothing left to emit, the fold could never reach <c>Settled</c> and the answer
    /// came only from <see cref="RootTypeSettleTimeout"/> elapsing: measured on
    /// <c>StaleStampRootBindingTest</c>, the first call answered in 3.2 s and the second burned
    /// the whole 90 s — a 93 s install of a package the installer had ALREADY decided to skip.
    /// So the fold also consults <see cref="NodeTypeCompileParkRegistry"/>, which is precisely the
    /// process's record of "no compile is coming for this type until its source changes". Nothing
    /// is retried and no bound moves; a wait for an event that provably cannot occur is replaced
    /// by the answer already in hand.</para>
    ///
    /// <para>The park is honoured only while NO release request is outstanding
    /// (<c>RequestedReleaseAt &gt; LastReleaseRequestHandledAt</c> — the release watcher's own
    /// trigger predicate, and the watcher un-parks before promoting it to Pending). A queued
    /// retry means a compile IS coming, so the wait stays a wait — the short-circuit can never
    /// jump ahead of a rebuild this very install asked for.</para>
    /// </summary>
    private static IObservable<bool> MayPublishIntoRoot(
        IMessageHub hub, string rootPath, IReadOnlyCollection<MeshNode> nodes, ILogger? logger)
    {
        var declaredType = InPackageTypeOf(rootPath, nodes);
        if (declaredType is null)
            return Observable.Return(true);

        var options = hub.JsonSerializerOptions;
        // Absent on a host without AddGraph (a unit-test hub): the fold then behaves exactly as
        // the pre-#1277 two-phase wait did.
        var parkRegistry = hub.ServiceProvider.GetService<NodeTypeCompileParkRegistry>();

        // Fold the type's stream into "is the answer known yet?" — the type has a loadable build,
        // or a rebuild we WITNESSED has ended without one, or the process has already RECORDED
        // that no rebuild is coming.
        return hub.GetWorkspace().GetMeshNodeStream(declaredType)
            .Where(node => node is not null)
            .Scan((Compiled: false, Loadable: false, Settled: false), (state, node) =>
            {
                // ContentAs, never `is NodeTypeDefinition`: a cross-hub mirror snapshot is
                // routinely un-materialized JSON, and a CLR type test blinds this to an in-flight
                // compile (the same reason NodeTypeEnrichmentHelpers.IsCompileInFlight reads it
                // this way).
                var def = node.ContentAs<NodeTypeDefinition>(options);
                var inFlight = def?.CompilationStatus
                    is CompilationStatus.Pending or CompilationStatus.Compiling;
                var compiled = state.Compiled || inFlight;
                // …and the SAME options here. HasLoadableBuild used to read the node with a CLR
                // type test, so on an un-materialized emission it answered "loadable" for the very
                // in-flight compile `inFlight` (one line up, on the same node) had just reported —
                // the two halves of this fold disagreeing about one snapshot, which settles the
                // wait early and recycles the root before its type has a build.
                var loadable = node.HasLoadableBuild(options);
                var parked = !inFlight
                    && !ReleaseRequestOutstanding(def)
                    && parkRegistry?.IsParked(declaredType) == true;
                return (compiled, loadable, loadable || parked || (compiled && !inFlight));
            })
            .Where(state => state.Settled)
            .Take(1)
            .Timeout(RootTypeSettleTimeout)
            .Select(state => state.Loadable)
            .Catch<bool, Exception>(_ => Observable.Return(false))
            .Do(loadable =>
            {
                if (!loadable)
                    logger?.LogWarning(
                        "[PackageInstaller] not publishing content assets into root {Root}: its NodeType "
                        + "{Type} has no build this framework can load, so the publish would activate the "
                        + "root against a type that cannot configure it — a request that parks for the "
                        + "whole slow-path budget and is then abandoned. Fix the type's compile error; the "
                        + "next install publishes the assets.",
                        rootPath, declaredType);
            });
    }

    /// <summary>
    /// Whether a release request has been flipped on the NodeType and not yet handled — the exact
    /// predicate <c>NodeTypeCompilationHelpers.InstallReleaseRequestWatcher</c> dispatches on. It
    /// is the one state in which a PARKED type is nonetheless about to recompile (the watcher
    /// un-parks before promoting the request to <c>Pending</c>), so the parked short-circuit in
    /// <see cref="MayPublishIntoRoot"/> must stand down while it holds.
    /// </summary>
    private static bool ReleaseRequestOutstanding(NodeTypeDefinition? def) =>
        def?.RequestedReleaseAt is { } requested
        && (def.LastReleaseRequestHandledAt is null
            || requested > def.LastReleaseRequestHandledAt.Value);

    /// <summary>
    /// Activates the roots this install just wrote, so a freshly installed package is not dark
    /// until someone navigates to it.
    ///
    /// <para>🚨 Warming a root is not a read — it ACTIVATES its hub, and a hub's NodeType
    /// enrichment binds ONCE for the hub's lifetime. So a root may only be warmed once its type
    /// can actually produce a configuration. For a SELF-TYPED root (the Store shape: root
    /// <c>Store</c> is nodeType <c>Store/Catalog</c>, defined by a child of the same package) the
    /// type is, at this moment, still carrying the compile stamp the node repo COMMITTED —
    /// <c>compilationStatus: Ok</c> with a months-old <c>compiledFrameworkVersion</c> and an
    /// assembly this mesh has never seen. Warming into that state made the outcome a race between
    /// the per-NodeType hub's framework-stale kickoff (which flips Pending, so enrichment WAITS for
    /// the rebuild) and this activation (which, if it wins, snaps the stale <c>Ok</c>, spends the
    /// single self-heal retry and can end on the silent defaults-only fallback). Measured margin on
    /// a developer machine: 13 ms. A loaded CI runner loses it, and the root then serves only the
    /// generic areas — "No renderer is registered for area <c>Tests</c> on hub <c>Store</c>", the
    /// plugin gate's Store/Catalog RED (2026-07-29, recurred 2026-08-10 on three different PRs).
    /// The recycle a few lines above exists precisely so the root re-activates against its FINAL
    /// type; warming before that type is loadable defeated it.</para>
    ///
    /// <para>So: when a root is typed by a NodeType this very package installs, SKIP the warm
    /// unless that type currently has a build an instance could load
    /// (<see cref="MeshDataSourceExtensions.AwaitLoadableBuild"/>, read once, bounded by
    /// <see cref="RootTypeProbeTimeout"/>). Skipping is the safe answer, and it is cheap: warming
    /// is only an optimisation against "the root stays dark until something touches it", whereas a
    /// warm into an unloadable type PINS the wrong configuration. The next real access — the gate's
    /// render, a visitor, the next install — activates the root against the rebuilt type and binds
    /// correctly. Deliberately NOT a wait: the installer runs on the mesh that is compiling, so
    /// holding here would trade a mis-binding for a stalled install.</para>
    /// </summary>
    /// <summary>
    /// Recycles the retyped partition root and waits for it to come back — the ORDERED replacement
    /// for the fire-and-forget <c>DisposeRequest</c> that used to be posted at write time.
    ///
    /// <para>🚨 Two things were wrong with posting it there, and they compound. It was MISTIMED:
    /// the in-package NodeType still advertised the compile stamp the repo COMMITTED, so the
    /// re-activation the recycle invites bound the fallback configuration for the new hub's
    /// lifetime — exactly what #1101 stopped the warm from doing. And it OUTLIVED the install: the
    /// teardown was still running when Install returned, so the next touch of the root — the
    /// content publish, a client's <c>SubscribeRequest</c>, anything — landed mid-teardown and got
    /// <c>HubDisposingException</c>. Both halves of <c>StaleStampRootBindingTest</c> are that
    /// failure ("cannot create '/content/'" and "cannot create 'LayoutAreaReference { Area =
    /// Tests }'"), and in both the reply died with the hub, so the caller waited out its full
    /// request timeout for work that never happened.</para>
    ///
    /// <para>So the recycle now runs where it can do its job: after the type's rebuild has settled
    /// (<see cref="MayPublishIntoRoot"/> is that wait, and it answers promptly for a type whose
    /// rebuild produced nothing loadable — there is nothing worth rebinding to, so the recycle is
    /// skipped), and it WAITS for the root to answer before the install proceeds. Called only when
    /// the placeholder dance actually ran, i.e. when there is a placeholder binding to replace.</para>
    /// </summary>
    private static IObservable<Unit> SettleRetypedRoot(
        IMessageHub hub, string? rootPath, IReadOnlyCollection<MeshNode> nodes, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return Observable.Return(Unit.Default);

        return MayPublishIntoRoot(hub, rootPath!, nodes, logger)
            .SelectMany(rebindable =>
            {
                if (!rebindable)
                {
                    logger?.LogInformation(
                        "[PackageInstaller] not recycling root {Root}: its in-package NodeType has no "
                        + "build an instance could load, so a fresh hub would bind the fallback too. "
                        + "The rebind watcher recycles it once the type compiles.", rootPath);
                    return Observable.Return(Unit.Default);
                }

                // Impersonated for the same reason as the pings below: the recycle runs from the
                // installer's chain, where no ambient AccessContext exists on a fresh boot.
                var accessService = hub.ServiceProvider.GetService<AccessService>();
                using (accessService?.ImpersonateAsSystem())
                    hub.Post(new DisposeRequest(), o => o.WithTarget(new Address(rootPath!)));
                return WaitForRootReady(hub, rootPath!, logger);
            });
    }

    /// <summary>
    /// Waits until <paramref name="rootPath"/>'s hub answers a <see cref="PingRequest"/>.
    ///
    /// <para>Ordering is the whole primitive: the ping is enqueued on the root BEHIND any
    /// <c>DisposeRequest</c> already in flight, so it cannot be answered by the hub that is going
    /// away — only by the one that comes after, and answering it IS that re-activation. A rejected
    /// or undelivered ping means the teardown is still running, so it retries. The
    /// <see cref="RootReadyTimeout"/> is a graceful sink, never a wedge: a root that never answers
    /// is logged and the install carries on.</para>
    /// </summary>
    private static IObservable<Unit> WaitForRootReady(
        IMessageHub hub, string rootPath, ILogger? logger)
    {
        var address = new Address(rootPath);
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Defer(Probe)
            .Repeat()
            .Where(alive => alive)
            .FirstAsync()
            .Timeout(RootReadyTimeout)
            .Select(_ => Unit.Default)
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception,
                    "[PackageInstaller] root {Root} never answered after its recycle — continuing; "
                    + "whatever touches it next may still land on a hub that is tearing down",
                    rootPath);
                return Observable.Return(Unit.Default);
            });

        // 🚨 Impersonated PER PING (each Repeat re-subscription is its own post), because a
        // fresh-boot reconcile runs from a hosted-service chain with no ambient AccessContext —
        // without this, PostPipeline refused EVERY ping ("AccessContext must never be null for an
        // application post"): a fail-level storm per retry until RootReadyTimeout, the wait
        // degraded into a pure delay, and the install then proceeded against a hub that was still
        // tearing down. RunAsSystem seals the scope at Subscribe (the identity-latch ratchet,
        // #1790); an infrastructure probe posts under SYSTEM at its call site, never off ambient.
        IObservable<bool> Probe() =>
            accessService.RunAsSystem(() =>
                    hub.Observe<PingResponse>(new PingRequest(), o => o.WithTarget(address)))
                .Take(1)
                .Timeout(RootPingTimeout)
                .Select(_ => true)
                // The delay bounds the retry loop: a DeliveryFailure answers immediately, and
                // re-subscribing on it with no pause would spin the hub for the whole budget.
                .Catch<bool, Exception>(_ => Observable.Return(false).Delay(RootPingRetryDelay));
    }

    private static IObservable<Unit> WarmInstalledRoots(
        IMessageHub hub, PackageManifest manifest, IReadOnlyCollection<MeshNode> nodes, ILogger? logger)
    {
        var workspace = hub.GetWorkspace();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var roots = nodes
            .Select(n => n.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0)
            return Observable.Return(Unit.Default);

        // True when warming this root now cannot pin the wrong configuration: either its type is
        // not one this package defines (nothing about to be rebuilt), or that type already has a
        // build an instance could load. One bounded READ — never a wait for a compile.
        IObservable<bool> MayWarm(string root)
        {
            var declaredType = InPackageTypeOf(root, nodes);
            if (declaredType is null)
                return Observable.Return(true);
            return workspace.GetMeshNodeStream(declaredType)
                .Where(node => node is not null)
                .Take(1)
                .Timeout(RootTypeProbeTimeout)
                .Select(node => node.HasLoadableBuild(hub.JsonSerializerOptions))
                .Catch<bool, Exception>(_ => Observable.Return(false))
                .Do(loadable =>
                {
                    if (!loadable)
                        logger?.LogInformation(
                            "[PackageInstaller] not warming root {Root}: its NodeType {Type} has no "
                            + "build this framework can load yet (the repo's committed compile stamp "
                            + "is being rebuilt). Warming now would bind the root to the fallback "
                            + "configuration for its hub's lifetime; the first real access after the "
                            + "rebuild binds it correctly.",
                            root, declaredType);
                });
        }

        // PHASE 1 — ACTIVATE, strictly sequentially. Each activation's gating pass WRITES its
        // partition's access table, and concurrent passes deadlock (40P01) on the shared
        // effective-permissions rebuild, so this half must stay a Concat.
        var activated = roots
            .Select(root => Observable
                .Using(
                    () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
                    _ => MayWarm(root)
                        .SelectMany(mayWarm => mayWarm
                            ? workspace.GetMeshNodeStream(root)
                                .Where(node => node is not null)
                                .Take(1)
                                .Timeout(WarmTimeout)
                                .Select(_ => true)
                            : Observable.Return(false)))
                .Where(warmed => warmed)
                .Select(_ => root)
                .Catch<string, Exception>(exception =>
                {
                    logger?.LogWarning(exception,
                        "[PackageInstaller] warming installed root {Root} failed — it stays dark until "
                        + "something else activates it", root);
                    return Observable.Empty<string>();
                }))
            .ToObservable()
            .Concat()
            .Do(root => logger?.LogInformation("[PackageInstaller] warmed installed root {Root}", root))
            .ToList();

        // PHASE 2 — wait for the cover grants, CONCURRENTLY.
        //
        // 🚨 Warming the hub is only half of "installed". The gating pass that makes the partition
        // READABLE runs off the activation, so returning at the end of phase 1 reports a clean
        // install over a partition whose cover still denies every viewer. Wait for the grant itself.
        //
        // 🚨 But the wait must NOT ride the sequential chain. It only OBSERVES — a query, no
        // write — so it carries none of the serialisation phase 1 needs, and ridden sequentially a
        // boot installing eight gated packages would pay the detector budget eight times over.
        // Merged, the whole install pays it at most once.
        //
        // The predecessor of this code appeared to be fast here, but only because it asked an
        // exact-path GetMeshNodeStream about a node that is usually absent: that NotFounds
        // immediately, so the wait never actually waited for ANY grant to land — and it poisoned the
        // path's storm breaker on the way past, which is what then suppressed the gating write
        // itself (#2229 item A).
        return activated
            .SelectMany(warmed =>
            {
                // 🚨 Only roots whose NodeType THIS PACKAGE DEFINES. The cover grant is written by
                // that type's gating configuration, which lives plugin-side — core cannot introspect
                // it, which is exactly why the grant is addressed as a well-known PATH here. So for a
                // root typed by something this install did not bring, there is no gating pass of ours
                // to wait for and the bound would be spent entirely on dead air.
                //
                // 🚨 …and only roots this install left GATED (CoverGrantExpected). That filter is
                // the fix for the stall: the grant is optional BY THIS METHOD'S OWN CONTRACT, so on
                // a partition the installer itself published, the query could never emit and every
                // install paid the whole budget before proceeding green — 30.2 s each, 60.4 s for a
                // test doing two installs, on the PRODUCTION install path as much as in the suite.
                // Filtering here rather than inside the detector keeps the merge width honest: a
                // root with nothing owed does not occupy one of GatingWaitConcurrency's slots.
                //
                // This can only ever skip a DIAGNOSTIC: DetectGatingStall is documented "never
                // fatal", its result is discarded, and its whole product is the log line inside it.
                // It cannot change what is installed or who can read it.
                var gating = warmed
                    .Where(root => InPackageTypeOf(root, nodes) is not null)
                    .Where(root => CoverGrantExpected(manifest, root))
                    .ToList();
                if (gating.Count == 0)
                    return Observable.Return(Unit.Default);
                // 🚨 SYSTEM, like phase 1 — the whole install runs as System and the _Access
                // listing is only readable that way; under the ambient (empty) identity the
                // listing would come back empty and the wait would spend its full bound on a
                // grant that is actually there. RunAsSystem, never Observable.Using: the latter
                // opens the scope on the SUBSCRIBING thread and closes it on the terminating one,
                // leaving the subscriber latched as system-security (#1790, and the shape
                // ImpersonationScopeSiteRatchetGuard refuses at any new site).
                return accessService.RunAsSystem(() => gating
                    .Select(root => DetectGatingStall(hub, manifest, root, logger)
                        .Select(_ => Unit.Default))
                    .ToObservable()
                    // 🚨 BOUNDED. A bare Merge() subscribes every root at once, and each one is a
                    // LIVE mesh query held open until the grant lands or the bound elapses — a boot
                    // installing a large selection would fan that out with no ceiling at all. This
                    // repo's failure mode for exactly that is documented: storm -> action block
                    // saturated -> liveness probe times out -> pod pulled from the Service -> 502 ->
                    // SIGKILL. Merge's own maxConcurrent is the bound (never a SemaphoreSlim, which
                    // parks a hub thread); it costs nothing in latency here because the waits
                    // overlap, and the whole phase is still capped by GatingDetectorBudget.
                    .Merge(GatingWaitConcurrency)
                    .DefaultIfEmpty(Unit.Default)
                    .LastAsync());
            });
    }

    /// <summary>
    /// The install record's <c>AuthorizedBy</c> on a (re-)stamp: the principal that authorized THIS
    /// action when there is one, otherwise the existing record's — the record is rebuilt from the
    /// CATALOG manifest, which never carries the field, so without the carry-forward the first
    /// unattended update would erase the admin authorization it is itself checked against. Pure.
    /// </summary>
    // Internal for the commercial-authorization pin (InternalsVisibleTo).
    internal static string? SeedAuthorizedBy(PackageManifest? existingRecord, string? authorizingUserId) =>
        string.IsNullOrWhiteSpace(authorizingUserId) ? existingRecord?.AuthorizedBy : authorizingUserId;

    /// <summary>
    /// The install record's <see cref="PackageManifest.Source"/> on a (re-)stamp: the registry source
    /// the CURRENT install came from when it is known, otherwise the one already recorded. Same
    /// carry-forward as <see cref="SeedAuthorizedBy"/>, for the same reason and now with teeth.
    ///
    /// <para>🚨 Not every lister stamps the field: it is set by the registry as it merges its sources
    /// and by the default install's own lister, but a catalog rendered straight off a repo path
    /// (<c>PluginUpdateWatcher</c>, a <c>PluginCatalog</c> node) hands over a manifest with no source
    /// at all. Rebuilding the record from that manifest verbatim would ERASE a stamp a real install
    /// wrote — and since #1772 the bundle route matches the caller's <c>PluginGrant</c> against
    /// exactly this field, an erased source makes the package unservable to every consumer, silently.
    /// A distribution lane that goes dark on the first auto-update is the worst kind of regression:
    /// consumers just quietly compile instead.</para>
    ///
    /// <para>🚨 <b>It never INVENTS a source</b> — the result is either the one this install states
    /// or the one already recorded, never a guess, so it can only ever name a source some real
    /// install came from. A stated source does WIN over the recorded one: that is the newer fact
    /// about where the package comes from (a package genuinely moved between sources must stop
    /// claiming the old one), exactly as <see cref="SeedAuthorizedBy"/> prefers the principal that
    /// authorized THIS action. The carry-forward applies only where the current install supplies
    /// nothing.</para>
    /// </summary>
    /// <param name="existingRecord">The install record being re-stamped, or null on a first install.</param>
    /// <param name="manifest">The catalog manifest this install is being written from.</param>
    /// <returns>The source to record.</returns>
    internal static string? SeedSource(PackageManifest? existingRecord, PackageManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.Source) ? existingRecord?.Source : manifest.Source;

    /// <summary>
    /// Publishes the package's CONTENT-COLLECTION assets — the raw binaries it commits under
    /// <c>{package}/content/**</c> (course videos and their posters, og images, fonts) — into the
    /// target partition root's <c>content</c> collection, so merging a course or plugin publishes it
    /// COMPLETELY and nothing has to be uploaded to each portal out of band (issue #848).
    ///
    /// <para>No new mechanism: this reaches, from a REGISTRY install, the very path the compiled-source
    /// GitSync import already uses. <see cref="ContentAssetMapper"/> classifies the repo paths exactly
    /// as <c>GitHubSyncService.ParseSnapshot</c> does, and
    /// <see cref="ContentImportExtensions.SyncContentFiles"/> posts one
    /// <see cref="SyncContentFilesRequest"/> carrying the BYTES INLINE to the hub that owns the
    /// collection — the partition ROOT, where the portal mounts <c>content</c> (children inherit it).
    /// So a file committed at <c>AgenticPrimer/content/videos/primer1.mp4</c> becomes collection-relative
    /// <c>videos/primer1.mp4</c> on node <c>AgenticPrimer</c> — i.e. exactly what the course's own
    /// <c>&lt;video src&gt;</c> resolves to through the content route.</para>
    ///
    /// <para>🚨 <b>Additive, never a mirror.</b> The GitSync importer mirrors (pruning what the source
    /// dropped) because it owns the whole Space; an install does not. Portals today serve content that
    /// was uploaded by hand and never committed — measured on memex, <c>AgenticEngineering</c>'s
    /// <c>module1-intro.mp4</c> is served but absent from git — and a pruning mirror would DELETE
    /// exactly the files this issue exists to stop losing. Pruning is incoherent on the incremental
    /// path anyway, which only ever fetches the manifest diff's changed files.</para>
    ///
    /// <para>A failure LOGS and continues rather than failing the install: a mesh with no <c>content</c>
    /// collection mounted (tests, a minimal host) answers "collection not found", and by this point the
    /// nodes have already landed — throwing would leave the package half-written. The written count is
    /// logged so an incomplete publish is visible rather than silent.</para>
    ///
    /// <para>🚨 The post ACTIVATES the root's hub (see <see cref="MayPublishIntoRoot"/>), so it is
    /// gated on the root's in-package NodeType having a build an instance can load — the same
    /// condition the warm consults. Gating INSIDE this method rather than at its three call sites
    /// is deliberate: a call site that forgot would silently reopen the door.</para>
    /// </summary>
    private static IObservable<int> SyncPackageContent(
        IMessageHub hub, string? rootPath, string? sourceFolder,
        IReadOnlyList<PackageFile> files, IReadOnlyCollection<MeshNode> nodes, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return Observable.Return(0);

        // TryClassify is itself the path-only precheck: it invokes the bytes factory ONLY once it
        // has classified the path as content, so node files never materialize bytes. A separate
        // IsContentPath filter in front just repeated its TrySplit work.
        var assets = files
            .Select(file => ContentAssetMapper.TryClassify(
                FolderRelative(file.RelativePath, sourceFolder), () => file.Bytes))
            .Where(asset => asset is not null).Select(asset => asset!)
            .ToArray();
        if (assets.Length == 0)
            return Observable.Return(0);

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return MayPublishIntoRoot(hub, rootPath!, nodes, logger)
            .SelectMany(mayPublish => mayPublish
                // The root must not be TEARING DOWN when the publish lands, or
                // ContentService.CreateCollection throws HubDisposingException — no byte written —
                // and the ImportContentResponse the handler still posts dies with the hub, so the
                // caller waits out its whole 60 s request timeout for assets that never landed.
                // SettleRetypedRoot has normally already done this; the probe costs one ping when
                // the root is up, and covers the call sites that do not run it.
                ? WaitForRootReady(hub, rootPath!, logger).SelectMany(_ => Publish())
                : Observable.Return(0));

        IObservable<int> Publish() => ContentAssetMapper.ToContentSyncs(rootPath!, assets)
            // Impersonated per post, like every other installer write: the pipeline hops schedulers
            // and an ambient impersonation does not survive those hops (see Upsert).
            .Select(sync => Observable.Using(
                    () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
                    _ => hub.SyncContentFiles(sync.NodePath)
                        .To(sync.TargetCollection, sync.TargetPath)
                        .Add(sync.Files)
                        .Mirror(false)
                        .Post())
                .Select(response => response.Success
                    ? response.FilesImported
                    : throw new InvalidOperationException(
                        response.Error ?? "content sync failed without an error message"))
                // 🚨 "The address may reactivate (recycle / restart); retry to get the
                // authoritative answer" is not advice — it is the framework's TRANSIENT verdict,
                // and this is the one consumer that used to throw it away and declare the
                // package's binaries lost. A root is recycled TWICE during an install: once by the
                // installer itself (sequenced by SettleRetypedRoot, and probed again by
                // WaitForRootReady just above) and once by the framework's NodeTypeRebindWatcher
                // when the change feed reports the retype. That second one belongs to nobody and
                // can land AFTER the readiness probe has passed, which is why the proactive wait
                // needs this reactive backstop behind it — the probe answers "is it up now", not
                // "will it still be up when the bytes arrive".
                //
                // Not a blind retry, and nothing to sleep on: the re-ask waits for the exact
                // teardown that rejected it (that hub instance's own DisposalCompleted) and then
                // asks the address again, which activates the FINAL hub. Only a typed transient
                // is re-asked — an application failure, a bad path, a missing collection all still
                // fail on the first answer, so a genuinely broken publish cannot hide in a loop.
                .RetryWhen(faults => faults
                    .Select((fault, attempt) => (fault, attempt))
                    .SelectMany(f => IsRootRecycling(f.fault) && f.attempt < RootRecycleReAsks
                        ? RootTeardownSettled(hub, sync.NodePath, logger)
                        : Observable.Throw<Unit>(f.fault)))
                .Catch<int, Exception>(exception =>
                {
                    logger?.LogWarning(exception,
                        "[PackageInstaller] publishing {Count} content asset(s) to {Node} failed — the "
                        + "package's nodes are installed but its binaries are not being served",
                        sync.Files.Count, sync.NodePath);
                    return Observable.Return(0);
                }))
            .ToObservable()
            .Concat()
            .Sum()
            .Do(written => logger?.LogInformation(
                "[PackageInstaller] published {Written}/{Total} content asset(s) into {Root}'s "
                + "content collection", written, assets.Length, rootPath));
    }

    /// <summary>
    /// A package file's path RELATIVE to its package folder — the form
    /// <see cref="ContentAssetMapper"/> classifies against (the "relative to the Space" shape GitSync
    /// feeds it, where the fetch has already stripped the subdirectory). A path that does not start
    /// with the folder is returned unchanged.
    /// </summary>
    private static string FolderRelative(string relativePath, string? sourceFolder)
    {
        if (string.IsNullOrEmpty(sourceFolder))
            return relativePath;
        var prefix = sourceFolder + "/";
        return relativePath.StartsWith(prefix, StringComparison.Ordinal)
            ? relativePath[prefix.Length..]
            : relativePath;
    }

    private static IObservable<MeshNode> WriteInstalledRecord(
        IMessageHub hub, PackageManifest manifest, string installedFromRef, int count,
        ModuleManifest? moduleManifest = null, string? authorizingUserId = null)
    {
        var recordPath = $"{InstalledPartition}/{manifest.Id}";
        var serializerOptions = hub.JsonSerializerOptions;
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();

        // Read the existing record first: the re-stamp must preserve the per-record policy
        // (SeedAutoUpdate) rather than rebuild it from the policy-less catalog manifest.
        var existing = persistence is not null
            ? persistence.Read(recordPath, serializerOptions).Take(1)
                .Select(n => n?.ContentAs<PackageManifest>(serializerOptions))
                .Catch<PackageManifest?, Exception>(_ => Observable.Return<PackageManifest?>(null))
            : Observable.Return<PackageManifest?>(null);

        return existing.SelectMany(existingRecord =>
        {
            var record = MeshNode.FromPath(recordPath) with
            {
                NodeType = PackageNodeType,
                Name = manifest.Name ?? manifest.Id,
                State = MeshNodeState.Active,
                Content = manifest with
                {
                    InstalledFromRef = installedFromRef,
                    InstalledAtUtc = DateTimeOffset.UtcNow,
                    InstalledNodeCount = count,
                    // The manifest baseline the NEXT update diffs against (null when the package ships
                    // no manifest.lock — the legacy full path stays in charge then).
                    ModuleVersion = moduleManifest?.ModuleVersion ?? manifest.ModuleVersion,
                    // The RELEASED SemVer, from the same lock the ModuleVersion above comes from.
                    // Persisted because nothing else in the mesh carries it: the plugin node holds
                    // only the AUTHORED major.minor, and the lock is a repo artifact GitSync does
                    // not import. Without it the portal cannot name the version a module shipped at.
                    ReleasedVersion = moduleManifest?.Version ?? manifest.ReleasedVersion,
                    InstalledFiles = moduleManifest?.Files ?? manifest.InstalledFiles,
                    // Transport-only: the candidate-side map rides in on the CATALOG entry for the
                    // diff and must not be persisted — the record's baseline is InstalledFiles
                    // alone, exactly as ManifestFiles' own doc promises (Copilot catch: a full
                    // install passes the catalog manifest through, so without this it leaked in).
                    ManifestFiles = null,
                    AutoUpdate = SeedAutoUpdate(
                        existingRecord, hub.ServiceProvider.GetService<PluginCatalogOptions>()),
                    // WHO authorized this install — what an unattended update of a commercial
                    // package is re-checked against (#830).
                    AuthorizedBy = SeedAuthorizedBy(existingRecord, authorizingUserId),
                    // WHICH registry source it came from — what a consumer's PluginGrant is matched
                    // against when it asks this instance for the package's bundle (#1772). Carried
                    // forward for the same reason as AuthorizedBy: not every lister stamps it.
                    Source = SeedSource(existingRecord, manifest),
                },
            };
            // System-impersonated like every installer write (Using — see Upsert): this runs after
            // barrier scheduler hops, where no ambient context survives.
            // Off-router issuing: the boot default-install runs with the DI root mesh hub — a
            // target-less CreateOrUpdateNodeRequest posted there runs on the router (ROUTER_TRAFFIC).
            return Observable.Using(
                    () => hub.ServiceProvider.GetService<AccessService>()?.ImpersonateAsSystem()
                          ?? System.Reactive.Disposables.Disposable.Empty,
                    _ => hub.NodeOperationIssuingHub()
                        .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(record)))
                .FirstAsync().Select(d => d.Message)
                .SelectMany(resp => resp.Success
                    ? Observable.Return(resp.Node!)
                    : Observable.Throw<MeshNode>(new InvalidOperationException(
                        $"Recording install of '{manifest.Id}' failed: {resp.Error}")));
        });
    }

    // Installs a Code package: synthesize the NodeType node from the manifest's configuration, import
    // the package's Source/*.cs files as its Code nodes (rebased UNDER the NodeType so its default
    // Sources query finds them), and record the install. Creating/updating the NodeType + Source nodes
    // drives the mesh's Roslyn compile — but only when something actually changed, so an unchanged
    // re-install neither rewrites nodes nor recompiles.
    private static IObservable<InstallResult> InstallCode(
        IMessageHub hub, PackageManifest manifest, IReadOnlyList<PackageFile> files,
        string installedFromRef, ILogger? logger, int batchSize, string? authorizingUserId)
    {
        if (string.IsNullOrWhiteSpace(manifest.NodeTypeConfiguration))
            return Observable.Throw<InstallResult>(new InvalidOperationException(
                $"Code package '{manifest.Id}' has no nodeTypeConfiguration."));

        var partition = string.IsNullOrWhiteSpace(manifest.TargetPartition) ? "type" : manifest.TargetPartition!;
        var nodeTypePath = $"{partition}/{manifest.Id}";
        var sourceFolder = manifest.SourceFolder ?? manifest.Id;
        var parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions, hub.ServiceProvider.GetServices<IFileFormatParser>());

        var sourceNodes = files
            .Where(f => !IsManifest(f.RelativePath))
            .Select(f => ParseNode(parsers, nodeTypePath, sourceFolder, f, logger, hub.JsonSerializerOptions))
            .Where(n => n is not null).Select(n => n!)
            .ToArray();

        if (sourceNodes.Length == 0)
            return Observable.Throw<InstallResult>(new InvalidOperationException(
                $"Code package '{manifest.Id}' has no Source/*.cs files."));

        var nodeTypeNode = MeshNode.FromPath(nodeTypePath) with
        {
            NodeType = MeshNode.NodeTypePath,
            Name = manifest.Name ?? manifest.Id,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { Configuration = manifest.NodeTypeConfiguration },
        };

        var all = new[] { nodeTypeNode }.Concat(sourceNodes).ToArray();

        if (RefuseIfStaticShadowed(hub, manifest, all, logger) is { } shadowed)
            return shadowed;

        var options = hub.JsonSerializerOptions;
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();

        // NodeType first (so its Source nodes attach under a present type), then the Source nodes;
        // each is skipped when unchanged.
        return EnsureInstallPartitions(hub, partition, logger)
            .SelectMany(_ => UpsertIfChanged(hub, persistence, nodeTypeNode, options))
            .SelectMany(typeWritten => sourceNodes
                .Select(n => UpsertIfChanged(hub, persistence, n, options))
                .ToObservable().Merge(batchSize).ToList()
                .Select(srcWrites => typeWritten
                    ? srcWrites.Count(w => w) + 1
                    : srcWrites.Count(w => w)))
            .SelectMany(written =>
            {
                var result = new InstallResult(all.Length, written);
                logger?.LogInformation(
                    "Installed code package {Id} v{Version}: {Written} written, {Unchanged} unchanged ({Path}) @ {Ref}",
                    manifest.Id, manifest.Version, result.Written, result.Unchanged, nodeTypePath, installedFromRef);
                // Only recompile when something actually changed — an unchanged re-install must not
                // kick a redundant Roslyn build. SEQUENCED, never fire-and-forget: the observable is
                // cold, so a bare call would request no release at all, and an install that cannot
                // order against the compiles it starts is the defect #1732 is about.
                var releases = written > 0
                    ? SeedThenRequestReleases(hub, [nodeTypePath], logger)
                    : Observable.Return(System.Reactive.Unit.Default);
                return releases
                    .SelectMany(_ => WriteInstalledRecord(
                        hub, manifest, installedFromRef, all.Length, authorizingUserId: authorizingUserId))
                    .SelectMany(_ => WarmInstalledRoots(hub, manifest, all, logger))
                    .Select(_ => result);
            });
    }

    // Bulk-reads the CURRENT persisted state of every path — ONE round-trip on Postgres
    // (IStorageAdapter.ReadMany batches `WHERE (namespace, id) IN (…)`), parallel single reads
    // elsewhere — replacing the per-node read that made a course-sized install pay N sequential
    // probes before writing anything (#815). Missing paths are absent from the dictionary. A read
    // FAILURE emits null — "existence unknown" — which the caller must treat as "no bulk routing":
    // an empty snapshot would make every node look NEW and bulk-write nodes that may exist,
    // bypassing the per-node handler path existing nodes require. Null instead keeps the old
    // write-on-failure bias per node THROUGH the validating request path.
    private static IObservable<IReadOnlyDictionary<string, MeshNode>?> ReadCurrent(
        IStorageAdapter? persistence, IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => persistence is null || paths.Count == 0
            ? Observable.Return<IReadOnlyDictionary<string, MeshNode>?>(
                ImmutableDictionary<string, MeshNode>.Empty)
            : persistence.ReadMany(paths, options)
                .Where(n => n is not null)
                .ToList()
                .Select(found => (IReadOnlyDictionary<string, MeshNode>?)found
                    .GroupBy(n => n.Path, StringComparer.Ordinal)
                    .ToImmutableDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal))
                .Catch<IReadOnlyDictionary<string, MeshNode>?, Exception>(_ =>
                    Observable.Return<IReadOnlyDictionary<string, MeshNode>?>(null));

    // Upserts a node only if it is new or meaningfully changed; returns true if it wrote, false if it
    // skipped an unchanged node. Reads the CURRENT persisted node authoritatively via the storage
    // adapter (the SAME read the CreateOrUpdate handler uses) — no eventual-consistency lag and no
    // per-node hub activation. Absent path -> null -> written; a read failure falls back to writing.
    private static IObservable<bool> UpsertIfChanged(
        IMessageHub hub, IStorageAdapter? persistence, MeshNode node, JsonSerializerOptions options)
    {
        var existing = persistence is not null
            ? persistence.Read(node.Path, options)
            : Observable.Return<MeshNode?>(null);
        return existing
            .Take(1)
            .SelectMany(current => DecideAndWrite(hub, current, node, options))
            .Catch<bool, Exception>(_ => Upsert(hub, node).Select(_ => true));
    }

    // The write decision against a KNOWN current state — the body UpsertIfChanged wraps with its
    // own authoritative read. The node-repo install path calls this directly with one BULK-read
    // snapshot (ReadCurrent) instead of paying a per-node read round-trip (#815).
    private static IObservable<bool> DecideAndWrite(
        IMessageHub hub, MeshNode? current, MeshNode node, JsonSerializerOptions options)
    {
        // A CLAIMED node — the user set a non-Include SyncBehavior on it, typically after
        // modifying it — is theirs, not the repo's. Skip it, exactly as the static-repo
        // importer does. This is one of the fences that makes UNATTENDED updates (opted-in
        // records; our deployments opt in wholesale) safe: a local edit under a claim can
        // never be clobbered by a green build. An unclaimed local edit IS overwritten —
        // claiming is the deliberate act that decouples a node from its package.
        if (current is not null && current.SyncBehavior != SyncBehavior.Include)
            return Observable.Return(false);
        if (current is not null && IsUnchanged(current, node, options))
            return Observable.Return(false);
        if (current is not null && Environment.GetEnvironmentVariable("MW_INSTALL_DIFF") == "1")
        {
            var cur = ContentSignature(current.Content, options);
            var inc = ContentSignature(node.Content ?? current.Content, options);
            var at = 0;
            while (at < cur.Length && at < inc.Length && cur[at] == inc[at]) at++;
            var lo = Math.Max(0, at - 120);
            Console.WriteLine($"[DIFF] {node.Path} scalars={ScalarsUnchanged(current, node)} " +
                $"lens={cur.Length}/{inc.Length} firstDiff@{at}");
            Console.WriteLine($"  cur({current.Content?.GetType().Name}): …{cur[lo..Math.Min(cur.Length, at + 160)]}");
            Console.WriteLine($"  inc({node.Content?.GetType().Name}): …{inc[lo..Math.Min(inc.Length, at + 160)]}");
        }
        return Upsert(hub, node).Select(_ => true);
    }

    /// <summary>
    /// True when applying <paramref name="incoming"/> onto <paramref name="current"/> would produce no
    /// real change — i.e. the fields the upsert actually applies (mirrors <c>UpdateAccordingToSourceNode</c>:
    /// Content + Name/NodeType/Icon/Category/State/PreRenderedHtml) are identical, ignoring the churn
    /// fields (LastModified/Version). This is the content-checksum that makes an update touch only what
    /// really changed.
    /// </summary>
    // Internal for the InstallSignatureAlignmentTest pin (InternalsVisibleTo).
    internal static bool IsUnchanged(MeshNode current, MeshNode incoming, JsonSerializerOptions options)
    {
        if (!ScalarsUnchanged(current, incoming))
            return false;
        // A NodeType node's stored content is ENRICHED by the live compile (CompilationStatus, release
        // stamps, …), so a whole-content compare would ALWAYS look "changed" on re-install and pointlessly
        // rewrite + recompile it. Compare only the authored fields the installer writes — the
        // Configuration lambda AND the Sources list (a source-list change alters what compiles, so it
        // must re-install + recompile; the source .cs are separate Code nodes, diffed on their own).
        if (current.Content is NodeTypeDefinition curDef && incoming.Content is NodeTypeDefinition inDef)
            return string.Equals(curDef.Configuration, inDef.Configuration, StringComparison.Ordinal)
                && (curDef.Sources ?? []).SequenceEqual(inDef.Sources ?? [], StringComparer.Ordinal);
        // Otherwise compare the full content, applying the incoming over current so an omitted field
        // does not read as a change — with WHICHEVER side is a raw JsonElement ALIGNED to the other
        // side's TYPE first (see AlignToPeer). Both mirror cases are live:
        //   • current typed / incoming element — the persisted side materialized C# defaults the
        //     repo file legitimately omits (the diagnosed PluginContent.Currency = "CHF" churn).
        //   • current element / incoming typed — the reading hub had not resolved the module's own
        //     type when the persisted side was read, while the incoming file deserialized typed and
        //     materializes defaults the persisted element omits (FractalStars/Stars, 2026-08-11:
        //     cur(JsonElement) {preset} vs inc(FractalContent) {children, deflection, generations,
        //     preset, stepFactor} — the idempotence flap that SURVIVED the one-sided alignment; its
        //     nondeterminism was exactly which side of the module's type-registration race the
        //     persisted read landed on).
        var effectiveIncoming = incoming.Content ?? current.Content;
        return ContentSignature(AlignToPeer(effectiveIncoming, current.Content, options), options)
            == ContentSignature(AlignToPeer(current.Content, effectiveIncoming, options), options);
    }

    /// <summary>
    /// Materialized-default alignment for the content compare: when ONE side is a raw
    /// <c>JsonElement</c> and its peer is TYPED, deserialize the element to the peer's type so both
    /// sides materialize the same C# property defaults. Signing a raw element against a typed peer
    /// reads every materialized default as a change: the NONDETERMINISTIC "re-install of the
    /// unchanged snapshot wrote 1 node(s)" root churn behind the plugins gate's flapping
    /// idempotence check. The asymmetry runs BOTH ways — which is why the compare calls this once
    /// per side: typed-current/element-incoming (the hub re-serialized the persisted node,
    /// <c>PluginContent.Currency = "CHF"</c>) and element-current/typed-incoming (the persisted
    /// read landed before the module's type registration while the repo file deserialized typed —
    /// FractalStars/Stars, the flap that survived one-sided alignment).
    ///
    /// <para>Guards: alignment happens only when the element's <c>$type</c> matches the peer
    /// content's serialized discriminator (a differing <c>$type</c> IS a real change and must never
    /// be masked by coercing into the wrong type), and a failed deserialize falls back to the raw
    /// element — worst case an idempotent rewrite, never a missed change.</para>
    /// </summary>
    private static object? AlignToPeer(object? candidate, object? peer, JsonSerializerOptions options)
    {
        if (candidate is not JsonElement { ValueKind: JsonValueKind.Object } el
            || peer is null or JsonElement)
            return candidate;
        try
        {
            // A differing $type IS a real change — never mask it by coercing into the wrong type.
            // Three discriminator cases on the element side, each deliberate:
            //   • absent      → align. Repo content files legitimately omit the discriminator (the
            //                   node's nodeType implies the content type); skipping here would
            //                   re-open the default-churn for every discriminator-less file.
            //   • string      → align only when it NAMES the peer's type (see NamesPeerType — the
            //                   peer's serialized discriminator OR its CLR short/full name, because
            //                   a runtime-compiled type serializes without a discriminator at all).
            //   • non-string  → malformed; skip alignment entirely (raw compare → the malformed
            //                   value shows as a change instead of being silently repaired).
            var hasDiscriminator = el.TryGetProperty("$type", out var it);
            if (hasDiscriminator && it.ValueKind != JsonValueKind.String)
                return candidate;
            var candidateType = hasDiscriminator ? it.GetString() : null;
            if (candidateType is not null && !NamesPeerType(candidateType, peer, options))
                return candidate;

            // Deserialize WITHOUT tolerating unknown members: with the ambient Skip handling, a
            // property the element carries but the peer type lacks would be silently dropped and a
            // real change read as unchanged. Disallow makes that case throw → the catch below →
            // raw compare → change detected (worst case an idempotent rewrite, never a miss).
            // The $type discriminator is stripped first — deserializing to the CONCRETE type
            // treats it as an unmapped member.
            var strict = new JsonSerializerOptions(options)
            {
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            };
            var withoutDiscriminator = System.Text.Json.Nodes.JsonObject.Create(el)!;
            withoutDiscriminator.Remove("$type");
            return withoutDiscriminator.Deserialize(peer.GetType(), strict) ?? candidate;
        }
        catch (JsonException)
        {
            return candidate;                       // schema drift / unknown member — raw compare
        }
    }

    /// <summary>
    /// Whether <paramref name="discriminator"/> (an element's <c>$type</c>) names
    /// <paramref name="peer"/>'s type — the same-type guard in front of the alignment.
    ///
    /// <para>🚨 The peer's SERIALIZED <c>$type</c> is not sufficient evidence on its own. A
    /// runtime-compiled content type is deliberately never adopted into a long-lived per-hub
    /// <c>ITypeRegistry</c> (a per-compile collectible identity would poison later resolutions and
    /// pin the assembly), so <c>ObjectPolymorphicConverter.Write</c> emits such a value with NO
    /// discriminator at all. Reading "no <c>$type</c>" as "a different type" skipped the alignment
    /// for exactly the packages whose content types are dynamic, and the raw compare then found the
    /// one and only difference — the <c>$type</c> member the element carries and the typed peer does
    /// not — so the node was rewritten on every re-install (<c>Underwriting/Rulebook/*</c>, 40
    /// nodes per run, allow-listed as known debt since). The discriminator IS the type's short or
    /// full CLR name by construction, so the peer's own name is the authoritative fallback.</para>
    /// </summary>
    private static bool NamesPeerType(string discriminator, object peer, JsonSerializerOptions options)
    {
        var peerType = peer.GetType();
        if (string.Equals(discriminator, peerType.Name, StringComparison.Ordinal)
            || string.Equals(discriminator, peerType.FullName, StringComparison.Ordinal))
            return true;
        // A peer whose hub DID register it may carry an explicit collection name that differs from
        // the CLR name — honour that too.
        return JsonSerializer.SerializeToNode(peer, options)
                is System.Text.Json.Nodes.JsonObject peerNode
            && peerNode.TryGetPropertyValue("$type", out var pt)
            && pt is System.Text.Json.Nodes.JsonValue pv
            && pv.TryGetValue<string>(out var pts)
            && string.Equals(discriminator, pts, StringComparison.Ordinal);
    }

    // The node's scalar fields, applying the incoming's non-null values over the current (mirrors
    // UpdateAccordingToSourceNode) — unchanged? The churn fields (LastModified/Version) are ignored.
    // MainNode is the one field that is NOT null-keeps-state; see the comment on its line.
    private static bool ScalarsUnchanged(MeshNode current, MeshNode incoming) =>
        (incoming.Name ?? current.Name) == current.Name
        && (incoming.NodeType ?? current.NodeType) == current.NodeType
        && (incoming.Icon ?? current.Icon) == current.Icon
        && (incoming.Category ?? current.Category) == current.Category
        && (incoming.State == default ? current.State : incoming.State) == current.State
        && (incoming.PreRenderedHtml ?? current.PreRenderedHtml) == current.PreRenderedHtml
        && (incoming.Order ?? current.Order) == current.Order
        // 🚨 MainNode's "was it set?" test is MeshNode.HasExplicitMainNode, NOT a null check — the
        // field is non-nullable and defaults to the node's own path, so a plain `?? current` would
        // read every untouched source as "re-parent to self" and demote every satellite. Missing
        // here as well as in the merge until #2631, which is why an install could never move one:
        // this gate skipped the write before the upsert was even asked (a package's _Access grant
        // carries an AUTHORED mainNode, preserved by ParseNodeRepoFile, and re-scoping it landed
        // nowhere).
        && (incoming.HasExplicitMainNode ? incoming.MainNode : current.MainNode) == current.MainNode;

    // Content serialized with the hub options ($type discriminators), then CANONICALIZED — object
    // keys sorted recursively — so the comparison is order-insensitive. It must be: on a
    // RE-install the stored side is often the TYPED content (the owning hub re-serialized it, in
    // record-declaration order) while the incoming side is the repo file's JsonElement (in file
    // order). Same values, different order — and an order-sensitive compare called every root
    // "changed" on every sync, which rewrote and RECOMPILED every plugin root forever. That was
    // the whole idempotence-pin failure the day the plugins gate first executed (2026-07-29):
    //   cur(PluginContent): {"$type":"PluginContent","body":…,"requires":…}
    //   inc(JsonElement):   {"$type":"PluginContent","requires":…,"body":…}
    private static string ContentSignature(object? content, JsonSerializerOptions options) =>
        content is null ? "" : Canonical(JsonSerializer.SerializeToNode(content, options));

    // Empty members (null / [] / {}) are DROPPED from the signature: a typed record serializes its
    // defaulted collection as "installPaths": [] while the repo file simply omits the property —
    // same meaning, and exactly the residue that kept every plugin root "changed" after the
    // key-order fix. Dropping empties is safe for change DETECTION: clearing a real value ( ["X"]
    // → [] ) still differs, because only ONE side's member vanishes from the signature.
    private static string Canonical(System.Text.Json.Nodes.JsonNode? node) => node switch
    {
        null => "null",
        System.Text.Json.Nodes.JsonObject obj => "{" + string.Join(",", obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (p.Key, Value: Canonical(p.Value)))
            .Where(p => p.Value is not ("null" or "[]" or "{}"))
            .Select(p => JsonSerializer.Serialize(p.Key) + ":" + p.Value)) + "}",
        System.Text.Json.Nodes.JsonArray arr => "[" + string.Join(",", arr.Select(Canonical)) + "]",
        _ => node.ToJsonString(),
    };

    // Installs a NODE-NATIVE plugin repo (node-per-file): the files ARE MeshNodes at their canonical
    // paths, so parse them verbatim (no partition rebase), upsert only the changed ones, and request a
    // live compile for every NodeType node. This is the shape MeshWeaver.Plugins ships.
    private static IObservable<InstallResult> InstallNodeRepo(
        IMessageHub hub, PackageManifest manifest, IReadOnlyList<PackageFile> files,
        string installedFromRef, ILogger? logger, int batchSize, string? authorizingUserId)
    {
        _ = batchSize; // node-repo installs are ordered (bucketed bulk saves + Concat), not fanned out
        var parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions, hub.ServiceProvider.GetServices<IFileFormatParser>());
        // The CI manifest sidecar (when the package ships one) becomes the install record's diff
        // baseline — the next update touches only what its manifest diff names.
        var moduleManifest = files
            .Where(f => ModuleManifest.IsManifestPath(f.RelativePath))
            .Select(f => ModuleManifest.TryParse(f.Content, logger))
            .FirstOrDefault(m => m is not null);
        var nodes = ParseAll(parsers, files, manifest.Id, logger, hub.JsonSerializerOptions);

        if (nodes.Length == 0)
            return Observable.Throw<InstallResult>(new InvalidOperationException(
                $"Node-repo plugin '{manifest.Id}' has no installable nodes."));

        if (RefuseIfStaticShadowed(hub, manifest, nodes, logger) is { } shadowed)
            return shadowed;

        var options = hub.JsonSerializerOptions;
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        var nodeTypePaths = nodes.Where(n => n.Content is NodeTypeDefinition).Select(n => n.Path).ToArray();

        // Ordering solves two chicken-and-eggs at once:
        // (1) a NodeType's Source must land BEFORE the NodeType itself — creating the NodeType
        //     triggers the live compile, which reads its Source children;
        // (2) every NodeType must land BEFORE any instance that REFERENCES it — including the
        //     package ROOT when its type ships in the same package (the Store: the root is
        //     nodeType Store/Catalog, defined by a child node). The not-registered probe only
        //     needs the type NODE to exist, not its compile.
        // Underscore satellites (_Access, _Policy, …) land LAST — a satellite must anchor under
        // an already-existing owner.
        //
        // 🚨 Bucket 0 is EXACTLY the types' compile inputs — the Source/ and Test/ subtrees.
        // It must NOT swallow every descendant of a type path: a typed INSTANCE nested under
        // its leaf-shaped type (ClaimsDeepfield/Cedent/NSV under type ClaimsDeepfield/Cedent)
        // would then write BEFORE the type node and be refused "NodeType … is not registered"
        // on a fresh mesh. The same bucket previously also matched via the package ROOT when
        // the root node carries NodeTypeDefinition CONTENT on a Space root (UWDeepfield) —
        // its path prefixes the whole package, pulling every instance ahead of the types.
        // The root is therefore classified FIRST (stage-0 territory), and only Source/Test
        // children order ahead of their type; other descendants (instances, docs, Release
        // satellites) land in stage 2, after the types' visibility barrier.
        int Order(MeshNode n)
        {
            if (n.Path.Split('/').Any(seg => seg.StartsWith('_')))
                return 4;                                        // satellites after their owners
            if (!n.Path.Contains('/', StringComparison.Ordinal))
                return 2;                                        // the root (written in stage 0/2)
            if (n.Content is NodeTypeDefinition)
                return 1;                                        // the types (after their Source)
            if (nodeTypePaths.Any(t =>
                    n.Path.StartsWith(t + "/Source/", StringComparison.Ordinal)
                    || n.Path.StartsWith(t + "/Test/", StringComparison.Ordinal)))
                return 0;                                        // a type's compile inputs
            return 3;                                            // plain content + typed instances
        }

        // Three stages, same ordering guarantees as ever — but the writes are now ROUTED (#815):
        //
        // • A NEW non-root, non-satellite node takes the BULK path: one transactional
        //   IStorageAdapter.WriteMany per ordering bucket (Postgres: one NpgsqlBatch per table
        //   window; the partition-storage proxy: one WriteBatchRequest per owning hub). The
        //   response IS the visibility barrier — a committed batch needs no 100 ms Exists poll,
        //   which is what made a ~300-node course install pay minutes of serial round-trips.
        //   Per-node validation is preserved: the claimed/unchanged skip runs per node against
        //   the ONE bulk-read snapshot (ReadCurrent), and the create path's type-existence check
        //   is applied per DISTINCT type up front (ValidateBulkTypes, identical rule:
        //   static registry → in-package → persistence).
        // • The ROOT, the underscore satellites, and every node that ALREADY EXISTS keep the
        //   per-node CreateOrUpdateNodeRequest path unchanged — the root because its create runs
        //   the standard partition path (provisioning + the placeholder dance below), satellites
        //   because their guards live in the handler (AccessAssignment scoping, system-owned
        //   grant rejection, MainNode normalisation), existing nodes because updates must flow
        //   through the owning per-node hub's stream (version bump + reconciliation).
        //
        // The original two races the stage barriers solve, still solved:
        //
        // (1) THE ROOT lands first — as a Space PLACEHOLDER when its real type is dynamic and
        //     ships in this very package (the Store: the root is nodeType Store/Catalog, defined
        //     by a child). The Space create runs the standard partition path (provisioning +
        //     Admin/Partition definition) and, once persistence-visible, preempts the implicit
        //     partition bootstrap: without it, the first CHILD create triggers the heal, whose
        //     generic Space root races OUR typed root through the debounced per-node-hub
        //     persists — last persist wins (observed: the heal's Space replacing the typed root).
        // (2) THE TYPES land before any instance referencing them. Bulk-written types are
        //     COMMITTED when their batch responds, so only types updated through the per-node
        //     path (already-existing ones — which are persistence-visible by definition) still
        //     go through the Exists barrier; on a fresh mesh that set is empty and no poll runs.
        //
        // Then the FINAL root (retyping the placeholder), the plain content, and LAST the
        // underscore satellites (a satellite must anchor under an existing owner).
        var root = nodes.FirstOrDefault(n => !n.Path.Contains('/', StringComparison.Ordinal));
        var rootTypeIsStatic = root is null
            || string.IsNullOrEmpty(root.NodeType)
            || hub.ServiceProvider.FindStaticNode(root.NodeType!) is not null;
        var placeholderRoot = root is not null && !rootTypeIsStatic
            ? root with { NodeType = "Space", Content = null }
            : null;
        var stage0 = root is null ? Array.Empty<MeshNode>() : new[] { placeholderRoot ?? root };
        var stage1 = nodes.Where(n => Order(n) <= 1).OrderBy(Order).ToArray();
        var stage2 = nodes
            .Where(n => Order(n) >= 2)
            .Where(n => placeholderRoot is not null || !ReferenceEquals(n, root))
            .OrderBy(Order).ToArray();

        IObservable<System.Reactive.Unit> Visible(params string[] paths) =>
            persistence is null || paths.Length == 0
                ? Observable.Return(System.Reactive.Unit.Default)
                : paths.Select(path => Observable
                        .Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                        .SelectMany(_ => persistence.Exists(path))
                        .Where(exists => exists)
                        .FirstAsync()
                        .Timeout(TimeSpan.FromSeconds(30)))
                    .ToObservable().Concat().LastAsync().Select(_ => System.Reactive.Unit.Default);

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();

        // The per-node REQUEST path — decisions run against the bulk-read snapshot (one shared
        // read instead of N sequential probes), the write itself is the full validating
        // CreateOrUpdateNodeRequest, retry-on-error parity with UpsertIfChanged.
        IObservable<IList<(string Path, bool Wrote)>> WriteAll(
            IReadOnlyList<MeshNode> batch, IReadOnlyDictionary<string, MeshNode> current) =>
            batch.Count == 0
                ? Observable.Return((IList<(string, bool)>)new List<(string, bool)>())
                : batch.Select(n => DecideAndWrite(hub, current.GetValueOrDefault(n.Path), n, options)
                        .Catch<bool, Exception>(_ => Upsert(hub, n).Select(_ => true))
                        .Select(wrote => (n.Path, wrote)))
                    .ToObservable().Concat().ToList(); // sequential to respect the ordering

        // The BULK path for NEW nodes: one transactional WriteMany per ordering bucket. The
        // response means COMMITTED — storage acceptance is checked loudly (an install must never
        // report success for a node persistence did not take), and the create path's stamps
        // (CreatedDate/LastModified, Active state) are applied for parity. System-impersonated
        // exactly as Upsert is — per call, because ambient impersonation does not survive the
        // pipeline's scheduler hops.
        //
        // 🚨 ANNOUNCED on the mesh-change feed, per node, post-commit — via
        // WriteManyAndPublishCreated, never a bare WriteMany. Bypassing the per-node request
        // path also bypasses its MeshChangeEvent, and that event is what invalidates the
        // caches deciding whether a node is REACHABLE (PathResolutionService's resolution
        // cache, MeshNodeStreamCache, the Orleans path-cache invalidator). Without it a node
        // lands in storage and stays invisible to the running mesh: on a fresh mesh the Store
        // package's `Store/Plugin` row was written and then answered `No node found` forever
        // (2026-08-05) — probed by the already-installed Edu/Publish roots during the window
        // between the Store ROOT landing and its TYPES landing, that probe cached the miss,
        // and nothing ever invalidated it. Every plugin root typed on Store/Plugin wore the
        // missing-type overlay, no compile ever started, and only a portal restart healed it.
        IObservable<IList<(string Path, bool Wrote)>> BulkSave(IReadOnlyList<MeshNode> batch)
        {
            if (batch.Count == 0)
                return Observable.Return((IList<(string, bool)>)new List<(string, bool)>());
            if (persistence is null)
                return WriteAll(batch, ImmutableDictionary<string, MeshNode>.Empty);
            var now = DateTimeOffset.UtcNow;
            var stamped = batch
                .Select(n => n with
                {
                    State = MeshNodeState.Active,
                    CreatedDate = n.CreatedDate == default ? now : n.CreatedDate,
                    LastModified = now,
                })
                .ToArray();
            // RunAsSystem, never Observable.Using(ImpersonateAsSystem) (#1790): the scope is an
            // AsyncLocal store/restore pair and Rx can dispose it on a different thread than the
            // one that created it, latching the subscriber as `system`.
            // ImpersonationScopeThreadAffinityTest ratchets this; the rest of this file already
            // uses RunAsSystem for exactly that reason.
            return accessService.RunAsSystem(
                    () => persistence.WriteManyAndPublishCreated(stamped, options, changeFeed))
                .SelectMany(written =>
                {
                    if (written.Count != batch.Count)
                    {
                        var missing = batch.Select(n => n.Path)
                            .Except(written.Select(w => w.Path), StringComparer.Ordinal)
                            .ToArray();
                        throw new InvalidOperationException(
                            $"Bulk install of '{manifest.Id}' persisted {written.Count}/{batch.Count} "
                            + $"node(s) — storage did not accept: {string.Join(", ", missing)}");
                    }
                    return ReapplyRefused(stamped, written)
                        .Select(refusedOutcome => (IList<(string, bool)>)batch
                            .Select(n => (n.Path,
                                refusedOutcome.TryGetValue(n.Path, out var wrote) ? wrote : true))
                            .ToList());
                });
        }

        // 🚨 ACCEPTED IS NOT APPLIED — a bulk emission is not proof the node was written (#2361).
        //
        // The bulk path is chosen for nodes the install's ONE bulk read (ReadCurrent, taken before
        // the root write, the visibility barrier and the access phase) reported ABSENT, and it
        // writes them STRAIGHT to the storage adapter at the version the repo file carries — 0,
        // because a node file has no version. That classification is a snapshot from hundreds of
        // milliseconds earlier: if the path exists by the time the batch lands, version 0 is a
        // BACKWARD write, and the write-integrity chain does exactly what it must — the store's
        // version-conditional upsert refuses it, MonotonicWriteGuard merges latest-wins, and the
        // package's authored values are DROPPED. Both halves report the refusal the same way:
        // by emitting the DURABLE node instead of the one handed in (IStorageAdapter.Write's
        // contract, and the guard's "a version ABOVE the one we handed it" rule).
        //
        // The count therefore still matches, and reporting `Wrote = true` off the count alone is
        // the installer asserting a write that never happened: the partition keeps the foreign
        // content, the install logs success, and the ONLY thing that notices is the plugin gate's
        // idempotence pin on the NEXT install — where the node now exists, takes the request path,
        // mints a forward version and finally lands ("re-install of the unchanged snapshot wrote
        // 2 node(s): Skill/presentation, Skill/slide", intermittently, MeshWeaver#2361).
        //
        // So a refused node is re-decided here as exactly what it turned out to be — an EXISTING
        // node — through DecideAndWrite, the same function the request path uses, against the
        // durable row the store just handed back. That keeps all three rules the bulk
        // classification skipped on the (false) grounds that the node was new: the CLAIM fence (a
        // node the user took off the repo is still not overwritten), the unchanged-skip (a durable
        // row that already equals the authored node is not rewritten just because its version is
        // higher), and — for everything else — the validating request path, whose owning hub mints
        // a FORWARD version so the write actually lands. Never a retry of the bulk write itself:
        // repeating a version-0 write against a newer row loses again, by construction.
        //
        // The outcome per refused path is returned rather than assumed, so `Written`/`WrittenPaths`
        // report what happened instead of what was attempted.
        IObservable<IReadOnlyDictionary<string, bool>> ReapplyRefused(
            IReadOnlyList<MeshNode> requested, IReadOnlyList<MeshNode> written)
        {
            var durable = written
                .Where(n => !string.IsNullOrEmpty(n.Path))
                .GroupBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var refused = requested
                .Where(n => durable.TryGetValue(n.Path, out var durableNode)
                            && durableNode.Version > n.Version)
                .ToArray();
            if (refused.Length == 0)
                return Observable.Return(
                    (IReadOnlyDictionary<string, bool>)ImmutableDictionary<string, bool>.Empty);

            logger?.LogWarning(
                "[PackageInstaller] {Package}: storage REFUSED the bulk write of {Count} node(s) — a "
                + "durable row newer than the version the package file carries already existed, so the "
                + "authored content was dropped. Re-deciding them as existing nodes through the "
                + "validating request path: {Paths}",
                manifest.Id, refused.Length, string.Join(", ", refused.Select(n => n.Path)));

            return refused
                .Select(n => DecideAndWrite(hub, durable[n.Path], n, options)
                    .Catch<bool, Exception>(_ => Upsert(hub, n).Select(_ => true))
                    .Select(wrote => (n.Path, wrote)))
                .ToObservable().Concat().ToList()
                .Select(outcomes => (IReadOnlyDictionary<string, bool>)outcomes
                    .ToImmutableDictionary(o => o.Path, o => o.wrote, StringComparer.Ordinal));
        }

        // The same per-node type-existence rule the create path applies (MeshExtensions, step 3:
        // static registry → persistence), evaluated once per DISTINCT type instead of once per
        // node — with the types THIS package installs satisfied by construction (the bulk write
        // of the types commits before any instance batch is sent).
        IObservable<System.Reactive.Unit> ValidateBulkTypes(IReadOnlyList<MeshNode> bulk)
        {
            var inPackage = nodeTypePaths
                .Concat(root?.Content is NodeTypeDefinition ? new[] { root.Path } : Array.Empty<string>())
                .ToImmutableHashSet(StringComparer.Ordinal);
            var unknown = bulk
                .Select(n => n.NodeType)
                .Where(t => !string.IsNullOrEmpty(t))
                .Select(t => t!)
                .Distinct(StringComparer.Ordinal)
                .Where(t => !inPackage.Contains(t)
                    && hub.ServiceProvider.FindStaticNode(t) is null)
                .ToArray();
            if (unknown.Length == 0 || persistence is null)
                return Observable.Return(System.Reactive.Unit.Default);
            return unknown
                .Select(t => persistence.Exists(t).Take(1).Select(exists => (Type: t, Exists: exists)))
                .ToObservable().Merge().ToList()
                .SelectMany(results =>
                {
                    var missing = results.Where(r => !r.Exists).Select(r => r.Type).ToArray();
                    return missing.Length == 0
                        ? Observable.Return(System.Reactive.Unit.Default)
                        : Observable.Throw<System.Reactive.Unit>(new InvalidOperationException(
                            $"Install of '{manifest.Id}' failed: NodeType(s) not registered: "
                            + string.Join(", ", missing)));
                });
        }

        // 🚨 CONFIRM THE SELF-TYPED ROOT'S RETYPE RECONCILED before the install reports success.
        // Stage 2 retypes the Space placeholder to the in-package type via
        // GetMeshNodeStream(root).Update. That write is UpdateRemote: it returns the OPTIMISTIC
        // snapshot the instant the patch is ACCEPTED and does NOT wait for the owner's reconciled
        // state to echo back onto the shared IMeshNodeStreamCache handle (its own contract —
        // "callers needing the reconciled state follow the shared GetMeshNodeStream(path) handle").
        // So without this the install completed while that shared handle still replayed the Space
        // PLACEHOLDER: a reader immediately after install (the GUI, the SelfTypedRootInstallTest pin)
        // read NodeType "Space" instead of the in-package type — the intermittent flake under CI
        // load, where the owner's async fan-out lags the install's optimistic completion. FOLLOW the
        // shared handle until it carries the real type, so the install's completion is a happens-
        // before for every reader of that SAME handle. Reactive and bounded by the retype LANDING
        // (it is in flight and always settles); the Timeout is the graceful sink for a wedged owner,
        // never a fixed sleep that would cache the fallback.
        // 🚨 Explicitly SYSTEM-scoped: the shared-handle read runs through MeshNodeStreamCache's
        // per-user read gate, and the freshly-installed partition is System-owned with NO user
        // grants — an ambient USER identity at this subscription (the catalog click's, before it
        // ran installs as System; any future caller's) turns the reconciliation into
        // "lacks Read permission on '{root}'" and fails the whole install (education CI,
        // 2026-08-05, first image carrying #817). Provisioning reads its own outcome as System,
        // never as whoever happened to trigger it.
        IObservable<System.Reactive.Unit> RootRetypeReconciled() =>
            placeholderRoot is null || root is null || string.IsNullOrEmpty(root.NodeType)
                ? Observable.Return(System.Reactive.Unit.Default)
                // REQUIRED, never optional: falling back to the ambient identity on a missing
                // AccessService would re-open the exact "lacks Read on '{root}'" hole this scope
                // exists to close.
                : Observable.Using(
                        () => hub.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem(),
                        _ => hub.GetMeshNodeStream(root.Path))
                    .Where(n => n is not null
                        && string.Equals(n.NodeType, root.NodeType, StringComparison.Ordinal))
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(30))
                    .Select(_ => System.Reactive.Unit.Default);

        // 🚨 AND the retype must be PERSISTED, not just reconciled on the stream: the owning
        // hub's persist is DEBOUNCED, and the decisions a LATER install makes — PlaceholderNeeded
        // and UpsertIfChanged — read the STORAGE ADAPTER, not the stream. Completing the install
        // on the stream echo alone leaves a window where a re-install still reads the Space
        // placeholder from persistence, re-runs the placeholder dance, and rewrites the root:
        // "re-install of the unchanged snapshot wrote 1 node(s)" — the FLAPPING idempotence
        // failure in the plugin gate (identical packages pass/fail per run purely on whether the
        // debounced persist flushed in time). Same bounded-poll shape as Visible(); the Timeout
        // is the graceful sink for a wedged persist, never a fixed sleep.
        IObservable<System.Reactive.Unit> RootRetypePersisted() =>
            placeholderRoot is null || root is null || persistence is null
                || string.IsNullOrEmpty(root.NodeType)
                ? Observable.Return(System.Reactive.Unit.Default)
                : Observable
                    .Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                    .SelectMany(_ => persistence.Read(root.Path, options))
                    .Where(n => n is not null
                        && string.Equals(n.NodeType, root.NodeType, StringComparison.Ordinal))
                    .FirstAsync()
                    .Timeout(TimeSpan.FromSeconds(30))
                    .Select(_ => System.Reactive.Unit.Default);

        // Eager provisioning must also cover the package's OWN partition: with a dynamic root
        // type the placeholder covers it, but belt-and-braces keeps the fresh-mesh pin honest.
        // 🚨 The Space placeholder is for a FRESH mesh only — it lets the root exist before the
        // package's own root type has compiled. On a RE-install the root already carries its final
        // type, and the dance is pure damage: the placeholder differs from the live root BY
        // CONSTRUCTION, so stage 0 rewrote it and stage 2 retyped it back — one guaranteed churn
        // write per package per sync (the idempotence pin's "wrote 1 node" on EVERY repo the day
        // the plugins gate was first executed, 2026-07-29), plus a window in which a LIVE plugin
        // root is a contentless Space. Decide off the SAME authoritative bulk-read snapshot the
        // routing uses (ReadCurrent — a read failure yields the empty snapshot, so a fresh mesh
        // still gets its placeholder). When the root is already final, stage 0 writes NOTHING —
        // the root's (idempotent) content upsert happens in stage 2 like any other node.
        bool PlaceholderNeeded(IReadOnlyDictionary<string, MeshNode> current) =>
            placeholderRoot is not null && root is not null
                && (!current.TryGetValue(root.Path, out var curRoot)
                    || !string.Equals(curRoot.NodeType, root.NodeType, StringComparison.Ordinal));

        return EnsureInstallPartitions(hub, manifest.TargetPartition ?? manifest.Id, logger)
            .SelectMany(_ => ReadCurrent(persistence, nodes.Select(n => n.Path).ToArray(), options))
            .SelectMany(maybeCurrent =>
            {
                // Route: NEW non-satellite, non-root nodes take the bulk path per ordering
                // bucket; the root, satellites and existing nodes keep the request path.
                // A FAILED bulk read (maybeCurrent == null) means existence is UNKNOWN — bulk
                // routing is disabled entirely and every node takes the request path, whose
                // handler decides create-vs-update against its own authoritative read (the same
                // per-node write-on-failure the pre-bulk installer had).
                var current = maybeCurrent ?? ImmutableDictionary<string, MeshNode>.Empty;
                bool IsBulk(MeshNode n) => maybeCurrent is not null && !current.ContainsKey(n.Path);
                var bulkSources = stage1.Where(n => Order(n) == 0 && IsBulk(n)).ToArray();
                var bulkTypes = stage1.Where(n => Order(n) == 1 && IsBulk(n)).ToArray();
                var requestStage1 = stage1.Where(n => !IsBulk(n)).ToArray(); // keeps Order sort
                var stage2Root = stage2.Where(n => Order(n) == 2).ToArray();
                var bulkInstances = stage2.Where(n => Order(n) == 3 && IsBulk(n)).ToArray();
                var requestInstances = stage2.Where(n => Order(n) == 3 && !IsBulk(n)).ToArray();
                // The package's OWN root access satellites are HOISTED out of the satellite stage
                // and written in the publication phase below, immediately before
                // EnsureDeclaredAccess — so create-only still means "the package's shipped shape
                // wins" now that the phase runs ahead of the content (#1758).
                var partitionPath = manifest.TargetPartition ?? manifest.Id;
                var hoistedAccess = stage2
                    .Where(n => Order(n) == 4 && IsPartitionAccessSatellite(n.Path, partitionPath))
                    .ToArray();
                var satellites = stage2
                    .Where(n => Order(n) == 4 && !IsPartitionAccessSatellite(n.Path, partitionPath))
                    .ToArray();
                // Only types updated through the debounced per-node path still need the Exists
                // barrier; a bulk-written type is committed when its batch responds. (These paths
                // already exist, so the barrier passes on its first probe — no 100 ms tail.)
                var requestTypePaths = requestStage1
                    .Where(n => n.Content is NodeTypeDefinition).Select(n => n.Path).ToArray();
                var allBulk = bulkSources.Concat(bulkTypes).Concat(bulkInstances).ToArray();

                return ValidateBulkTypes(allBulk)
                    .SelectMany(_ => WriteAll(
                        PlaceholderNeeded(current) || placeholderRoot is null
                            ? stage0
                            : Array.Empty<MeshNode>(),
                        current))
                    .SelectMany(rootWrites => Visible(root is null ? [] : [root.Path])
                        // 🚨 THE PUBLICATION IS A PHASE, AND IT IS THIS ONE (#1758). It lands the
                        // instant the root exists and BEFORE a single content node does, so
                        // "installed" can never be observable before "readable".
                        //
                        // It used to run at the very END of the install — after every content node,
                        // every type, the retype reconcile and the persisted poll. But a package
                        // becomes REACHABLE the moment its root lands in stage 0 above: the path
                        // resolves, readers arrive, and the paywall landing ({plugin}/Subscribe)
                        // correctly DENIES because the grants that make a cover public are simply
                        // not there yet. Measured on a fresh mesh: bursts of denials 12–17 s before
                        // the partition's access shape was written (Store 17 s, Edu 12 s,
                        // AgenticOffice 12 s) and ZERO denials after it. The permission fold was
                        // innocent throughout — this is pure sequencing.
                        //
                        // Placed AFTER the root's own write and its visibility barrier, never
                        // before: an access satellite is the partition's first CHILD create, and a
                        // child create on a partition whose root is not yet persistence-visible
                        // triggers the implicit partition bootstrap, whose generic Space root races
                        // ours (see the stage-0 note above). Root, then access, then everything
                        // else — which collapses the window from the whole content install to the
                        // two or three access writes themselves.
                        //
                        // The paths this install is ABOUT to write are handed to EnsureDeclaredAccess,
                        // so a scoped package's per-child DENIES are established BEFORE the children
                        // they gate exist. That is strictly narrower than the old placement, where
                        // every child sat ungated for the whole install; nothing here widens access.
                        .SelectMany(_ => WriteAll(hoistedAccess, current))
                        .SelectMany(accessWrites => EnsureDeclaredAccess(
                                hub, manifest, partitionPath, logger, nodes.Select(n => n.Path))
                            .Select(_ => accessWrites))
                        .SelectMany(accessWrites => BulkSave(bulkSources)
                            .Select(sourceWrites => (IList<(string Path, bool Wrote)>)accessWrites
                                .Concat(sourceWrites).ToList()))
                        .SelectMany(sourceWrites => BulkSave(bulkTypes)
                            .SelectMany(typeBulkWrites => WriteAll(requestStage1, current)
                                .Select(typeReqWrites => (IList<(string Path, bool Wrote)>)sourceWrites
                                    .Concat(typeBulkWrites).Concat(typeReqWrites).ToList()))
                            .SelectMany(typeWrites => Visible(requestTypePaths)
                                .SelectMany(_ => WriteAll(stage2Root, current))
                                .SelectMany(rootFinalWrites => BulkSave(bulkInstances)
                                    .SelectMany(instBulkWrites => WriteAll(requestInstances, current)
                                        .SelectMany(instReqWrites => WriteAll(satellites, current)
                                            .Select(satWrites => (IList<(string Path, bool Wrote)>)rootFinalWrites
                                                .Concat(instBulkWrites).Concat(instReqWrites)
                                                .Concat(satWrites).ToList()))))
                    // The retype's optimistic emit is not the reconciled state — wait for the
                    // shared root handle to carry the in-package type AND for the debounced
                    // persist to land it in storage (a later install reads persistence).
                    .SelectMany(rest => RootRetypeReconciled()
                        .SelectMany(_ => RootRetypePersisted())
                        .Select(_ => rest))
                    // 🚨 RECYCLE the retyped root's hub. It was ACTIVATED as the Space placeholder
                    // (RootRetypeReconciled reads the stream, and readers race the install anyway),
                    // so the live hub instance still carries the placeholder's configuration — the
                    // default areas, none of the package type's. Nothing re-activates it: the node's
                    // stored type changed but the hub does not watch its own NodeType. The symptom
                    // is a freshly installed package whose ROOT renders without its type's areas
                    // ("No renderer is registered for area Tests on hub Store" — the plugin gate's
                    // Store/Catalog RED, 2026-07-29; same family as the freshly-provisioned-Store-
                    // is-invisible incident) until someone manually recycles it. Dispose is the
                    // recycle idiom (RecycleLayoutArea): fire-and-forget, next access re-activates
                    // with the final type. Only when the placeholder dance actually ran.
                    //
                    // 🚨 This recycle is NECESSARY but was not SUFFICIENT: the identical symptom
                    // recurred 2026-08-09 because PathResolutionService PINNED a fabricated
                    // partition-root placeholder (no NodeType → the root's hub binds the mesh
                    // DEFAULT configuration), so every re-activation after this Dispose re-read
                    // the same fabrication. Partition provisioning runs BEFORE the root write, so
                    // any routed touch inside that window fabricated it. Fixed at the source —
                    // synthesized resolutions are never cached, and a fill that lands after its
                    // own invalidation is discarded (PathResolutionCachePoisonTest).
                    //
                    // 🚨 …and it recurred AGAIN on a build carrying that fix (#1104) — plugin-gate
                    // run 31361446933, whose available-areas list was EXACTLY
                    // ConfigureDefaultNodeHub's set (AddDefaultLayoutAreas + Invite) and not one
                    // area of the node's real type. That list IS the fingerprint: it says the hub
                    // bound the mesh DEFAULT configuration, not a stale type and not the Space
                    // placeholder, which is what distinguishes this defect from a compile failure.
                    // It recurred because fixing RESOLUTION cannot help a hub a bad resolution has
                    // ALREADY activated: GetHostedHub pins by address and the hub never re-reads
                    // its NodeType. That is why this Post is no longer where the guarantee lives.
                    // It is fire-and-forget, conditional on the placeholder dance having run, and
                    // available to nobody but this installer — while ANY writer can retype a node.
                    // The framework now un-pins on its own: every activation arms
                    // NodeTypeRebindWatcher, which recycles the hub the first time the mesh change
                    // feed reports a different NodeType for its path. This Post stays as the fast
                    // path (it recycles immediately rather than on the feed hop) and as the marker
                    // of intent; it is not load-bearing. If the symptom ever reappears, check all
                    // three: is the hub recycled, is the resolution for the bare root path serving
                    // a real node, and did the rebind watcher see the retype?
                    //
                    // 🚨 …and it no longer happens HERE. A fire-and-forget teardown posted at write
                    // time outlives the install and is a race handed to whoever touches the root
                    // next — the installer's own content publish, a client's SubscribeRequest,
                    // anything — which lands mid-teardown and gets HubDisposingException: no bytes
                    // written, or an area that never renders, plus a caller left waiting on a reply
                    // that died with the hub. Both halves of StaleStampRootBindingTest are exactly
                    // that (red on main from 2026-08-10): "cannot create '/content/'" for the
                    // package that ships assets, "cannot create 'LayoutAreaReference { Area =
                    // Tests }'" for the one that does not.
                    //
                    // It is also MISTIMED here: at this point the in-package type still advertises
                    // the compile stamp the repo COMMITTED, so the re-activation this recycle
                    // invites would bind the fallback for the new hub's lifetime — the very thing
                    // #1101 stopped the warm from doing. The recycle now runs once that type has a
                    // build an instance can load, and WAITS for the root to come back. See
                    // SettleRetypedRoot below.
                    .Select(rest => rest)
                    // A placeholder's write is bookkeeping, not content — its FINAL retype in
                    // stage 2 is the root's one counted write (keeps Written ≤ node count).
                    .Select(rest => (IList<(string Path, bool Wrote)>)(placeholderRoot is null ? rootWrites : [])
                        .Concat(typeWrites).Concat(rest).ToList())))
            .SelectMany(writes =>
            {
                var written = writes.Where(w => w.Wrote).Select(w => w.Path).ToImmutableList();
                var result = new InstallResult(nodes.Length, written.Count)
                {
                    WrittenPaths = written,
                };
                logger?.LogInformation(
                    "Installed node-repo plugin {Id}: {Written} written, {Unchanged} unchanged ({Count} node(s)) @ {Ref}",
                    manifest.Id, result.Written, result.Unchanged, nodes.Length, installedFromRef);

                // Recompile only the NodeTypes, and only when something changed — in TWO ORDERED
                // WAVES around the root's recycle. See ReleaseWaves for why the split exists and
                // why the root's own type has to be in the first one (#1732).
                var retypedRoot = placeholderRoot is not null ? root?.Path : null;
                var (firstWave, deferredWave) = ReleaseWaves(retypedRoot, nodeTypePaths, nodes);
                var releases = result.Written > 0
                    ? SeedPrebuiltAssemblies(hub, nodeTypePaths, logger)
                    : Observable.Return(0);

                // The declared access was published as a PHASE before the first content node
                // landed (see the note at that call site, #1758) — there is deliberately no second
                // write pass here, so nothing can re-derive a shape from nodes it has itself just
                // written. What stays is the phase's POST-CONDITION, read once and reported LOUDLY:
                // an install that reports success while its partition is unreadable is the failure
                // this whole ordering exists to make impossible, and it must never be silent.
                return VerifyDeclaredAccess(hub, manifest, manifest.TargetPartition ?? manifest.Id, logger)
                    // 🚨 Prune nodes THIS package no longer ships — read BEFORE WriteInstalledRecord
                    // overwrites the baseline below, so a node the repo retired is deleted rather
                    // than surviving forever (Systemorph/MeshWeaver#2473). A full install runs here
                    // whenever the delta path refuses an update (CatalogLayoutAreas.IncrementalUpdate,
                    // "changed shared Source/Test files; full install required") as well as on a
                    // fresh install — the exact route by which a retired NodeType with its Source
                    // deleted kept serving its last-built assembly until the framework identity
                    // moved, then recompiled against zero sources and parked the portal's readiness.
                    .SelectMany(_ => PruneRetiredNodes(
                        hub, manifest, moduleManifest, nodes, persistence, meshService, options, logger))
                    .SelectMany(_ => WriteInstalledRecord(
                        hub, manifest, installedFromRef, nodes.Length, moduleManifest, authorizingUserId))
                    // Adoption first (it can settle a release without compiling at all), then the
                    // FIRST wave: the root's own in-package NodeType, whose rebuild is precisely
                    // what SettleRetypedRoot waits for.
                    .SelectMany(_ => releases)
                    .SelectMany(_ => result.Written > 0
                        ? RequestReleases(hub, firstWave, logger)
                        : Observable.Return(System.Reactive.Unit.Default))
                    // The retyped root's recycle, moved here from the write stage and made ORDERED
                    // (see the note there). Only now can it do its job: the in-package type has had
                    // its rebuild, so the hub that comes back binds the package's own configuration
                    // instead of the fallback — and the install no longer returns while a teardown
                    // it started is still running.
                    .SelectMany(_ => SettleRetypedRoot(hub, retypedRoot, nodes, logger))
                    // …and ONLY NOW the rest of the package's types. Their compiles read the root
                    // (ValidateCellSurfaceSingleHome → GetMeshNode('<packageRoot>') for every
                    // `shared=` consumer), so launching them before the recycle above pointed a
                    // whole package's compiles at a hub this very method was about to dispose
                    // (#1732).
                    .SelectMany(_ => result.Written > 0
                        ? RequestReleases(hub, deferredWave, logger)
                        : Observable.Return(System.Reactive.Unit.Default))
                    .SelectMany(_ => WarmInstalledRoots(hub, manifest, nodes, logger))
                    // …then the package's committed binaries (course videos/posters) into the
                    // warmed root's content collection — the half of "publish" that merging used
                    // to leave undone (#848).
                    .SelectMany(_ => SyncPackageContent(
                        hub, manifest.TargetPartition ?? manifest.Id,
                        manifest.SourceFolder ?? manifest.Id, files, nodes, logger))
                    .Select(_ => result);
            }));
            });
    }

    /// <summary>
    /// The FULL install's counterpart to <see cref="InstallNodeRepoDeltaCore"/>'s prune — applied
    /// without a caller-handed diff, because a full install is exactly what runs when there IS no
    /// diff to hand in: a fresh install (nothing installed before — nothing to prune, and this
    /// returns 0 immediately) and, critically, whenever <c>CatalogLayoutAreas.IncrementalUpdate</c>
    /// refuses an update because it touches the package's shared <c>Source/</c>/<c>Test/</c> and
    /// falls back here. That fallback is the route by which a node the source repo retired used to
    /// survive in the mesh FOREVER (Systemorph/MeshWeaver#2473): the full install upserts everything
    /// the package still ships and, before this method existed, pruned nothing — so a NodeType whose
    /// <c>Source/</c> the repo deleted kept serving its last-built assembly until the framework
    /// identity next moved, at which point it recompiled against zero sources, parked at
    /// <c>CompileError</c>, and held every instance hub for the full 60 s activation budget.
    ///
    /// <para>Reads THIS package's own previous install record (<c>{InstalledPartition}/{Id}</c>) for
    /// its <see cref="PackageManifest.InstalledFiles"/> baseline — read here, BEFORE the caller's
    /// <c>WriteInstalledRecord</c> overwrites it — and diffs it against <paramref name="moduleManifest"/>
    /// via the same <see cref="ModuleManifest.DiffFrom"/> the delta path uses. A removed file whose
    /// node path is still produced by a currently-shipped file (the <c>X.json</c> → <c>X/index.json</c>
    /// layout-move case) is never a prune candidate. Bounded by construction to paths THIS package's
    /// own record previously listed — never a scan of the shared partition — so it can never touch
    /// another package's content. No-ops (returns 0) when the package ships no <c>manifest.lock</c>
    /// (no file-level baseline exists at all) or has no prior record with a file map.</para>
    /// </summary>
    private static IObservable<int> PruneRetiredNodes(
        IMessageHub hub, PackageManifest manifest, ModuleManifest? moduleManifest,
        IReadOnlyList<MeshNode> nodes, IStorageAdapter? persistence, IMeshService? meshService,
        JsonSerializerOptions options, ILogger? logger)
    {
        if (moduleManifest is null || persistence is null)
            return Observable.Return(0);

        var recordPath = $"{InstalledPartition}/{manifest.Id}";
        return persistence.Read(recordPath, options).Take(1)
            .Select(n => n?.ContentAs<PackageManifest>(options)?.InstalledFiles)
            .Catch<ImmutableSortedDictionary<string, string>?, Exception>(
                _ => Observable.Return<ImmutableSortedDictionary<string, string>?>(null))
            .SelectMany(previousFiles =>
            {
                if (previousFiles is not { Count: > 0 })
                    return Observable.Return(0);

                var currentNodePaths = nodes.Select(n => n.Path)
                    .ToImmutableHashSet(StringComparer.Ordinal);
                var removedNodePaths = moduleManifest.DiffFrom(previousFiles).RemovedFiles
                    .Select(NodePathForFile)
                    .Where(p => p is not null && !currentNodePaths.Contains(p))
                    .Select(p => p!)
                    .ToImmutableHashSet(StringComparer.Ordinal);

                return PruneRemovedNodes(hub, meshService, persistence, removedNodePaths, options, logger)
                    .Do(pruned =>
                    {
                        if (pruned > 0)
                            logger?.LogInformation(
                                "[PackageInstaller] {Id}: pruned {Pruned} node(s) the repo no longer "
                                + "ships (full install).", manifest.Id, pruned);
                    });
            });
    }

    /// <summary>
    /// Deletes each of <paramref name="removedNodePaths"/>, System-impersonated per delete like
    /// every installer write. A failed/absent delete degrades to a log line — never fails the
    /// install/update it runs inside. Shared by <see cref="InstallNodeRepoDeltaCore"/>'s prune
    /// (whose removed set comes from a caller-handed manifest diff) and
    /// <see cref="PruneRetiredNodes"/>'s (whose removed set comes from the same diff computed
    /// against the package's own previous install record).
    ///
    /// <para>Read-before-delete: a CLAIMED node (<see cref="MeshNode.SyncBehavior"/> other than
    /// <see cref="SyncBehavior.Include"/>) is the user's, not the repo's — the repo dropping its
    /// file revokes the PACKAGE's copy, never the user's claim. The same fence
    /// <see cref="UpsertIfChanged"/> applies on the write side.</para>
    /// </summary>
    private static IObservable<int> PruneRemovedNodes(
        IMessageHub hub, IMeshService? meshService, IStorageAdapter? persistence,
        IReadOnlyCollection<string> removedNodePaths, JsonSerializerOptions options, ILogger? logger)
    {
        if (meshService is null || removedNodePaths.Count == 0)
            return Observable.Return(0);
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return removedNodePaths
            .Select(path => (persistence is not null
                    ? persistence.Read(path, options).Take(1)
                    : Observable.Return<MeshNode?>(null))
                .SelectMany(current =>
                    current is not null && current.SyncBehavior != SyncBehavior.Include
                        ? Observable.Return(0)
                        // Sealed at Subscribe (RunAsSystem), never Observable.Using(ImpersonateAsSystem):
                        // impersonation is an AsyncLocal store/restore pair, and Using disposes on the
                        // TERMINATING thread — for a cross-hub delete, the owning hub's response thread,
                        // not the one that opened the scope — which latches system-security onto whatever
                        // runs next on the subscribing thread (#1790).
                        : accessService.RunAsSystem(() => meshService.DeleteNode(path))
                            .Take(1)
                            .Select(deleted => deleted ? 1 : 0))
                .Catch<int, Exception>(ex =>
                {
                    logger?.LogWarning(ex, "Pruning removed node {Path} failed.", path);
                    return Observable.Return(0);
                }))
            .ToObservable().Concat().Sum();
    }

    /// <summary>
    /// Applies a MANIFEST-DIFF update to an already-installed node-repo plugin: upserts only the
    /// <paramref name="changedFiles"/> (same landing order and unchanged-skip as the full install),
    /// prunes the <paramref name="removedNodePaths"/> (derived from the diff's removed files,
    /// restricted by the caller to previously-installed paths), requests a recompile only for the
    /// NodeTypes actually affected, and re-stamps the install record with
    /// <paramref name="newManifest"/> as the next diff baseline. A delta presupposes a prior
    /// install, so no fresh-mesh placeholder dance — the root and existing types are already
    /// present; a visibility barrier still guards instances of a type ADDED by this very delta.
    ///
    /// <para>Carries the same commercial gate as <see cref="Install"/> (#830): an update IS an
    /// install of new content, and the unattended path reaches it directly.</para>
    /// </summary>
    /// <param name="hub">The installing hub.</param>
    /// <param name="manifest">The catalog manifest of the package being updated.</param>
    /// <param name="newManifest">The module manifest at the candidate ref — the next diff baseline.</param>
    /// <param name="changedFiles">The files the diff named as added/changed.</param>
    /// <param name="removedNodePaths">Previously-installed node paths the diff removed.</param>
    /// <param name="installedFromRef">The git ref the files were read at.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="authorizingUserId">The principal that AUTHORIZED this update — for an
    /// unattended update, the install record's <see cref="PackageManifest.AuthorizedBy"/>. Only
    /// consulted for a commercial package. See <see cref="PackageEntitlement"/>.</param>
    /// <returns>A cold observable of the update outcome; Subscribe to run.</returns>
    public static IObservable<InstallResult> InstallNodeRepoDelta(
        IMessageHub hub,
        PackageManifest manifest,
        ModuleManifest newManifest,
        IReadOnlyList<PackageFile> changedFiles,
        IReadOnlyCollection<string> removedNodePaths,
        string installedFromRef,
        ILogger? logger = null,
        string? authorizingUserId = null)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.PluginCatalog.PackageInstaller");

        var effectiveLogger = logger;
        return PackageEntitlement.Authorize(hub, manifest, authorizingUserId, effectiveLogger)
            .SelectMany(_ => LicenseAcceptanceGate.Require(hub, manifest, authorizingUserId, effectiveLogger))
            .SelectMany(_ => InstallNodeRepoDeltaCore(
                hub, manifest, newManifest, changedFiles, removedNodePaths, installedFromRef,
                effectiveLogger, authorizingUserId));
    }

    private static IObservable<InstallResult> InstallNodeRepoDeltaCore(
        IMessageHub hub,
        PackageManifest manifest,
        ModuleManifest newManifest,
        IReadOnlyList<PackageFile> changedFiles,
        IReadOnlyCollection<string> removedNodePaths,
        string installedFromRef,
        ILogger? logger,
        string? authorizingUserId)
    {

        var parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions, hub.ServiceProvider.GetServices<IFileFormatParser>());
        var nodes = ParseAll(parsers, changedFiles, manifest.Id, logger, hub.JsonSerializerOptions);

        if (RefuseIfStaticShadowed(hub, manifest, nodes, logger) is { } shadowed)
            return shadowed;

        var options = hub.JsonSerializerOptions;
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        var nodeTypePaths = nodes.Where(n => n.Content is NodeTypeDefinition).Select(n => n.Path).ToArray();

        // Same landing order as the full install: a changed type's compile inputs before the type,
        // types before instances, satellites last.
        int Order(MeshNode n)
        {
            if (n.Path.Split('/').Any(seg => seg.StartsWith('_')))
                return 4;
            if (n.Content is NodeTypeDefinition)
                return 1;
            if (OwningTypePath(n.Path) is not null)
                return 0;
            return 3;
        }

        var head = nodes.Where(n => Order(n) <= 1).OrderBy(Order).ToArray();
        var tail = nodes.Where(n => Order(n) >= 2).OrderBy(Order).ToArray();

        IObservable<System.Reactive.Unit> TypesVisible() =>
            persistence is null || nodeTypePaths.Length == 0
                ? Observable.Return(System.Reactive.Unit.Default)
                : nodeTypePaths.Select(path => Observable
                        .Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                        .SelectMany(_ => persistence.Exists(path))
                        .Where(exists => exists)
                        .FirstAsync()
                        .Timeout(TimeSpan.FromSeconds(30)))
                    .ToObservable().Concat().LastAsync().Select(_ => System.Reactive.Unit.Default);

        IObservable<IList<bool>> WriteAll(IReadOnlyList<MeshNode> batch) =>
            batch.Count == 0
                ? Observable.Return((IList<bool>)new List<bool>())
                : batch.Select(n => UpsertIfChanged(hub, persistence, n, options))
                    .ToObservable().Concat().ToList();

        // Prune the removed nodes — System-impersonated per delete, like every installer write.
        // A failed/absent delete degrades to a log line, never fails the update.
        //
        // Read-before-delete: a CLAIMED node (non-Include SyncBehavior) is the user's, not the
        // repo's — the repo dropping its file revokes the PACKAGE's copy, never the user's claim.
        // The same fence UpsertIfChanged applies on the write side, and part of what makes
        // unattended (opted-in) auto-update safe. Only ever narrows the existing prune set:
        // removedNodePaths is already restricted to previously-installed paths, so a user-ADDED
        // node was never a prune candidate to begin with.
        IObservable<int> Prune() =>
            PruneRemovedNodes(hub, meshService, persistence, removedNodePaths, options, logger);

        // 🚨 The declared access is re-asserted BEFORE the delta's writes, not after them (#1758).
        // An UPDATE re-asserts it at all so a package that only just flipped its declaration (or
        // whose policy was lost) converges on the next sync rather than waiting for a full
        // re-install; running it FIRST means the re-asserted shape is in place before the nodes it
        // governs land. A delta presupposes a prior install, so the partition root already exists
        // and there is no bootstrap race to order around. Create-only, so this is free on the
        // common path.
        return EnsureInstallPartitions(hub, manifest.TargetPartition ?? manifest.Id, logger)
            .SelectMany(_ => EnsureDeclaredAccess(hub, manifest, manifest.TargetPartition ?? manifest.Id,
                logger, nodes.Select(n => n.Path)))
            .SelectMany(_ => WriteAll(head))
            .SelectMany(headWrites => TypesVisible()
                .SelectMany(_ => WriteAll(tail))
                .Select(tailWrites => (IList<bool>)headWrites.Concat(tailWrites).ToList()))
            .SelectMany(writes => Prune().Select(pruned => (Writes: writes, Pruned: pruned)))
            .SelectMany(t =>
            {
                var written = head.Concat(tail)
                    .Zip(t.Writes, (node, wrote) => (node, wrote))
                    .Where(x => x.wrote)
                    .Select(x => x.node)
                    .ToArray();
                var result = new InstallResult(nodes.Length, written.Length);

                // Recompile exactly what the delta touched: a written NodeType node, and the OWNER
                // of any written Source/Test node whose type node itself did not change. A pruned
                // source's owner recompiles too (stale code must leave the assembly).
                var releaseTargets = written
                    .Select(n => n.Content is NodeTypeDefinition ? n.Path : OwningTypePath(n.Path))
                    .Concat(removedNodePaths.Select(OwningTypePath))
                    .Where(p => p is not null).Select(p => p!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                logger?.LogInformation(
                    "Updated node-repo plugin {Id} incrementally: {Written} written, {Unchanged} unchanged, " +
                    "{Pruned} pruned, {Releases} recompile(s) @ {Ref} (module {ModuleVersion})",
                    manifest.Id, result.Written, result.Unchanged, t.Pruned,
                    releaseTargets.Length, installedFromRef, newManifest.ModuleVersion);

                // SEQUENCED, never fire-and-forget (the observable is cold — a bare call requests
                // nothing). A delta runs no placeholder dance, so there is no root recycle to split
                // the waves around; one wave is the whole set.
                var releases = releaseTargets.Length > 0
                    ? SeedThenRequestReleases(hub, releaseTargets, logger)
                    : Observable.Return(System.Reactive.Unit.Default);
                // The declared access was re-asserted BEFORE the writes (see the note at that call
                // site, #1758). What stays here is its POST-CONDITION — a read, never a second
                // write pass, so nothing can re-derive a shape from nodes it just wrote.
                return releases
                    .SelectMany(_ => VerifyDeclaredAccess(
                        hub, manifest, manifest.TargetPartition ?? manifest.Id, logger))
                    .SelectMany(_ => WriteInstalledRecord(hub, manifest, installedFromRef,
                        newManifest.Files.Count, newManifest, authorizingUserId))
                    .SelectMany(_ => WarmInstalledRoots(hub, manifest, nodes, logger))
                    // A changed BINARY is a changed file like any other: manifest.lock hashes the
                    // `content/**` assets too, so a re-cut video is in `changedFiles` and an
                    // unchanged one never travels. This is what keeps the incremental path cheap
                    // even for a course carrying tens of MB of video (#848).
                    .SelectMany(_ => SyncPackageContent(
                        hub, manifest.TargetPartition ?? manifest.Id,
                        manifest.SourceFolder ?? manifest.Id, changedFiles, nodes, logger))
                    .Select(_ => result);
            });
    }

    // The NodeType that owns a compile-input node (…/Source/* or …/Test/*), or null when the path
    // is no compile input. The prefix before /Source|/Test is the type's path by the node-repo
    // layout; for a partition-shared Source the prefix is the partition ROOT (only a type when its
    // content is a NodeTypeDefinition — a failed release request on a non-type root just logs).
    private static string? OwningTypePath(string nodePath)
    {
        var i = nodePath.IndexOf("/Source/", StringComparison.Ordinal);
        if (i < 0)
            i = nodePath.IndexOf("/Test/", StringComparison.Ordinal);
        return i > 0 ? nodePath[..i] : null;
    }

    // Parses a node-per-file file into a MeshNode at its CANONICAL path (no partition rebase) — the
    // file's repo-relative path IS the node's path. The export's top-level README.md is a GitHub
    // display file, never a node (mirrors GitHubSyncService.ParseFile, minus the space rebase).
    /// <summary>
    /// Parses every file of an install and reports the UNPARSEABLE ones as ONE aggregate line.
    ///
    /// <para>🚨 #1767: a per-file "skipped" warning is not a signal. `PensionFund` shipped 72
    /// BOM'd files, 62 of them were skipped, the package gated ZERO NodeTypes — and the install
    /// reported success for years, because the only evidence was 62 lines in a log nobody reads.
    /// "I installed nothing" and "I installed everything" must never look alike at the level where
    /// the verdict is read.</para>
    ///
    /// <para>Loud and counted, NOT fatal: the runtime itself skips unmaterialisable files and
    /// installs the rest, so refusing here would resolve a SMALLER tree than the mesh does — the
    /// equivalence break #1763 exists to prevent, in the opposite direction. Files that are not
    /// nodes by design (README, manifest, `content/**` assets) are not skips and are not counted.
    /// </para>
    /// </summary>
    private static MeshNode[] ParseAll(
        FileFormatParserRegistry parsers, IReadOnlyList<PackageFile> files, string packageId,
        ILogger? logger, JsonSerializerOptions? options = null)
    {
        var unparsed = new List<string>();
        var nodes = files
            .Select(f => ParseCanonical(parsers, f, logger, options, unparsed))
            .Where(n => n is not null).Select(n => n!)
            .ToArray();

        if (unparsed.Count > 0)
            logger?.LogWarning(
                "Package '{Package}': {Skipped} of {Candidates} candidate files had no parser and "
                + "were skipped; {Installed} nodes installed. First skipped: {Sample}.",
                packageId, unparsed.Count, files.Count(f => !IsNotANodeFile(f.RelativePath)),
                nodes.Length, string.Join(", ", unparsed.Take(5)));

        return nodes;
    }

    /// <summary>
    /// Files that are NOT nodes by design, and so are neither parsed nor counted as skips.
    ///
    /// <para>The README is a GitHub display file. The manifest is the install record's baseline.
    /// A <c>{package}/content/**</c> asset is not a node — its bytes go to the partition root's
    /// content collection (SyncPackageContent), which is where the served
    /// <c>/api/content/{root}/content/…</c> URL resolves. (It used to be
    /// <c>/static/{root}/content/…</c>; #587 unmounted content from /static entirely, so that shape
    /// is now 404 for everyone — an authored asset URL must name the access-controlled route.) Same
    /// split GitHubSyncService.ParseSnapshot makes, and the one NodePathForFile asserts. Excluding
    /// them also silences the "No parser for …/videos/x.mp4" warning every course emitted per
    /// install.</para>
    ///
    /// <para>ONE predicate, used by both the parse loop and the aggregate line's denominator —
    /// "3 of 200 skipped" reads very differently when 150 of the 200 were never node candidates
    /// (Copilot review, #1781). Two copies of this list would drift, and the drift would show up as
    /// a quietly wrong count rather than as a failure.</para>
    /// </summary>
    private static bool IsNotANodeFile(string relativePath) =>
        string.Equals(relativePath, "README.md", StringComparison.OrdinalIgnoreCase)
        || ModuleManifest.IsManifestPath(relativePath)
        || ContentAssetMapper.IsContentPath(relativePath);

    private static MeshNode? ParseCanonical(
        FileFormatParserRegistry parsers, PackageFile file, ILogger? logger,
        JsonSerializerOptions? options = null, List<string>? unparsed = null)
    {
        if (IsNotANodeFile(file.RelativePath))
            return null;
        var ext = System.IO.Path.GetExtension(file.RelativePath);
        var parsed = parsers.TryParse(ext, file.RelativePath, file.Content, file.RelativePath);
        if (parsed is null)
        {
            logger?.LogWarning("No parser for node-repo file {Path}; skipped.", file.RelativePath);
            unparsed?.Add(file.RelativePath);
            return null;
        }
        var (id, ns) = NodeFileMapper.FromRelativePath(file.RelativePath);
        // 🚨 WithPath, not `with { Id/Namespace }`. MeshNode.MainNode is a STORED, non-nullable init
        // property whose default is evaluated once at construction, so a plain record copy moves the
        // computed Path and leaves MainNode naming the namespace the parser minted the node in — and
        // the install then persists that as if it were deliberate (#2939: six live Skill nodes at
        // `Hosting/Skill/x` carrying `MainNode = "Skill/x"`, Active, and invisible to every search
        // because `is:main` is SQL `n.main_node = n.path`).
        //
        // This line used to read `MainNode = parsed.MainNode ?? (…)`, which is DEAD CODE: MainNode is
        // non-nullable, so the right-hand side never ran and the parser's value was always kept. The
        // intent — preserve an AUTHORED mainNode, because an _Access grant's mainNode IS its scope
        // and the permission evaluator silently ignores a grant whose mainNode is wrong — is right
        // and is preserved; a null check simply cannot express it. MeshNode.HasExplicitMainNode is
        // the predicate built for the question, and WithPath is where it now lives so the next
        // rebase site cannot get it wrong.
        return AsAuthored(parsed, file, logger, options).WithPath(id, ns) with
        {
            State = MeshNodeState.Active,
        };
    }

    /// <summary>
    /// The node with the content the FILE declares, whenever the parse materialised a
    /// RUNTIME-COMPILED (collectible-assembly) content type.
    ///
    /// <para>🚨 An install must write what the package says, and for a dynamically-compiled content
    /// type the parse cannot know it guessed right. Such a type's <c>$type</c> discriminator is its
    /// bare CLR name, and that name is unique only inside its own package — one customer node repo
    /// ships <c>Currency</c> in four packages and eleven further names in two or more. The mesh-wide
    /// content-type map answers by name, so the deserialiser hands the installer whichever
    /// package's record compiled most recently, and materialising it is destructive both ways:
    /// members the foreign record does not declare are DROPPED (<c>Reinsurance/Samples/AlpinaCedent</c>
    /// installed with its whole content stripped to <c>{}</c>), and defaults it does declare are
    /// INJECTED (<c>Ifrs17/Currency/*</c> gained <c>code</c>/<c>decimalPlaces</c> from
    /// <c>ClaimsDeepfield</c>'s record). The unchanged-check then compares an authored file against
    /// a foreign materialisation, finds a difference, and rewrites the node on EVERY install — with
    /// the set of rewritten nodes varying run to run, because which package won the name depends on
    /// compile order (Systemorph/MeshWeaver#1299).</para>
    ///
    /// <para>The authored element is written instead, and the OWNING hub — the one place the node's
    /// own NodeType is known — types it on read. Statically-registered content
    /// (<see cref="NodeTypeDefinition"/>, markdown, access assignments) is a real, process-unique
    /// registration rather than a name guess, so it stays typed: the installer's own ordering and
    /// compile-trigger logic reads <c>Content is NodeTypeDefinition</c>.</para>
    ///
    /// <para>🚨 #2266: the re-read here MUST tolerate exactly what the PRIMARY parse tolerated —
    /// <see cref="FileFormatParserRegistry.TryParse"/> already produced <paramref name="parsed"/>
    /// from this very <paramref name="file"/>, after stripping a leading UTF-8 BOM
    /// (<see cref="FileFormatParserRegistry.WithoutBom"/>) and applying <paramref name="options"/>'s
    /// comment/trailing-comma leniency. Re-parsing the RAW, un-stripped <c>file.Content</c> under
    /// <see cref="JsonDocumentOptions"/>' strict defaults made the fallback below reachable for
    /// every BOM'd file the primary parse tolerated — <c>samples/Graph/Data/PensionFund</c> ships
    /// its Currency/Position/Year instances BOM'd, so every one of them silently installed the
    /// materialised (possibly wrong-package) value instead of the authored file, which is also what
    /// made the CHF/EUR/USD Currency nodes fail the idempotence check on a second install (#2271):
    /// the wrongly-installed materialised value is what the unchanged-skip then compared against.</para>
    /// </summary>
    // Internal for the InstallAuthoredContentTest pin (InternalsVisibleTo).
    internal static MeshNode AsAuthored(
        MeshNode parsed, PackageFile file, ILogger? logger, JsonSerializerOptions? options = null)
    {
        if (parsed.Content is null || !parsed.Content.GetType().Assembly.IsCollectible)
            return parsed;
        try
        {
            // Same three tolerances JsonFileParser.Parse copies off the hub's own options — this
            // read must never be STRICTER than the parse that already succeeded on this content.
            var documentOptions = options is null
                ? default
                : new JsonDocumentOptions
                {
                    CommentHandling = options.ReadCommentHandling,
                    AllowTrailingCommas = options.AllowTrailingCommas,
                    MaxDepth = options.MaxDepth,
                };
            using var doc = JsonDocument.Parse(
                FileFormatParserRegistry.WithoutBom(file.Content), documentOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("content", out var authored))
                return parsed with { Content = authored.Clone() };
        }
        catch (JsonException)
        {
            // Reachable only for content that is genuinely malformed in a way the primary parse's
            // OWN deserialization somehow tolerated (e.g. the "content" property itself is odd
            // enough for JsonFileParser's typed deserialize to accept but this raw re-read cannot
            // structurally locate) — fall through rather than invent a content shape.
        }
        logger?.LogWarning(
            "[PackageInstaller] {Path} materialised the runtime-compiled content type {Type}, and the "
            + "file's authored content could not be re-read — installing the materialised value, which "
            + "may carry another package's defaults.",
            file.RelativePath, parsed.Content.GetType().Name);
        return parsed;
    }

    // Each write is System-impersonated INDIVIDUALLY — an ambient whole-pipeline impersonation
    // does not survive the pipeline's scheduler hops. Observable.Using, NOT Defer+using: the
    // post happens when hub.Observe's stream is SUBSCRIBED, so the impersonation must still be
    // alive then (Defer+using disposes it before the post — the exact trap the Edu redeemer
    // documented). The admin-gated install is the authorization (see Install).
    // Off-router issuing (NodeOperationIssuingHub): the boot default-install seed calls this with
    // the DI root mesh hub, and a target-less CreateOrUpdateNodeRequest posted there EXECUTES the
    // whole bulk upsert on the router's action block (ROUTER_TRAFFIC). No-op for any other caller.
    private static IObservable<int> Upsert(IMessageHub hub, MeshNode node) =>
        Observable.Using(
                () => hub.ServiceProvider.GetService<AccessService>()?.ImpersonateAsSystem()
                      ?? System.Reactive.Disposables.Disposable.Empty,
                _ => hub.NodeOperationIssuingHub()
                    .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node)))
            .FirstAsync().Select(d => d.Message)
            .SelectMany(resp => resp.Success
                ? Observable.Return(1)
                : Observable.Throw<int>(new InvalidOperationException(
                    $"Install of '{node.Path}' failed: {resp.Error}")));

    // Parse one package file into a node rebased under the target partition (mirrors
    // GitHubSyncService.ParseFile). The package.json manifest is filtered out before this.
    private static MeshNode? ParseNode(
        FileFormatParserRegistry parsers, string partition, string sourceFolder, PackageFile file,
        ILogger? logger, JsonSerializerOptions? options = null)
    {
        var rel = FolderRelative(file.RelativePath, sourceFolder);

        // A `content/**` asset is not a node — SyncPackageContent writes its bytes into the target
        // partition root's content collection instead (see ParseCanonical).
        if (ContentAssetMapper.IsContentPath(rel))
            return null;

        var ext = System.IO.Path.GetExtension(rel);
        var parsed = parsers.TryParse(ext, rel, file.Content, rel);
        if (parsed is null)
        {
            logger?.LogWarning("No parser for package file {Path}; skipped.", file.RelativePath);
            return null;
        }

        var (id, ns) = NodeFileMapper.FromRelativePath(rel);
        var rebasedNs = string.IsNullOrEmpty(ns) ? partition : $"{partition}/{ns}";
        return AsAuthored(parsed, file, logger, options) with
        {
            Id = id,
            Namespace = rebasedNs,
            MainNode = $"{rebasedNs}/{id}",
            State = MeshNodeState.Active,
        };
    }

    private static bool IsManifest(string relativePath) =>
        relativePath.EndsWith("/package.json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(relativePath, "package.json", StringComparison.OrdinalIgnoreCase)
        || ModuleManifest.IsManifestPath(relativePath);

    /// <summary>
    /// The canonical node path a node-repo file maps to, or null for the non-node files a package
    /// ships (README, the manifest sidecar, <c>content/**</c> assets). Used to derive prune targets
    /// from a manifest diff's removed files — a removed content asset must never delete its owning
    /// node.
    /// </summary>
    public static string? NodePathForFile(string relativePath)
    {
        if (string.Equals(relativePath, "README.md", StringComparison.OrdinalIgnoreCase)
            || ModuleManifest.IsManifestPath(relativePath)
            || ContentAssetMapper.IsContentPath(relativePath))
            return null;
        var (id, ns) = NodeFileMapper.FromRelativePath(relativePath);
        return string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
    }

    /// <summary>
    /// Removes the install record <c>{InstalledPartition}/{packageId}</c> — the ONE sanctioned
    /// removal route for a record the installer wrote (#840).
    ///
    /// <para><b>Why this has to exist.</b> Install records are written under SYSTEM impersonation
    /// into a partition whose <c>_Policy</c> caps <c>create/update/delete</c> at <c>false</c> for
    /// EVERY caller — a platform admin holding an Admin assignment on <c>Plugins</c> included. That
    /// policy is correct (only the installer writes there), but it left no way out: when a package
    /// leaves the registry (a course folder renamed <c>KmuBasics</c> → <c>AgenticOffice</c>) its
    /// record has no catalog card, hence no Uninstall, and <c>publicRead</c> keeps the phantom
    /// "installed" record rendering publicly forever. The gap was the missing SURFACE, not the
    /// policy — so the fix is a system-identity removal primitive, used by an ADMIN-GATED action
    /// (<see cref="CatalogLayoutAreas"/>' orphan list), never a relaxation of the policy or a
    /// user-identity delete.</para>
    ///
    /// <para>Same shape as the installer's prune: <c>ImpersonateAsSystem</c> scoped around the ONE
    /// delete (<c>Observable.Using</c>, so the impersonation is alive when the request is actually
    /// posted on Subscribe). Only the RECORD is removed — the installed content partition is a
    /// separate lifecycle (delete it as a partition), which is why removing the record is safe even
    /// while its content is still in use.</para>
    ///
    /// <para>A thin pass-through: an ABSENT record faults with the mesh's own "Node not found"
    /// (the delete's contract), which the caller surfaces. That is the second admin clicking a card
    /// the first one already removed — logged, never swallowed into a fake success.</para>
    /// </summary>
    /// <param name="hub">The hub owning the mesh service.</param>
    /// <param name="packageId">The package id whose record to remove (the record's node id).</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>A cold observable emitting whether the record was deleted; Subscribe to run.</returns>
    public static IObservable<bool> RemoveInstalledRecord(
        IMessageHub hub, string packageId, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (string.IsNullOrWhiteSpace(packageId))
            return Observable.Throw<bool>(new ArgumentException(
                "A package id is required to remove an install record.", nameof(packageId)));

        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Throw<bool>(new InvalidOperationException(
                "No IMeshService is registered — cannot remove an install record."));

        // 🚨 System identity is REQUIRED, never best-effort. Plugins/_Policy caps delete for every
        // caller (install records are written only under scoped impersonation), so falling back to
        // Disposable.Empty would run this delete under whatever ambient principal happens to be on
        // the thread — denied, and reported as a puzzling access error instead of a wiring fault.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        if (accessService is null)
            return Observable.Throw<bool>(new InvalidOperationException(
                "No AccessService is registered — cannot remove an install record: the delete must "
                + "run as System because Plugins/_Policy denies it to every ordinary caller."));

        var recordPath = $"{InstalledPartition}/{packageId}";
        // 🚨 RunAsSystem, never Observable.Using (#1790) — and doubly so for a method that RETURNS
        // the scoped observable: with Observable.Using the scope is opened on whatever thread the
        // caller subscribes from and disposed on the delete's terminating thread, so the caller is
        // left running as System and the terminating thread is handed the caller's identity.
        return accessService.RunAsSystem(() => meshService.DeleteNode(recordPath))
            .Take(1)
            // DeleteNode faults on a missing node rather than answering false, so a value here IS a
            // removal — the caller's error path reports the absent-record case.
            .Do(_ => logger?.LogInformation(
                "[PackageInstaller] removed install record {Path}", recordPath));
    }

    /// <summary>
    /// Splits the package's installed NodeTypes into the two release waves the install issues
    /// around <see cref="SettleRetypedRoot"/>.
    ///
    /// <para>🚨 <b>Why a split, and not simply "release everything after the recycle"</b> (#1732).
    /// The recycle exists to re-activate the retyped root against its FINAL type, and
    /// <see cref="MayPublishIntoRoot"/> — the wait that decides whether recycling is even worth it
    /// — is a wait for exactly that type's rebuild. Deferring the root type's own release behind
    /// the settle would therefore wait for a compile nobody asked for, and answer only when
    /// <see cref="RootTypeSettleTimeout"/> elapsed: a 90 s stall on every self-typed package (the
    /// Store shape). So the root's own in-package type goes in wave ONE.</para>
    ///
    /// <para>Everything else goes in wave TWO, AFTER the root has been torn down and answered
    /// again. Those are the compiles that read the root: a <c>shared=</c> consumer's compile runs
    /// the cell-surface single-home gate, which reads the owning package root
    /// (<c>ValidateCellSurfaceSingleHome</c> → <c>ReadCompileSourceNode</c> →
    /// <c>GetMeshNode('&lt;packageRoot&gt;')</c>). Launching them before the recycle made the
    /// installer race its own teardown — the faulting set in every incident was exactly the
    /// module's <c>shared=</c> consumers, never anything else. #1726 made those reads PATIENT
    /// (they re-probe across a recycle and, if it outlasts the budget, report
    /// <c>CompilationStatus.Unavailable</c> rather than a code verdict); this makes them
    /// UNNECESSARY, which is the half #1726 deliberately did not do.</para>
    ///
    /// <para>With no recycle to order against (<paramref name="retypedRoot"/> null — the common
    /// re-install, where the root already carries its final type) there is nothing to defer and
    /// every type stays in wave one, exactly as before.</para>
    /// </summary>
    private static (IReadOnlyCollection<string> First, IReadOnlyCollection<string> Deferred) ReleaseWaves(
        string? retypedRoot, IReadOnlyCollection<string> nodeTypePaths, IReadOnlyCollection<MeshNode> nodes)
    {
        if (retypedRoot is null || nodeTypePaths.Count == 0)
            return (nodeTypePaths, Array.Empty<string>());
        var rootType = InPackageTypeOf(retypedRoot, nodes);
        if (rootType is null)
            return (Array.Empty<string>(), nodeTypePaths);
        // OrdinalIgnoreCase to match InPackageTypeOf's own type↔path comparison.
        var first = nodeTypePaths
            .Where(p => string.Equals(p, rootType, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var deferred = nodeTypePaths
            .Where(p => !string.Equals(p, rootType, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return (first, deferred);
    }

    /// <summary>
    /// Bound on the install-time seed, sized to the work it is a bound ON rather than to a wish.
    ///
    /// <para>🚨 <b>This used to be a flat 60 s, and that cap did not save time — it duplicated
    /// work.</b> The seed underneath (<c>ShippedPrebuiltBundles.SeedBundles</c>) is a
    /// <c>Concat</c> of one write per assembly, each with its own 30 s <c>SeedBudget</c>: strictly
    /// sequential, so a package with 17 assemblies (Store) is legitimately allowed several minutes
    /// while the outer cap fired at one. And <c>Timeout</c> abandons the RESULT, never the WORK: the
    /// seed ran on and reported its coverage afterwards, while the install had already fallen back
    /// to compiling the very types it was about to deliver. Measured 2026-08-27 (Education e2e,
    /// shard 1): Store was in Roslyn at 06:38, the "adoption attempt failed" line landed at 06:51,
    /// and the tally right behind it read 27 assemblies backed. When the cap fires, the system does
    /// BOTH — the seed and the compile it exists to avoid — which is strictly worse than either.
    /// That is the same inversion #1317 removed one layer down: a join must not out-run the answers
    /// it is joining.</para>
    ///
    /// <para>The fallback this guards is a Roslyn compile per type, which the release watcher
    /// allows <c>CompilationCacheOptions.RoslynCompileTimeout</c> (5 min) EACH. A bound on the
    /// cheaper path must therefore be no tighter than the inner budget summed over the types it
    /// covers — anything less is a promise to do the expensive thing whenever the cheap one is
    /// merely busy. Per-assembly, on top of a floor for the enumeration and bundle reads.</para>
    /// </summary>
    private static readonly TimeSpan SeedFloor = TimeSpan.FromSeconds(30);

    /// <summary>Per-installed-type share of the install-time seed bound — the inner seed's own
    /// per-assembly budget, so the outer bound can never expire while the inner work is still
    /// inside its budget.</summary>
    private static readonly TimeSpan SeedPerType = TimeSpan.FromSeconds(30);

    /// <summary>The install-time seed bound for <paramref name="typeCount"/> installed types —
    /// pure, so the sizing rule is pinned by a test with no hub. Never below the floor.</summary>
    internal static TimeSpan SeedBound(int typeCount)
        => SeedFloor + SeedPerType * Math.Max(0, typeCount);

    /// <summary>
    /// #1707 slice 3 — adopt-before-compile at INSTALL: give the deployment's prebuilt bundle
    /// sources one bounded chance to supply the just-installed types' assemblies, BEFORE any
    /// release request is issued. An adopted type's request is SATISFIED by the release watcher
    /// (Ok + sources current + usable build ⇒ no Roslyn); anything not adopted compiles exactly as
    /// before. Never faults — every failure degrades to "the installed types compile".
    ///
    /// <para>The bound is a STALL detector, sized by <see cref="SeedFloor"/> +
    /// <see cref="SeedPerType"/> × types — see the remarks on those fields for why a flat cap
    /// shorter than the fallback it guards was the bug, not a safety.</para>
    /// </summary>
    /// <returns>A cold observable of the number of adopted assemblies; Subscribe to run.</returns>
    private static IObservable<int> SeedPrebuiltAssemblies(
        IMessageHub hub, IReadOnlyCollection<string> nodeTypePaths, ILogger? logger)
    {
        var consumer = hub.ServiceProvider.GetService<IPrebuiltAssemblyConsumer>();
        if (consumer is null || nodeTypePaths.Count == 0)
            return Observable.Return(0);
        var bound = SeedBound(nodeTypePaths.Count);
        return consumer.SeedForTypes(nodeTypePaths)
            .Take(1)
            .Timeout(bound)
            .Catch<int, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Install: prebuilt adoption attempt failed after {Bound} for {Count} type(s) — "
                    + "the installed types compile instead. This bound is the inner seed's own "
                    + "per-assembly budget summed, so expiring it means the seed STALLED, not that "
                    + "it was slow",
                    bound, nodeTypePaths.Count);
                return Observable.Return(0);
            })
            .Do(adopted =>
            {
                if (adopted > 0)
                    logger?.LogInformation(
                        "Install: adopted {Adopted} prebuilt assembly(ies) for {Count} installed "
                        + "type(s) — their release requests settle without compiling",
                        adopted, nodeTypePaths.Count);
            });
    }

    /// <summary>
    /// Flips the release trigger on each of <paramref name="nodeTypePaths"/> and completes once
    /// every flip has LANDED.
    ///
    /// <para>🚨 The completion is the whole point: an install must be able to ORDER a teardown
    /// against the compiles it starts, and the previous fire-and-forget shape returned nothing to
    /// order against (#1732). Merged, not concatenated — the flips are independent writes to
    /// different per-type hubs, exactly as concurrent as the old synchronous <c>foreach</c> made
    /// them; only the "and now they have all landed" signal is new.</para>
    ///
    /// <para>Never faults: a refused or failed flip is logged and the install carries on, so one
    /// unreleasable type can neither fail the install nor strand the types after it.</para>
    /// </summary>
    /// <returns>A cold observable; Subscribe to request the releases.</returns>
    private static IObservable<System.Reactive.Unit> RequestReleases(
        IMessageHub hub, IReadOnlyCollection<string> nodeTypePaths, ILogger? logger)
    {
        if (nodeTypePaths.Count == 0)
            return Observable.Return(System.Reactive.Unit.Default);
        // System-impersonated: the release flips are stream writes posted from a pool continuation
        // with no ambient context — and the seed's own System scope does not flow across the
        // subscription hop (AsyncLocal), so it is re-established here. Observable.Using keeps the
        // scope alive for the SUBSCRIBE of every flip (each is cold), which a bare `using` around
        // the composition would not.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using(
                () => accessService?.ImpersonateAsSystem()
                      ?? System.Reactive.Disposables.Disposable.Empty,
                _ => nodeTypePaths
                    .Select(path => hub.ObserveNodeTypeRelease(path,
                        onError: msg => logger?.LogWarning(
                            "Release request for {Path} failed: {Msg}", path, msg)))
                    .Merge()
                    .ToList())
            .Select(_ => System.Reactive.Unit.Default)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Install: seed-then-release failed for {Count} type(s) — request releases manually "
                    + "(Compile button) if assemblies stay stale", nodeTypePaths.Count);
                return Observable.Return(System.Reactive.Unit.Default);
            });
    }

    /// <summary>
    /// The composed <see cref="SeedPrebuiltAssemblies"/> → <see cref="RequestReleases"/> pair, for
    /// the paths that have no root recycle to order around (the incremental delta update). Cold:
    /// Subscribe to run, and the completion means every release trigger has landed.
    /// </summary>
    private static IObservable<System.Reactive.Unit> SeedThenRequestReleases(
        IMessageHub hub, IReadOnlyCollection<string> nodeTypePaths, ILogger? logger)
        => SeedPrebuiltAssemblies(hub, nodeTypePaths, logger)
            .SelectMany(_ => RequestReleases(hub, nodeTypePaths, logger));

}

/// <summary>
/// The outcome of installing a package: how many capability nodes it carried (<see cref="Total"/>),
/// how many were actually written (<see cref="Written"/>), and — derived — how many were left
/// untouched because their content was unchanged (<see cref="Unchanged"/>). A clean re-install of an
/// unchanged package has <c>Written == 0</c>.
/// </summary>
public readonly record struct InstallResult(int Total, int Written)
{
    /// <summary>Nodes left untouched because their content did not change.</summary>
    public int Unchanged => Total - Written;

    /// <summary>
    /// The PATHS that were actually written, when the install path tracked them (the node-repo
    /// flavor does; older flavors leave this empty). A re-install of an unchanged snapshot must
    /// write nothing — when it does, a bare count is undiagnosable, and an unnamed regression is
    /// how the placeholder-root churn shipped: every gate run said "wrote 1 node" and nothing said
    /// WHICH. Named paths turn the idempotence pin's failure into the fix's first line.
    /// </summary>
    public System.Collections.Immutable.ImmutableList<string> WrittenPaths { get; init; } =
        System.Collections.Immutable.ImmutableList<string>.Empty;


}
