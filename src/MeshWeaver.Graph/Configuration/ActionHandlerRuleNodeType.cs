using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.ActionLinks;

/// <summary>
/// The <c>ActionHandlerRule</c> node type — the durable entries of a portal's action-handler chain
/// (<see cref="ActionHandlerRule"/>). Doc/Architecture/ActionLinkContracts → "Handlers are rule
/// nodes".
/// </summary>
public static class ActionHandlerRuleNodeType
{
    /// <summary>The NodeType value of a rule node.</summary>
    public const string NodeType = nameof(ActionHandlerRule);

    /// <summary>Registers the node type and makes its content type known to every hub.</summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <returns>The builder.</returns>
    public static TBuilder AddActionHandlerRuleType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(new MeshNode(NodeType)
        {
            Name = "Action Handler Rule",
            HubConfiguration = config => config
                .AddMeshDataSource(source => source.WithContentType<ActionHandlerRule>()),
        });
        // Platform plumbing, not pickable content.
        builder.AddAutocompleteExcludedTypes(NodeType);
        // 🚨 Every hub, not only the rule's own: the chain is read from whatever hub resolves an
        // invocation, and a reader whose TypeRegistry lacks the type gets a raw JsonElement.
        builder.WithMeshType(typeof(ActionHandlerRule), nameof(ActionHandlerRule));
        return builder;
    }
}

/// <summary>
/// The ONE live read of the action-handler chain, and the resolution over it — so no publisher
/// names an implementation, and every one answers "nothing claims this key" the same way.
/// </summary>
public static class ActionHandlerRuleExtensions
{
    private const string QueryId = "action-handler-rules";

    /// <summary>
    /// The handler chain, LIVE: every <c>ActionHandlerRule</c> node in the mesh, bound by
    /// <see cref="ActionHandlerRules.Bind"/>, re-emitted whenever a rule is added, changed or
    /// removed — a rule written while the portal runs is in the next emission, with no recycle.
    ///
    /// <para>One synced query (<c>hub.GetQuery</c>), shared by every caller, read as SYSTEM: which
    /// handler acts for a key is platform configuration, never a function of who happens to ask.
    /// Rules are authored wherever their package lives, so the read is mesh-wide; what keeps one
    /// partition's rule from routing into another's content is the binding's own-partition check.</para>
    /// </summary>
    /// <param name="hub">The hub reading the chain; its serializer options type the rule content.</param>
    /// <returns>A live stream of chain snapshots.</returns>
    public static IObservable<ActionHandlerChain> ActionHandlerChain(this IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var access = hub.ServiceProvider.GetService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ActionHandlerRuleExtensions));
        return access.RunAsSystem(() => hub.GetQuery(QueryId, MeshWideQuery.OfType(ActionHandlerRuleNodeType.NodeType)))
            .Select(nodes => ActionHandlerRules.Bind(nodes
                .Where(node => !string.IsNullOrEmpty(node.Path))
                .Select(node => (Path: node.Path!, Rule: node.ContentAs<ActionHandlerRule>(hub.JsonSerializerOptions, logger)))));
    }

    /// <summary>
    /// The handler for <paramref name="invocation"/> on this mesh, or the named outcome that says
    /// why there is none — resolved over the CURRENT chain (<see cref="ActionHandlerChain(IMessageHub)"/>).
    ///
    /// <para>🚨 The chain's terminal is LOUD: an unclaimed key resolves to
    /// <see cref="ActionOutcomeStatus.UnknownHandler"/> whose detail names how many rules were read
    /// and every rule that was refused, and it is logged at Warning with the key — an unclaimed key
    /// is the signal that a package is missing or its rule is broken. A refused rule is logged even
    /// when another rule resolves the key.</para>
    ///
    /// <para>Cold: the read runs on Subscribe and emits one resolution. A chain that cannot be read
    /// errors the observable; it never answers "unknown" for a read that did not happen.</para>
    /// </summary>
    /// <param name="hub">The hub reading the chain.</param>
    /// <param name="invocation">The invocation to resolve.</param>
    /// <returns>A cold observable emitting one resolution.</returns>
    public static IObservable<ActionHandlerResolution> ResolveActionHandler(this IMessageHub hub, ActionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(invocation);
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ActionHandlerRuleExtensions));
        return hub.ActionHandlerChain()
            .Take(1)
            .Select(chain =>
            {
                foreach (var refused in chain.Rejected)
                    logger?.LogWarning("Action handler rule {Path} refused: {Reason}", refused.Path, refused.Reason);
                var resolution = chain.Resolve(invocation);
                if (resolution.Outcome.Status == ActionOutcomeStatus.UnknownHandler)
                    logger?.LogWarning(
                        "No action handler rule claims key {HandlerKey} version {HandlerVersion}: {Detail}",
                        invocation.HandlerKey, invocation.HandlerVersion, resolution.Outcome.Detail);
                return resolution;
            });
    }
}
