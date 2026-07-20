using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
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

    /// <summary>
    /// Installs <paramref name="manifest"/>'s content <paramref name="files"/> into its target
    /// partition and records the install, writing only the nodes that actually changed.
    /// </summary>
    public static IObservable<InstallResult> Install(
        IMessageHub hub,
        PackageManifest manifest,
        IReadOnlyList<PackageFile> files,
        string installedFromRef,
        ILogger? logger = null,
        int batchSize = DefaultBatchSize)
    {
        // Installing a curated package is a platform action (the catalog tab gates it to global
        // admins; that gate IS the authorization) — the same footing as a GitSync import: it
        // writes partition ROOTS whose node types are dynamic (e.g. Store/Plugin — invisible to
        // the static-only PartitionWriteGuard check) and type/infrastructure nodes no user
        // principal may create. The SYSTEM impersonation is scoped around EACH write (inside
        // Upsert), never around the whole pipeline: the pipeline hops schedulers (visibility
        // barriers on a timer), and an ambient impersonation does not survive those hops.
        return InstallCore(hub, manifest, files, installedFromRef, logger, batchSize);
    }

    private static IObservable<InstallResult> InstallCore(
        IMessageHub hub,
        PackageManifest manifest,
        IReadOnlyList<PackageFile> files,
        string installedFromRef,
        ILogger? logger,
        int batchSize)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.PluginCatalog.PackageInstaller");

        if (manifest.Kind == PackageKind.Code)
            return InstallCode(hub, manifest, files, installedFromRef, logger, batchSize);

        if (manifest.Kind == PackageKind.NodeRepo)
            return InstallNodeRepo(hub, manifest, files, installedFromRef, logger, batchSize);

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
                return WriteInstalledRecord(hub, manifest, installedFromRef, nodes.Length).Select(_ => result);
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

    private static IObservable<MeshNode> WriteInstalledRecord(
        IMessageHub hub, PackageManifest manifest, string installedFromRef, int count)
    {
        var record = MeshNode.FromPath($"{InstalledPartition}/{manifest.Id}") with
        {
            NodeType = PackageNodeType,
            Name = manifest.Name ?? manifest.Id,
            State = MeshNodeState.Active,
            Content = manifest with
            {
                InstalledFromRef = installedFromRef,
                InstalledAtUtc = DateTimeOffset.UtcNow,
                InstalledNodeCount = count,
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
    }

    // Installs a Code package: synthesize the NodeType node from the manifest's configuration, import
    // the package's Source/*.cs files as its Code nodes (rebased UNDER the NodeType so its default
    // Sources query finds them), and record the install. Creating/updating the NodeType + Source nodes
    // drives the mesh's Roslyn compile — but only when something actually changed, so an unchanged
    // re-install neither rewrites nodes nor recompiles.
    private static IObservable<InstallResult> InstallCode(
        IMessageHub hub, PackageManifest manifest, IReadOnlyList<PackageFile> files,
        string installedFromRef, ILogger? logger, int batchSize)
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
                return WriteInstalledRecord(hub, manifest, installedFromRef, all.Length).Select(_ => result);
            });
    }

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
            .SelectMany(current => current is not null && IsUnchanged(current, node, options)
                ? Observable.Return(false)
                : Upsert(hub, node).Select(_ => true))
            .Catch<bool, Exception>(_ => Upsert(hub, node).Select(_ => true));
    }

    /// <summary>
    /// True when applying <paramref name="incoming"/> onto <paramref name="current"/> would produce no
    /// real change — i.e. the fields the upsert actually applies (mirrors <c>UpdateAccordingToSourceNode</c>:
    /// Content + Name/NodeType/Icon/Category/State/PreRenderedHtml) are identical, ignoring the churn
    /// fields (LastModified/Version). This is the content-checksum that makes an update touch only what
    /// really changed.
    /// </summary>
    private static bool IsUnchanged(MeshNode current, MeshNode incoming, JsonSerializerOptions options)
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
        // does not read as a change.
        return ContentSignature(incoming.Content ?? current.Content, options)
            == ContentSignature(current.Content, options);
    }

    // The node's scalar fields, applying the incoming's non-null values over the current (mirrors
    // UpdateAccordingToSourceNode) — unchanged? The churn fields (LastModified/Version) are ignored.
    private static bool ScalarsUnchanged(MeshNode current, MeshNode incoming) =>
        (incoming.Name ?? current.Name) == current.Name
        && (incoming.NodeType ?? current.NodeType) == current.NodeType
        && (incoming.Icon ?? current.Icon) == current.Icon
        && (incoming.Category ?? current.Category) == current.Category
        && (incoming.State == default ? current.State : incoming.State) == current.State
        && (incoming.PreRenderedHtml ?? current.PreRenderedHtml) == current.PreRenderedHtml;

    // Content serialized with the hub options ($type discriminators) so typed content compares
    // structurally (both sides are typed records → deterministic JSON).
    private static string ContentSignature(object? content, JsonSerializerOptions options) =>
        content is null ? "" : JsonSerializer.Serialize(content, options);

    // Installs a NODE-NATIVE plugin repo (node-per-file): the files ARE MeshNodes at their canonical
    // paths, so parse them verbatim (no partition rebase), upsert only the changed ones, and request a
    // live compile for every NodeType node. This is the shape MeshWeaver.Plugins ships.
    private static IObservable<InstallResult> InstallNodeRepo(
        IMessageHub hub, PackageManifest manifest, IReadOnlyList<PackageFile> files,
        string installedFromRef, ILogger? logger, int batchSize)
    {
        _ = batchSize; // node-repo installs are ordered (Concat), not fanned out
        var parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions);
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

        // Three stages with VISIBILITY BARRIERS between them, solving two races at once:
        //
        // (1) THE ROOT lands first — as a Space PLACEHOLDER when its real type is dynamic and
        //     ships in this very package (the Store: the root is nodeType Store/Catalog, defined
        //     by a child). The Space create runs the standard partition path (provisioning +
        //     Admin/Partition definition) and, once persistence-visible, preempts the implicit
        //     partition bootstrap: without it, the first CHILD create triggers the heal, whose
        //     generic Space root races OUR typed root through the debounced per-node-hub
        //     persists — last persist wins (observed: the heal's Space replacing the typed root).
        // (2) THE TYPES land before any instance referencing them: the create path's
        //     type-existence check reads PERSISTENCE (MeshExtensions, step 3), and a
        //     freshly-created type node persists through the DEBOUNCED pipeline — an instance
        //     written right behind its in-package type races that debounce and is refused as
        //     "not registered". The barrier polls until every type node is persistence-visible.
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

        IObservable<IList<bool>> WriteAll(IReadOnlyList<MeshNode> batch) =>
            batch.Count == 0
                ? Observable.Return((IList<bool>)new List<bool>())
                : batch.Select(n => UpsertIfChanged(hub, persistence, n, options))
                    .ToObservable().Concat().ToList(); // sequential to respect the ordering

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
        IObservable<System.Reactive.Unit> RootRetypeReconciled() =>
            placeholderRoot is null || root is null || string.IsNullOrEmpty(root.NodeType)
                ? Observable.Return(System.Reactive.Unit.Default)
                : hub.GetMeshNodeStream(root.Path)
                    .Where(n => n is not null
                        && string.Equals(n.NodeType, root.NodeType, StringComparison.Ordinal))
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(30))
                    .Select(_ => System.Reactive.Unit.Default);

        // Eager provisioning must also cover the package's OWN partition: with a dynamic root
        // type the placeholder covers it, but belt-and-braces keeps the fresh-mesh pin honest.
        return EnsurePartitionsProvisioned(hub, manifest.TargetPartition ?? manifest.Id, InstalledPartition)
            .SelectMany(_ => WriteAll(stage0))
            .SelectMany(rootWrites => Visible(root is null ? [] : [root.Path])
                .SelectMany(_ => WriteAll(stage1))
                .SelectMany(typeWrites => Visible(nodeTypePaths)
                    .SelectMany(_ => WriteAll(stage2))
                    // The retype's optimistic emit is not the reconciled state — wait for the
                    // shared root handle to actually carry the in-package type before reporting.
                    .SelectMany(rest => RootRetypeReconciled().Select(_ => rest))
                    // A placeholder's write is bookkeeping, not content — its FINAL retype in
                    // stage 2 is the root's one counted write (keeps Written ≤ node count).
                    .Select(rest => (IList<bool>)(placeholderRoot is null ? rootWrites : [])
                        .Concat(typeWrites).Concat(rest).ToList())))
            .SelectMany(writes =>
            {
                var result = new InstallResult(nodes.Length, writes.Count(w => w));
                logger?.LogInformation(
                    "Installed node-repo plugin {Id}: {Written} written, {Unchanged} unchanged ({Count} node(s)) @ {Ref}",
                    manifest.Id, result.Written, result.Unchanged, nodes.Length, installedFromRef);
                // Recompile only the NodeTypes, and only when something changed.
                if (result.Written > 0)
                {
                    // System-impersonated: the release flips are stream writes posted from a
                    // continuation with no ambient context (see Upsert).
                    var accessService = hub.ServiceProvider.GetService<AccessService>();
                    using (accessService?.ImpersonateAsSystem())
                        foreach (var path in nodeTypePaths)
                            hub.RequestNodeTypeRelease(path,
                                onError: msg => logger?.LogWarning("Release request for {Path} failed: {Msg}", path, msg));
                }
                return WriteInstalledRecord(hub, manifest, installedFromRef, nodes.Length).Select(_ => result);
            });
    }

    // Parses a node-per-file file into a MeshNode at its CANONICAL path (no partition rebase) — the
    // file's repo-relative path IS the node's path. The export's top-level README.md is a GitHub
    // display file, never a node (mirrors GitHubSyncService.ParseFile, minus the space rebase).
    private static MeshNode? ParseCanonical(FileFormatParserRegistry parsers, PackageFile file, ILogger? logger)
    {
        if (string.Equals(file.RelativePath, "README.md", StringComparison.OrdinalIgnoreCase))
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
        var rel = file.RelativePath;
        var prefix = sourceFolder + "/";
        if (rel.StartsWith(prefix, StringComparison.Ordinal))
            rel = rel[prefix.Length..];

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
        || string.Equals(relativePath, "package.json", StringComparison.OrdinalIgnoreCase);
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
}
