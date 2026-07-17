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
        // The whole install runs under the SYSTEM identity — the same footing as a GitSync
        // import. Installing a curated package is a platform action (the catalog tab gates it to
        // global admins; that gate IS the authorization): it writes partition ROOTS whose node
        // types are dynamic (e.g. Store/Plugin — invisible to the static-only
        // PartitionWriteGuard check) and type/infrastructure nodes no user principal may create.
        // Observable.Using scopes the impersonation to this one subscription.
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Using(
            () => accessService?.ImpersonateAsSystem() ?? System.Reactive.Disposables.Disposable.Empty,
            _ => InstallCore(hub, manifest, files, installedFromRef, logger, batchSize));
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
        return hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(record))
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
                    hub.RequestNodeTypeRelease(nodeTypePath,
                        onError: msg => logger?.LogWarning(
                            "Release request for {Path} failed: {Msg}", nodeTypePath, msg));
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
        // rewrite + recompile it. Compare only the authored field the installer writes — the
        // Configuration lambda (the source .cs are separate Code nodes, diffed on their own).
        if (current.Content is NodeTypeDefinition curDef && incoming.Content is NodeTypeDefinition inDef)
            return string.Equals(curDef.Configuration, inDef.Configuration, StringComparison.Ordinal);
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

        // Order so the Space root and a NodeType's Source land BEFORE the NodeType itself — creating
        // the NodeType triggers the live compile, which reads its Source children.
        static int Order(MeshNode n) =>
            n.Content is NodeTypeDefinition ? 2 : (n.Path.Contains('/', StringComparison.Ordinal) ? 1 : 0);

        // Only the install-record partition needs eager provisioning here: the repo's own content
        // partitions are provisioned by the Space-root create (OwnsPartitionProvisioningValidator),
        // which lands first in the ordering below.
        return EnsurePartitionsProvisioned(hub, InstalledPartition)
            .SelectMany(_ => nodes.OrderBy(Order)
                .Select(n => UpsertIfChanged(hub, persistence, n, options))
                .ToObservable().Concat().ToList()) // sequential to respect the ordering above
            .SelectMany(writes =>
            {
                var result = new InstallResult(nodes.Length, writes.Count(w => w));
                logger?.LogInformation(
                    "Installed node-repo plugin {Id}: {Written} written, {Unchanged} unchanged ({Count} node(s)) @ {Ref}",
                    manifest.Id, result.Written, result.Unchanged, nodes.Length, installedFromRef);
                // Recompile only the NodeTypes, and only when something changed.
                if (result.Written > 0)
                    foreach (var path in nodeTypePaths)
                        hub.RequestNodeTypeRelease(path,
                            onError: msg => logger?.LogWarning("Release request for {Path} failed: {Msg}", path, msg));
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
            MainNode = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}",
            State = MeshNodeState.Active,
        };
    }

    private static IObservable<int> Upsert(IMessageHub hub, MeshNode node) =>
        hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
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
