using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Static replacement for <c>NodeTypeService.EnrichWithNodeType</c> and its
/// supporting methods. Stateless: no in-memory dictionaries, no cross-silo
/// change-feed subscription. The MeshNode IS the cache.
///
/// <para>The slow path consumes the single shared NodeType stream from
/// <see cref="INodeTypeStreamCache"/> — one <c>Replay(1).RefCount()</c>
/// subscription per NodeType path, kept in the cache's concurrent
/// dictionary. The cache's <c>MaybeKickCompile</c> side-effect fires the
/// compile exactly once on first touch. There is no dedicated "node-type
/// service hub": <c>GetMeshNodeStream</c> for a remote path already returns
/// an <c>ISynchronizationStream</c> that runs on its own hub, so the mesh
/// hub's workspace can request it without re-entry risk.</para>
/// </summary>
internal static class NodeTypeEnrichmentHelpers
{
    private static readonly TimeSpan SlowPathTimeout = TimeSpan.FromSeconds(30);

    public static IObservable<MeshNode> EnrichWithNodeType(
        IMessageHub meshHub,
        MeshConfiguration meshConfiguration,
        IMeshNodeCompilationService? compilationService,
        MeshNode node,
        ILogger? logger = null)
    {
        if (node.HubConfiguration != null && node.AssemblyLocation != null)
            return Observable.Return(node);

        var nodeType = node.NodeType;
        if (string.IsNullOrEmpty(nodeType))
            return Observable.Return(ApplyDefaultConfig(node, meshConfiguration));

        // Static fast-path: any AddMeshNodes-registered type with both fields.
        if (meshConfiguration.Nodes.TryGetValue(nodeType, out var staticTypeNode)
            && staticTypeNode.HubConfiguration != null
            && !string.IsNullOrEmpty(staticTypeNode.AssemblyLocation))
        {
            return Observable.Return(ApplyEntry(
                node, staticTypeNode.AssemblyLocation,
                staticTypeNode.HubConfiguration, nodeType, meshConfiguration));
        }

        // Static-provider fast-path: IStaticNodeProvider-registered NodeTypes
        // ship HubConfiguration + AssemblyLocation in-process (the delegate
        // doesn't survive serialisation, so we MUST find them locally before
        // opening a remote stream). Look up the provider that owns the
        // requested NodeType path and apply its config directly.
        var providerNode = meshHub.ServiceProvider
            .GetServices<IStaticNodeProvider>()
            .SelectMany(p => p.GetStaticNodes())
            .FirstOrDefault(n => string.Equals(n.Path, nodeType, StringComparison.OrdinalIgnoreCase));
        if (providerNode is { HubConfiguration: { } hubCfg }
            && !string.IsNullOrEmpty(providerNode.AssemblyLocation))
        {
            return Observable.Return(ApplyEntry(
                node, providerNode.AssemblyLocation!, hubCfg, nodeType, meshConfiguration));
        }

        // Slow path: consume the ONE shared NodeType stream from
        // INodeTypeStreamCache — a Replay(1).RefCount() subscription per
        // NodeType path held in the cache's concurrent dictionary. Its
        // MaybeKickCompile side-effect fires the compile exactly once on
        // first touch of an un-built NodeType. We do NOT roll our own remote
        // stream or Pending-flip here — that produced a second, uncoordinated
        // compile trigger and stranded the wait when neither trigger ran.
        var streamCache = meshHub.ServiceProvider.GetService<INodeTypeStreamCache>();
        if (streamCache is null)
        {
            logger?.LogWarning(
                "EnrichWithNodeType: INodeTypeStreamCache not registered — cannot resolve {NodeType}",
                nodeType);
            return Observable.Return(
                WithCompilationErrorOverlay(node, nodeType,
                    "NodeType stream cache unavailable", meshConfiguration));
        }

        return streamCache.GetStream(nodeType)
            // Settled compile (Ok/Error) OR an already-configured node
            // (HubConfiguration + AssemblyLocation pre-populated — static
            // provider, or a built-in that ships its assembly).
            .Where(typeNode => (typeNode.HubConfiguration != null
                                && !string.IsNullOrEmpty(typeNode.AssemblyLocation))
                || (typeNode.Content is NodeTypeDefinition def
                    && (def.CompilationStatus == CompilationStatus.Ok
                        || def.CompilationStatus == CompilationStatus.Error)))
            .Take(1)
            .Timeout(SlowPathTimeout)
            .SelectMany(typeNode => ApplyStreamResult(
                typeNode, node, nodeType, meshConfiguration, compilationService, logger))
            .Catch<MeshNode, Exception>(ex =>
            {
                logger?.LogDebug(ex,
                    "EnrichWithNodeType slow path for '{NodeType}' faulted — applying compilation-error overlay",
                    nodeType);
                return Observable.Return(
                    WithCompilationErrorOverlay(node, nodeType, ex.Message, meshConfiguration));
            });
    }

