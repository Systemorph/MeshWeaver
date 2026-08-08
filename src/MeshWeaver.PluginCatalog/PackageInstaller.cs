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
        return PackageEntitlement.Authorize(hub, manifest, authorizingUserId, logger)
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
        var parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions);

        var nodes = files
            .Where(f => !IsManifest(f.RelativePath))
            .Select(f => ParseNode(parsers, partition!, sourceFolder, f, logger))
            .Where(n => n is not null).Select(n => n!)
            .ToArray();

        if (nodes.Length == 0)
            return Observable.Throw<InstallResult>(new InvalidOperationException(
                $"Package '{manifest.Id}' has no installable content files."));

        var options = hub.JsonSerializerOptions;
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        return EnsurePartitionsProvisioned(hub, partition, InstalledPartition)
            .SelectMany(_ => nodes
                .Select(n => UpsertIfChanged(hub, persistence, n, options))
                .ToObservable().Merge(batchSize).ToList())
            .SelectMany(writes =>
            {
                var result = new InstallResult(nodes.Length, writes.Count(w => w));
                logger?.LogInformation(
                    "Installed package {Id} v{Version}: {Written} written, {Unchanged} unchanged into {Partition} @ {Ref}",
                    manifest.Id, manifest.Version, result.Written, result.Unchanged, partition, installedFromRef);
                // The declared-access shape lands BEFORE the roots are warmed: warming ACTIVATES
                // each root hub, whose gating pass seeds the partition's access table — it must see
                // the shape this package declares, not a partition nobody can read.
                return EnsureDeclaredAccess(hub, manifest, partition, logger,
                        nodes.Select(n => n.Path))
                    .SelectMany(_ => WriteInstalledRecord(
                        hub, manifest, installedFromRef, nodes.Length, authorizingUserId: authorizingUserId))
                    .SelectMany(_ => WarmInstalledRoots(hub, nodes.Select(n => n.Path), logger))
                    // …then the package's committed binaries, into the warmed root's content
                    // collection — the half of "publish" that merging used to leave undone (#848).
                    .SelectMany(_ => SyncPackageContent(hub, partition, sourceFolder, files, logger))
                    .SelectMany(_ => RunInstallHooks(hub, partition!, logger))
                    .Select(_ => result);
            });
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
    ///   <item><b>Free</b> (<see cref="PackageManifest.Price"/> 0 or absent) with no declared
    ///     <see cref="PackageManifest.PublicSegments"/> — the same fully-public policy: a free
    ///     package that a catalog hands out must be readable by everyone, signed in or not.</item>
    ///   <item><b>Free with declared <see cref="PackageManifest.PublicSegments"/></b> — public read
    ///     SCOPED to the declaration: Public+Anonymous Viewer grants at the partition root (the
    ///     cover and, by downward inheritance, the declared segments) plus Public+Anonymous Viewer
    ///     DENIES on every other child segment — the exact root-grant + per-child-deny shape the
    ///     Store's <c>CatalogGate</c> seeds for <c>/Store</c> (#200/#204). Underscore satellites
    ///     and the well-known <c>Public</c> segment follow the <c>PluginGate</c> conventions so the
    ///     two mechanisms converge instead of fighting.</item>
    ///   <item><b>Priced</b> (any non-zero <see cref="PackageManifest.Price"/> — positive =
    ///     purchasable, negative = coupon-only) — the installer writes NOTHING: the partition lands
    ///     gated, readable only via the entitlement machinery (PluginGate / purchase), which is
    ///     exactly the point of a price.</item>
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

        // A priced package (positive = purchasable, negative = coupon-only) installs GATED: no
        // public read of any kind — entitlement (PluginGate / purchase / coupon) is the only way
        // in. Pre-installed overrides a price: platform baseline is public by definition.
        if (!manifest.PreInstalled && manifest.IsCommercial())
        {
            logger?.LogDebug(
                "[PackageInstaller] {Id} is priced — {Partition} stays gated (entitlement only)",
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
    /// <c>{partition}/_Policy</c>, create-only (the shape every built-in catalog already ships:
    /// read-only publication, no secrets).
    /// </summary>
    private static IObservable<Unit> EnsurePartitionPublicRead(
        IMessageHub hub, PackageManifest manifest, string partition, ILogger? logger)
    {
        var policyPath = $"{partition}/{PartitionPolicyId}";
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
        var existing = persistence is not null
            ? persistence.Read(policyPath, hub.JsonSerializerOptions).Take(1)
            : Observable.Return<MeshNode?>(null);

        return existing.SelectMany(current => current is not null
            ? Observable.Return(Unit.Default)
            : Upsert(hub, new MeshNode(PartitionPolicyId, partition)
                {
                    NodeType = PartitionAccessPolicyNodeType.NodeType,
                    Name = "Access Policy",
                    State = MeshNodeState.Active,
                    Content = new PartitionAccessPolicy { PublicRead = true },
                })
                .Do(_ => logger?.LogInformation(
                    "[PackageInstaller] {Id} declares public content — published {Partition} "
                    + "read-only to everyone via {Path}", manifest.Id, partition, policyPath))
                .Select(_ => Unit.Default));
    }

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

            var shape = new List<MeshNode>
            {
                ViewerAssignment(partition, WellKnownUsers.Public, denied: false),
                ViewerAssignment(partition, WellKnownUsers.Anonymous, denied: false),
            };
            foreach (var child in gated)
            {
                shape.Add(ViewerAssignment(child, WellKnownUsers.Public, denied: true));
                shape.Add(ViewerAssignment(child, WellKnownUsers.Anonymous, denied: true));
            }

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

    private static IObservable<Unit> WarmInstalledRoots(
        IMessageHub hub, IEnumerable<string> paths, ILogger? logger)
    {
        var workspace = hub.GetWorkspace();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var roots = paths
            .Select(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0)
            return Observable.Return(Unit.Default);

        return roots
            .Select(root => Observable
                .Using(
                    () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
                    _ => workspace.GetMeshNodeStream(root)
                        .Where(node => node is not null)
                        .Take(1)
                        .Timeout(WarmTimeout))
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
            .DefaultIfEmpty(string.Empty)
            .LastAsync()
            .Select(_ => Unit.Default);
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
    /// </summary>
    private static IObservable<int> SyncPackageContent(
        IMessageHub hub, string? rootPath, string? sourceFolder,
        IReadOnlyList<PackageFile> files, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return Observable.Return(0);

        // Cheap path-only precheck first, so the (majority) node files never materialize bytes —
        // the same two-step GitHubSyncService.ParseSnapshot uses.
        var assets = files
            .Select(file => (File: file, Relative: FolderRelative(file.RelativePath, sourceFolder)))
            .Where(x => ContentAssetMapper.IsContentPath(x.Relative))
            .Select(x => ContentAssetMapper.TryClassify(x.Relative, () => x.File.Bytes))
            .Where(asset => asset is not null).Select(asset => asset!)
            .ToArray();
        if (assets.Length == 0)
            return Observable.Return(0);

        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return ContentAssetMapper.ToContentSyncs(rootPath!, assets)
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
                },
            };
            // System-impersonated like every installer write (Using — see Upsert): this runs after
            // barrier scheduler hops, where no ambient context survives.
            return Observable.Using(
                    () => hub.ServiceProvider.GetService<AccessService>()?.ImpersonateAsSystem()
                          ?? System.Reactive.Disposables.Disposable.Empty,
                    _ => hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(record)))
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
        var parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions);

        var sourceNodes = files
            .Where(f => !IsManifest(f.RelativePath))
            .Select(f => ParseNode(parsers, nodeTypePath, sourceFolder, f, logger))
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
        var options = hub.JsonSerializerOptions;
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();

        // NodeType first (so its Source nodes attach under a present type), then the Source nodes;
        // each is skipped when unchanged.
        return EnsurePartitionsProvisioned(hub, partition, InstalledPartition)
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
                // kick a redundant Roslyn build.
                if (written > 0)
                {
                    // System-impersonated: the release flip is a stream write posted from a
                    // continuation with no ambient context (see Upsert).
                    var accessService = hub.ServiceProvider.GetService<AccessService>();
                    using (accessService?.ImpersonateAsSystem())
                        hub.RequestNodeTypeRelease(nodeTypePath,
                            onError: msg => logger?.LogWarning(
                                "Release request for {Path} failed: {Msg}", nodeTypePath, msg));
                }
                return WriteInstalledRecord(
                        hub, manifest, installedFromRef, all.Length, authorizingUserId: authorizingUserId)
                    .SelectMany(_ => WarmInstalledRoots(hub, all.Select(n => n.Path), logger))
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
        // does not read as a change — with the incoming ALIGNED to the current content's TYPE first
        // (see AlignedIncoming): the persisted side is often typed and materializes C# property
        // defaults the repo file legitimately omits.
        return ContentSignature(AlignedIncoming(current.Content, incoming.Content, options) ?? current.Content, options)
            == ContentSignature(current.Content, options);
    }

    /// <summary>
    /// Materialized-default alignment for the content compare. The persisted side is often TYPED —
    /// the owning hub re-serialized it, materializing C# property defaults (the diagnosed case:
    /// <c>PluginContent.Currency = "CHF"</c>) — while the incoming side is the repo file's raw
    /// <c>JsonElement</c>, which legitimately OMITS defaulted properties. Signing them as-is reads
    /// every materialized default as a change: the NONDETERMINISTIC "re-install of the unchanged
    /// snapshot wrote 1 node(s)" root churn behind the plugins gate's flapping idempotence check
    /// (~14 packages allow-listed) — nondeterministic because it fires only once the hub happens to
    /// have re-serialized the node before the re-install's read. Deserializing the incoming element
    /// to the CURRENT content's type makes both sides materialize the same defaults.
    ///
    /// <para>Guards: alignment happens only when the element's <c>$type</c> matches the current
    /// content's serialized discriminator (a differing <c>$type</c> IS a real change and must never
    /// be masked by coercing into the wrong type), and a failed deserialize falls back to the raw
    /// element — worst case an idempotent rewrite, never a missed change.</para>
    /// </summary>
    private static object? AlignedIncoming(object? current, object? incoming, JsonSerializerOptions options)
    {
        if (incoming is not JsonElement { ValueKind: JsonValueKind.Object } el
            || current is null or JsonElement)
            return incoming;
        try
        {
            // A differing $type IS a real change — never mask it by coercing into the wrong type.
            // Both discriminators read defensively: a non-string $type on either side skips
            // alignment (raw compare → change detected).
            var incomingType = el.TryGetProperty("$type", out var it) && it.ValueKind == JsonValueKind.String
                ? it.GetString()
                : null;
            var currentType = JsonSerializer.SerializeToNode(current, options)
                    is System.Text.Json.Nodes.JsonObject curNode
                && curNode.TryGetPropertyValue("$type", out var ct)
                && ct is System.Text.Json.Nodes.JsonValue cv
                && cv.TryGetValue<string>(out var cts)
                ? cts
                : null;
            if (incomingType is not null
                && !string.Equals(incomingType, currentType, StringComparison.Ordinal))
                return incoming;

            // Deserialize WITHOUT tolerating unknown members: with the ambient Skip handling, a
            // property the file ADDS but the current type lacks would be silently dropped and a
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
            return withoutDiscriminator.Deserialize(current.GetType(), strict) ?? incoming;
        }
        catch (JsonException)
        {
            return incoming;                        // schema drift / unknown member — raw compare
        }
    }

    // The node's scalar fields, applying the incoming's non-null values over the current (mirrors
    // UpdateAccordingToSourceNode) — unchanged? The churn fields (LastModified/Version) are ignored.
    private static bool ScalarsUnchanged(MeshNode current, MeshNode incoming) =>
        (incoming.Name ?? current.Name) == current.Name
        && (incoming.NodeType ?? current.NodeType) == current.NodeType
        && (incoming.Icon ?? current.Icon) == current.Icon
        && (incoming.Category ?? current.Category) == current.Category
        && (incoming.State == default ? current.State : incoming.State) == current.State
        && (incoming.PreRenderedHtml ?? current.PreRenderedHtml) == current.PreRenderedHtml
        && (incoming.Order ?? current.Order) == current.Order;

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
        var parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions);
        // The CI manifest sidecar (when the package ships one) becomes the install record's diff
        // baseline — the next update touches only what its manifest diff names.
        var moduleManifest = files
            .Where(f => ModuleManifest.IsManifestPath(f.RelativePath))
            .Select(f => ModuleManifest.TryParse(f.Content, logger))
            .FirstOrDefault(m => m is not null);
        var nodes = files
            .Select(f => ParseCanonical(parsers, f, logger))
            .Where(n => n is not null).Select(n => n!)
            .ToArray();

        if (nodes.Length == 0)
            return Observable.Throw<InstallResult>(new InvalidOperationException(
                $"Node-repo plugin '{manifest.Id}' has no installable nodes."));

        var options = hub.JsonSerializerOptions;
        var persistence = hub.ServiceProvider.GetService<IStorageAdapter>();
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
            return Observable.Using(
                    () => accessService?.ImpersonateAsSystem()
                          ?? System.Reactive.Disposables.Disposable.Empty,
                    _ => persistence.WriteManyAndPublishCreated(stamped, options, changeFeed))
                .Select(written =>
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
                    return (IList<(string, bool)>)batch.Select(n => (n.Path, true)).ToList();
                });
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

        return EnsurePartitionsProvisioned(hub, manifest.TargetPartition ?? manifest.Id, InstalledPartition)
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
                var satellites = stage2.Where(n => Order(n) == 4).ToArray();
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
                        .SelectMany(_ => BulkSave(bulkSources))
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
                    .Select(rest =>
                    {
                        if (placeholderRoot is not null && root is not null)
                            hub.Post(new DisposeRequest(), o => o.WithTarget(new Address(root.Path)));
                        return rest;
                    })
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
                // Recompile only the NodeTypes, and only when something changed.
                if (result.Written > 0)
                {
                    // System-impersonated: the release flips are stream writes posted from a
                    // continuation with no ambient context (see Upsert).
                    using (accessService?.ImpersonateAsSystem())
                        foreach (var path in nodeTypePaths)
                            hub.RequestNodeTypeRelease(path,
                                onError: msg => logger?.LogWarning("Release request for {Path} failed: {Msg}", path, msg));
                }
                // Same order as the content path: publish the partition BEFORE warming its roots
                // (activation's gating pass must see the declared shape).
                return EnsureDeclaredAccess(hub, manifest, manifest.TargetPartition ?? manifest.Id,
                        logger, nodes.Select(n => n.Path))
                    .SelectMany(_ => WriteInstalledRecord(
                        hub, manifest, installedFromRef, nodes.Length, moduleManifest, authorizingUserId))
                    .SelectMany(_ => WarmInstalledRoots(hub, nodes.Select(n => n.Path), logger))
                    // …then the package's committed binaries (course videos/posters) into the
                    // warmed root's content collection — the half of "publish" that merging used
                    // to leave undone (#848).
                    .SelectMany(_ => SyncPackageContent(
                        hub, manifest.TargetPartition ?? manifest.Id,
                        manifest.SourceFolder ?? manifest.Id, files, logger))
                    .Select(_ => result);
            }));
            });
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

        var parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions);
        var nodes = changedFiles
            .Select(f => ParseCanonical(parsers, f, logger))
            .Where(n => n is not null).Select(n => n!)
            .ToArray();

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
            meshService is null || removedNodePaths.Count == 0
                ? Observable.Return(0)
                : removedNodePaths
                    .Select(path => (persistence is not null
                            ? persistence.Read(path, options).Take(1)
                            : Observable.Return<MeshNode?>(null))
                        .SelectMany(current =>
                            current is not null && current.SyncBehavior != SyncBehavior.Include
                                ? Observable.Return(0)
                                : Observable.Using(
                                        () => hub.ServiceProvider.GetService<AccessService>()?.ImpersonateAsSystem()
                                              ?? System.Reactive.Disposables.Disposable.Empty,
                                        _ => meshService.DeleteNode(path))
                                    .Take(1)
                                    .Select(deleted => deleted ? 1 : 0))
                        .Catch<int, Exception>(ex =>
                        {
                            logger?.LogWarning(ex, "Pruning removed node {Path} failed.", path);
                            return Observable.Return(0);
                        }))
                    .ToObservable().Concat().Sum();

        return EnsurePartitionsProvisioned(hub, manifest.TargetPartition ?? manifest.Id, InstalledPartition)
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

                if (releaseTargets.Length > 0)
                {
                    var accessService = hub.ServiceProvider.GetService<AccessService>();
                    using (accessService?.ImpersonateAsSystem())
                        foreach (var path in releaseTargets)
                            hub.RequestNodeTypeRelease(path,
                                onError: msg => logger?.LogWarning("Release request for {Path} failed: {Msg}", path, msg));
                }
                // An UPDATE re-asserts the declared access too: a package that only just flipped
                // its declaration (or whose policy was lost) must converge on the next sync rather
                // than wait for a full re-install. Create-only, so an existing shape is untouched
                // and this is free on the common path.
                return EnsureDeclaredAccess(hub, manifest, manifest.TargetPartition ?? manifest.Id,
                        logger, nodes.Select(n => n.Path))
                    .SelectMany(_ => WriteInstalledRecord(hub, manifest, installedFromRef,
                        newManifest.Files.Count, newManifest, authorizingUserId))
                    .SelectMany(_ => WarmInstalledRoots(hub, nodes.Select(n => n.Path), logger))
                    // A changed BINARY is a changed file like any other: manifest.lock hashes the
                    // `content/**` assets too, so a re-cut video is in `changedFiles` and an
                    // unchanged one never travels. This is what keeps the incremental path cheap
                    // even for a course carrying tens of MB of video (#848).
                    .SelectMany(_ => SyncPackageContent(
                        hub, manifest.TargetPartition ?? manifest.Id,
                        manifest.SourceFolder ?? manifest.Id, changedFiles, logger))
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
    private static MeshNode? ParseCanonical(FileFormatParserRegistry parsers, PackageFile file, ILogger? logger)
    {
        if (string.Equals(file.RelativePath, "README.md", StringComparison.OrdinalIgnoreCase)
            || ModuleManifest.IsManifestPath(file.RelativePath)
            // A `{package}/content/**` asset is NOT a node — its bytes go to the partition root's
            // content collection (SyncPackageContent), which is where the served
            // `/static/{root}/content/…` URL resolves. Same split GitHubSyncService.ParseSnapshot
            // makes, and the one NodePathForFile below has always asserted. Skipping it here also
            // silences the "No parser for …/videos/x.mp4" warning every course emitted per install.
            || ContentAssetMapper.IsContentPath(file.RelativePath))
            return null;
        var ext = System.IO.Path.GetExtension(file.RelativePath);
        var parsed = parsers.TryParse(ext, file.RelativePath, file.Content, file.RelativePath);
        if (parsed is null)
        {
            logger?.LogWarning("No parser for node-repo file {Path}; skipped.", file.RelativePath);
            return null;
        }
        var (id, ns) = NodeFileMapper.FromRelativePath(file.RelativePath);
        return parsed with
        {
            Id = id,
            Namespace = ns,
            // Preserve an AUTHORED mainNode: an _Access grant's mainNode IS its scope (the
            // permission evaluator silently ignores a grant whose mainNode is wrong), so
            // clobbering it with the path default breaks every access file a package ships.
            MainNode = parsed.MainNode ?? (string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}"),
            State = MeshNodeState.Active,
        };
    }

    // Each write is System-impersonated INDIVIDUALLY — an ambient whole-pipeline impersonation
    // does not survive the pipeline's scheduler hops. Observable.Using, NOT Defer+using: the
    // post happens when hub.Observe's stream is SUBSCRIBED, so the impersonation must still be
    // alive then (Defer+using disposes it before the post — the exact trap the Edu redeemer
    // documented). The admin-gated install is the authorization (see Install).
    private static IObservable<int> Upsert(IMessageHub hub, MeshNode node) =>
        Observable.Using(
                () => hub.ServiceProvider.GetService<AccessService>()?.ImpersonateAsSystem()
                      ?? System.Reactive.Disposables.Disposable.Empty,
                _ => hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node)))
            .FirstAsync().Select(d => d.Message)
            .SelectMany(resp => resp.Success
                ? Observable.Return(1)
                : Observable.Throw<int>(new InvalidOperationException(
                    $"Install of '{node.Path}' failed: {resp.Error}")));

    // Parse one package file into a node rebased under the target partition (mirrors
    // GitHubSyncService.ParseFile). The package.json manifest is filtered out before this.
    private static MeshNode? ParseNode(
        FileFormatParserRegistry parsers, string partition, string sourceFolder, PackageFile file, ILogger? logger)
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
        return parsed with
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
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService.DeleteNode(recordPath))
            .Take(1)
            // DeleteNode faults on a missing node rather than answering false, so a value here IS a
            // removal — the caller's error path reports the absent-record case.
            .Do(_ => logger?.LogInformation(
                "[PackageInstaller] removed install record {Path}", recordPath));
    }
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
