using System.Reactive.Linq;
using MeshWeaver.GitSync;
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

        if (manifest.Kind != PackageKind.Content)
            return Observable.Throw<int>(new NotSupportedException(
                $"Package '{manifest.Id}' is kind {manifest.Kind}; only Content packages install yet."));

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

        IObservable<int> UpsertOne(MeshNode node) =>
            hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
                .FirstAsync().Select(d => d.Message)
                .SelectMany(resp => resp.Success
                    ? Observable.Return(1)
                    : Observable.Throw<int>(new InvalidOperationException(
                        $"Install of '{node.Path}' failed: {resp.Error}")));

        return nodes
            .Select(UpsertOne)
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