    private static IObservable<MeshNode> ApplyStreamResult(
        MeshNode typeNode,
        MeshNode node,
        string nodeType,
        MeshConfiguration meshConfiguration,
        IMeshNodeCompilationService? compilationService,
        ILogger? logger)
    {
        var def = typeNode.Content as NodeTypeDefinition;

        // Static-provider NodeType: HubConfiguration + AssemblyLocation pre-populated,
        // no Roslyn compile to run. Use them directly — no reflection round-trip.
        if (typeNode.HubConfiguration != null
            && !string.IsNullOrEmpty(typeNode.AssemblyLocation)
            && (def?.CompilationStatus is null
                || def.CompilationStatus == CompilationStatus.Unknown))
        {
            return Observable.Return(ApplyEntry(
                node, typeNode.AssemblyLocation!, typeNode.HubConfiguration,
                nodeType, meshConfiguration));
        }

        if (def?.CompilationStatus == CompilationStatus.Ok
            && !string.IsNullOrEmpty(typeNode.AssemblyLocation))
        {
            if (compilationService is null)
                return Observable.Return(ApplyEntry(
                    node, typeNode.AssemblyLocation!, hubConfig: null,
                    nodeType, meshConfiguration));

            return compilationService.CompileAndGetConfigurations(typeNode)
                .Take(1)
                .Select(result =>
                {
                    var matching = result?.NodeTypeConfigurations
                        .FirstOrDefault(c =>
                            string.Equals(c.NodeType, nodeType, StringComparison.OrdinalIgnoreCase))
                        ?? result?.NodeTypeConfigurations.FirstOrDefault();
                    return ApplyEntry(
                        node, typeNode.AssemblyLocation!, matching?.HubConfiguration,
                        nodeType, meshConfiguration);
                })
                .Catch<MeshNode, Exception>(ex =>
                {
                    logger?.LogDebug(ex,
                        "EnrichWithNodeType: HubConfiguration reflection for '{NodeType}' faulted",
                        nodeType);
                    return Observable.Return(ApplyEntry(
                        node, typeNode.AssemblyLocation!, hubConfig: null,
                        nodeType, meshConfiguration));
                });
        }

        var error = def?.CompilationError ?? "Compilation failed";
        return Observable.Return(
            WithCompilationErrorOverlay(node, nodeType, error, meshConfiguration));
    }

    public static MeshNode ApplyDefaultConfig(MeshNode node, MeshConfiguration meshConfiguration)
    {
        if (node.HubConfiguration != null) return node;
        var defaultConfig = meshConfiguration.DefaultNodeHubConfiguration;
        return defaultConfig != null
            ? node with { HubConfiguration = defaultConfig }
            : node;
    }

    private static MeshNode ApplyEntry(
        MeshNode node,
        string assemblyLocation,
        Func<MessageHubConfiguration, MessageHubConfiguration>? hubConfig,
        string nodeType,
        MeshConfiguration meshConfiguration)
    {
        return CopyIconFromNodeType(
            node with
            {
                HubConfiguration = node.HubConfiguration ?? hubConfig,
                AssemblyLocation = node.AssemblyLocation
                    ?? (string.IsNullOrEmpty(assemblyLocation) ? null : assemblyLocation)
            },
            nodeType,
            meshConfiguration);
    }

    public static MeshNode WithCompilationErrorOverlay(
        MeshNode node,
        string nodeType,
        string? error,
        MeshConfiguration meshConfiguration)
    {
        var baseConfig = string.IsNullOrEmpty(error)
            ? (node.HubConfiguration ?? meshConfiguration.DefaultNodeHubConfiguration)
            : meshConfiguration.DefaultNodeHubConfiguration;

        if (string.IsNullOrEmpty(error))
            return CopyIconFromNodeType(
                node with { HubConfiguration = baseConfig }, nodeType, meshConfiguration);

        var overlay = CreateCompilationErrorConfiguration(error);
        Func<MessageHubConfiguration, MessageHubConfiguration> composed = baseConfig != null
            ? (config => overlay(baseConfig(config)))
            : overlay;
        return CopyIconFromNodeType(
            node with { HubConfiguration = composed }, nodeType, meshConfiguration);
    }

    private static MeshNode CopyIconFromNodeType(
        MeshNode node, string nodeType, MeshConfiguration meshConfiguration)
    {
        if (string.IsNullOrEmpty(node.Icon)
            && meshConfiguration.Nodes.TryGetValue(nodeType, out var builtInNode)
            && !string.IsNullOrEmpty(builtInNode.Icon))
        {
            return node with { Icon = builtInNode.Icon };
        }
        return node;
    }

    private static Func<MessageHubConfiguration, MessageHubConfiguration>
        CreateCompilationErrorConfiguration(string errorMessage)
    {
        return config => config.AddLayout(layout =>
            layout.WithView(MeshNodeLayoutAreas.OverviewArea, (host, ctx) =>
                Observable.Return<UiControl?>(BuildCompilationErrorMarkdown(errorMessage))));
    }

    private static UiControl BuildCompilationErrorMarkdown(string errorMessage)
    {
        var newlineIdx = errorMessage.IndexOf('\n');
        var header = newlineIdx >= 0 ? errorMessage[..newlineIdx].TrimEnd(':') : errorMessage;
        var body = newlineIdx >= 0 ? errorMessage[(newlineIdx + 1)..].TrimEnd() : string.Empty;

        var markdown =
$@"> **⚠ {header}**
>
> Fix the source code or the NodeType's `sources` list, then use the **Recycle** menu to flush the cached grain (or call `GetDiagnostics` via MCP to re-check).

```text
{body}
```";

        return Controls.Stack
            .WithStyle("padding: 16px;")
            .WithView(Controls.Markdown(markdown));
    }
}
