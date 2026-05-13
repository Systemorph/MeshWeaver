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
/// <para>The slow path uses the dedicated <see cref="NodeTypeServiceHub"/>
/// workspace to subscribe to the target NodeType's MeshNode stream — the
/// mesh hub itself must never be the requesting workspace for cross-hub
/// remote streams (causes re-entry deadlock during routing). When the
/// Activity hub's auto-watcher writes Ok/Error back to the parent MeshNode,
/// this subscriber sees the settled state and applies it.</para>
/// </summary>
internal static class NodeTypeEnrichmentHelpers
{
    private static readonly TimeSpan SlowPathTimeout = TimeSpan.FromSeconds(30);

    public static IObservable<MeshNode> EnrichWithNodeType(
        IMessageHub serviceHub,
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
        var providerNode = serviceHub.ServiceProvider
            .GetServices<IStaticNodeProvider>()
            .SelectMany(p => p.GetStaticNodes())
            .FirstOrDefault(n => string.Equals(n.Path, nodeType, StringComparison.OrdinalIgnoreCase));
        if (providerNode is { HubConfiguration: { } hubCfg }
            && !string.IsNullOrEmpty(providerNode.AssemblyLocation))
        {
            return Observable.Return(ApplyEntry(
                node, providerNode.AssemblyLocation!, hubCfg, nodeType, meshConfiguration));
        }

        // Slow path: dedicated NodeTypeService hub workspace + remote stream.
        IWorkspace workspace;
        try { workspace = serviceHub.GetWorkspace(); }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "EnrichWithNodeType: serviceHub workspace unavailable for {NodeType}", nodeType);
            return Observable.Return(
                WithCompilationErrorOverlay(node, nodeType,
                    "NodeType service workspace unavailable", meshConfiguration));
        }

        var typeAddress = new Address(nodeType);
        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            typeAddress, new MeshNodeReference());
        if (stream is null)
            return Observable.Return(
                WithCompilationErrorOverlay(node, nodeType,
                    $"No remote stream available for NodeType '{nodeType}'", meshConfiguration));

        var triggered = 0;

        return stream
            .Where(change => change?.Value != null)
            .Select(change => change.Value!)
            .Do(typeNode =>
            {
                if (typeNode.Content is not NodeTypeDefinition def) return;
                // Static-provider NodeTypes (IStaticNodeProvider — see
                // NodeOperationsTestSeedProviders.TypeDefinition) arrive with
                // HubConfiguration + AssemblyLocation already set and no
                // CompilationStatus. There's nothing for the CompileWatcher to
                // do; flipping Pending only wastes a Roslyn round-trip and
                // strands the slow path waiting on a Compiling → Ok/Error
                // transition that may never happen. Don't trigger.
                if (typeNode.HubConfiguration != null
                    && !string.IsNullOrEmpty(typeNode.AssemblyLocation)) return;
                if (def.CompilationStatus is not null
                    && def.CompilationStatus != CompilationStatus.Unknown) return;
                if (System.Threading.Interlocked.CompareExchange(ref triggered, 1, 0) != 0) return;
                logger?.LogDebug(
                    "EnrichWithNodeType slow path: flipping Pending for {NodeType}", nodeType);
                stream.Update(current =>
                {
                    if (current?.Content is not NodeTypeDefinition d) return null;
                    if (d.CompilationStatus is not null && d.CompilationStatus != CompilationStatus.Unknown)
                        return null;
                    return new ChangeItem<MeshNode>(
                        Value: current with { Content = d with { CompilationStatus = CompilationStatus.Pending } },
                        ChangedBy: WellKnownUsers.System,
                        StreamId: stream.StreamId,
                        ChangeType: ChangeType.Full,
                        Version: stream.Hub.Version,
                        Updates: null);
                });
            })
            // Settled state OR an already-configured static-provider node
            // (HubConfiguration + AssemblyLocation pre-populated, no compile).
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

/// <summary>
/// Singleton on the mesh hub that hosts a dedicated <see cref="IMessageHub"/>
/// for NodeType stream subscriptions. The mesh hub itself must never be the
/// requesting workspace for <c>GetRemoteStream</c> — when invoked from
/// routing/activation, the SubscribeRequest re-enters the same dispatcher
/// that's waiting on the EnrichWithNodeType result.
/// </summary>
public sealed class NodeTypeServiceHub
{
    private readonly Lazy<IMessageHub> _hub;

    public NodeTypeServiceHub(IMessageHub meshHub)
    {
        _hub = new Lazy<IMessageHub>(
            () => meshHub.GetHostedHub(
                new Address("_nodetype-service"),
                c => c.AddMeshDataSource()),
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IMessageHub Hub => _hub.Value;
}
