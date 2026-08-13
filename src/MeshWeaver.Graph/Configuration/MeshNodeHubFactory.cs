using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves HubConfiguration for a MeshNode via
/// <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/>, then composes
/// with <c>DefaultNodeHubConfiguration</c>. Stateless — the persisted
/// NodeType MeshNode is the cache.
/// </summary>
internal class MeshNodeHubFactory(
    IMessageHub meshHub,
    MeshConfiguration meshConfiguration,
    IMeshNodeCompilationService? compilationService,
    ILogger<MeshNodeHubFactory> logger) : IMeshNodeHubFactory
{
    public IObservable<MeshNode> ResolveHubConfiguration(MeshNode node)
        => NodeTypeEnrichmentHelpers.EnrichWithNodeType(
                meshHub, meshConfiguration, compilationService, node, logger)
            .Take(1)
            .Select(enriched =>
            {
                // 🚨 Stamp the NodeType path BEFORE the NodeType's own configuration runs, so the
                // WithContentType inside it records the content type under an exact, mesh-unique key
                // (see NodeTypePathHolder). This is the single funnel BOTH real activation paths go
                // through — MonolithRoutingService and MessageHubGrain — so stamping here covers
                // both hosts; the transient probe hubs stamp their own.
                //
                // 🚨 The stamp names the type whose CONFIGURATION IS BEING APPLIED — always
                // `enriched.NodeType`, never the node's own Path. A NodeType DEFINITION node
                // (`NodeType` == the literal "NodeType") is the case that makes the distinction
                // load-bearing, and it used to be special-cased to `enriched.Path` on the reasoning
                // that the node's path "is the type every instance of it will declare". The KEY was
                // right and the VALUE cannot be: a definition node's hub applies
                // NodeTypeNodeType's configuration — `WithContentType<NodeTypeDefinition>()` — and
                // never loads the compiled assembly of the type it declares, so the only thing it
                // can publish under its own path is NodeTypeDefinition. That is the answer to a
                // DIFFERENT question than the one the key asks: every read seam resolves by
                // `node.NodeType`, so the entry at `Store/Plugin` is what an INSTANCE of
                // Store/Plugin gets, and its content is PluginContent.
                //
                // MeshContentTypeRegistry.Register is last-writer-wins per key, so a definition
                // node cold-activating after an instance overwrote the instance's correct entry,
                // and the degrade seams then recovered instance content as the type's own
                // definition — at its unchanged Version, on every read, for the rest of the process
                // (Systemorph/MeshWeaver#1379: a paid Store package whose fulfilment read the
                // declaration, got a NodeTypeDefinition, and reported "nothing to install").
                // Instances and the schema probes already write the correct entry under exactly
                // this key, so dropping the special case loses nothing; the literal "NodeType" that
                // a definition node now stamps is itself a correct entry — a node whose NodeType is
                // "NodeType" does carry NodeTypeDefinition content.
                // Repro: NodeTypePathRegistrationTest.ActivatingANodeTypeDefinition_DoesNotClaimItsOwnPathForItsInstances.
                var nodeTypePath = enriched.NodeType;

                var defaultConfig = meshConfiguration.DefaultNodeHubConfiguration;
                var nodeConfig = enriched.HubConfiguration;
                if (defaultConfig != null)
                {
                    enriched = enriched with
                    {
                        HubConfiguration = nodeConfig != null
                            ? (Func<MessageHubConfiguration, MessageHubConfiguration>)(config =>
                                nodeConfig(defaultConfig(config).WithNodeTypePath(nodeTypePath)))
                            : config => defaultConfig(config).WithNodeTypePath(nodeTypePath)
                    };
                }
                else if (nodeConfig != null)
                {
                    enriched = enriched with
                    {
                        HubConfiguration = config => nodeConfig(config.WithNodeTypePath(nodeTypePath))
                    };
                }

                if (enriched.HubConfiguration == null)
                {
                    logger.LogWarning("No HubConfiguration resolved for node {Path} (NodeType: {NodeType})",
                        enriched.Path, enriched.NodeType);
                }

                // 🚨 The binding above is made ONCE and then PINNED by address: routing
                // short-circuits on GetHostedHub for an already-hosted address and never resolves
                // the path again, so nothing re-reads the NodeType for the hub's whole lifetime.
                // Arm the rebind watcher HERE — the single funnel every activation path (Monolith
                // routing AND MessageHubGrain) goes through — so a node that acquires or changes
                // its type recycles its hub instead of serving the wrong configuration forever
                // (issue #1104). See NodeTypeRebindWatcher for why the mesh change feed, not the
                // hub's own node stream, is the signal.
                return NodeTypeRebindWatcher.WithNodeTypeRebind(enriched, meshHub, logger);
            });
}
