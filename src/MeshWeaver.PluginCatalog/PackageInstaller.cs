using System.Reactive.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Installs a content package's folder into the mesh: parse the folder's files into MeshNodes,
/// rebase them under the package's target partition, and upsert them INCREMENTALLY via
/// <see cref="CreateOrUpdateNodeRequest"/> — never through the static-repo importer, whose
/// full-replace/prune semantics would wipe the rest of a shared partition (installing one agent
/// must not delete every other agent). After the content lands, an install record (a
/// <c>Package</c> node) is written under the <see cref="InstalledPartition"/> registry so the
/// catalog can show installed / update-available status. Reactive end-to-end; Subscribe to run.
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
    /// partition and records the install. Emits the number of content nodes upserted.
    /// </summary>
    public static IObservable<int> Install(
        IMessageHub hub,
        PackageManifest manifest,
        IReadOnlyList<PackageFile> files,
        string installedFromRef,
        ILogger? logger = null,
        int batchSize = DefaultBatchSize)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.PluginCatalog.PackageInstaller");

        if (manifest.Kind == PackageKind.Code)
            return InstallCode(hub, manifest, files, installedFromRef, logger, batchSize);

        var partition = manifest.TargetPartition;
        if (string.IsNullOrWhiteSpace(partition))
            return Observable.Throw<int>(new InvalidOperationException(
                $"Package '{manifest.Id}' has no targetPartition."));

        var sourceFolder = manifest.SourceFolder ?? manifest.Id;
        var parsers = new FileFormatParserRegistry(hub.JsonSerializerOptions);

        var nodes = files
            .Where(f => !IsManifest(f.RelativePath))
            .Select(f => ParseNode(parsers, partition!, sourceFolder, f, logger))
            .Where(n => n is not null).Select(n => n!)
            .ToArray();

        if (nodes.Length == 0)
            return Observable.Throw<int>(new InvalidOperationException(
                $"Package '{manifest.Id}' has no installable content files."));

        return nodes
            .Select(n => Upsert(hub, n))
            .ToObservable()
            .Merge(batchSize)
            .Sum()
            .SelectMany(count =>
            {
                logger?.LogInformation(
                    "Installed package {Id} v{Version}: {Count} node(s) into {Partition} @ {Ref}",
                    manifest.Id, manifest.Version, count, partition, installedFromRef);
                return WriteInstalledRecord(hub, manifest, installedFromRef, count).Select(_ => count);
            });
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
    // Sources query finds them), and record the install. Creating the NodeType + Source nodes drives
    // the mesh's first-build Roslyn compile, so the type goes live — no rebuild, no NuGet.
    private static IObservable<int> InstallCode(
        IMessageHub hub, PackageManifest manifest, IReadOnlyList<PackageFile> files,
        string installedFromRef, ILogger? logger, int batchSize)
    {
        if (string.IsNullOrWhiteSpace(manifest.NodeTypeConfiguration))
            return Observable.Throw<int>(new InvalidOperationException(
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
            return Observable.Throw<int>(new InvalidOperationException(
                $"Code package '{manifest.Id}' has no Source/*.cs files."));

        var nodeTypeNode = MeshNode.FromPath(nodeTypePath) with
        {
            NodeType = MeshNode.NodeTypePath,
            Name = manifest.Name ?? manifest.Id,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { Configuration = manifest.NodeTypeConfiguration },
        };

        // NodeType first, then its Source Code nodes — the mesh compiles the first build automatically.
        return Upsert(hub, nodeTypeNode)
            .SelectMany(_ => sourceNodes.Select(n => Upsert(hub, n)).ToObservable().Merge(batchSize).Sum())
            .SelectMany(srcCount =>
            {
                var total = srcCount + 1;
                logger?.LogInformation(
                    "Installed code package {Id} v{Version}: NodeType {Path} + {Count} source node(s) @ {Ref}",
                    manifest.Id, manifest.Version, nodeTypePath, srcCount, installedFromRef);
                // Trigger the compile explicitly (belt-and-suspenders — creating the NodeType +
                // Source nodes also kicks the first build) so the installed type goes live.
                hub.RequestNodeTypeRelease(nodeTypePath,
                    onError: msg => logger?.LogWarning(
                        "Release request for {Path} failed: {Msg}", nodeTypePath, msg));
                return WriteInstalledRecord(hub, manifest, installedFromRef, total).Select(_ => total);
            });
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
