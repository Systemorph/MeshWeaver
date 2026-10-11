using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// The configuration key listing further namespaces whose rules count — deployment-record
    /// configuration (<c>ActionLinks:RuleNamespaces:0</c>, <c>:1</c>, …), read on every chain read.
    /// </summary>
    public const string RuleNamespacesConfigKey = "ActionLinks:RuleNamespaces";

    /// <summary>
    /// The platform's own namespace for rules: <c>Admin</c>, where platform configuration lives and
    /// only a platform admin may write.
    /// </summary>
    public const string PlatformNamespace = "Admin";

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
        // Which namespaces' rules COUNT is declared, never derived from where a node happens to
        // be written: the platform's own namespace plus configuration here; each installed
        // package's own partition is contributed by the package installer's source.
        builder.DeclareActionHandlerRuleNamespaces(PlatformNamespace);
        builder.ConfigureServices(services => services
            .AddSingleton<IActionHandlerRuleNamespaceSource, PlatformActionHandlerRuleNamespaces>()
            .AddSingleton<ActionHandlerRuleRefusalLog>());
        return builder;
    }

    /// <summary>
    /// Declares namespaces whose <c>ActionHandlerRule</c> nodes count, as part of the platform's own
    /// configuration. A rule anywhere else is refused as
    /// <see cref="ActionHandlerRules.UndeclaredNamespace"/> and reported.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="namespaces">The namespaces (first path segments).</param>
    /// <returns>The builder.</returns>
    public static TBuilder DeclareActionHandlerRuleNamespaces<TBuilder>(this TBuilder builder, params string[] namespaces)
        where TBuilder : MeshBuilder
    {
        ArgumentNullException.ThrowIfNull(namespaces);
        var declaration = new ActionHandlerRuleNamespaceDeclaration([.. namespaces]);
        builder.ConfigureServices(services => services.AddSingleton(declaration));
        return builder;
    }
}

/// <summary>
/// A contributor to the set of namespaces whose <c>ActionHandlerRule</c> nodes count. The platform's
/// own (<see cref="ActionHandlerRuleNodeType.DeclareActionHandlerRuleNamespaces{TBuilder}"/> and
/// <see cref="ActionHandlerRuleNodeType.RuleNamespacesConfigKey"/>) is one; the package installer
/// contributes each installed package's own partition from its install records. A source is LIVE:
/// it re-emits when its declaration changes, so a newly installed package's rules count with no
/// recycle.
/// </summary>
public interface IActionHandlerRuleNamespaceSource
{
    /// <summary>The namespaces this source declares, live. Must emit a first value.</summary>
    /// <param name="hub">The hub reading the chain.</param>
    /// <returns>A live stream of namespace sets.</returns>
    IObservable<IEnumerable<string>> Namespaces(IMessageHub hub);
}

/// <summary>One platform declaration of rule namespaces (<see cref="ActionHandlerRuleNodeType.DeclareActionHandlerRuleNamespaces{TBuilder}"/>).</summary>
/// <param name="Namespaces">The declared namespaces.</param>
public sealed record ActionHandlerRuleNamespaceDeclaration(ImmutableArray<string> Namespaces);

/// <summary>
/// The platform's declared rule namespaces: every <see cref="ActionHandlerRuleNamespaceDeclaration"/>
/// plus <see cref="ActionHandlerRuleNodeType.RuleNamespacesConfigKey"/>.
/// </summary>
internal sealed class PlatformActionHandlerRuleNamespaces(
    IEnumerable<ActionHandlerRuleNamespaceDeclaration> declarations,
    IConfiguration? configuration = null) : IActionHandlerRuleNamespaceSource
{
    public IObservable<IEnumerable<string>> Namespaces(IMessageHub hub) =>
        Observable.Defer(() => Observable.Return<IEnumerable<string>>(declarations
            .SelectMany(d => d.Namespaces.IsDefault ? [] : d.Namespaces)
            .Concat(configuration?.GetSection(ActionHandlerRuleNodeType.RuleNamespacesConfigKey)
                        .GetChildren()
                        .Select(child => child.Value)
                        .OfType<string>()
                    ?? [])
            .ToImmutableArray()));
}

/// <summary>
/// Logs each refused rule ONCE per mesh, at Warning — a mesh-scoped singleton, so the memory of
/// what was already reported lives and dies with the mesh (Doc/Architecture/NoStaticState).
/// </summary>
internal sealed class ActionHandlerRuleRefusalLog(ILoggerFactory? loggerFactory = null)
{
    private readonly ConcurrentDictionary<RejectedActionHandlerRule, byte> reported = new();
    private readonly ILogger? logger = loggerFactory?.CreateLogger(typeof(ActionHandlerRuleExtensions));

    public void Report(ActionHandlerChain chain)
    {
        foreach (var refused in chain.Rejected)
            if (reported.TryAdd(refused, 0))
                logger?.LogWarning(
                    "Action handler rule {Path} refused: {Reason} (declared rule namespaces: {Declared})",
                    refused.Path, refused.Reason, string.Join(", ", chain.DeclaredNamespaces));
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
    /// <see cref="ActionHandlerRules.Bind"/> against the DECLARED namespaces, re-emitted whenever a
    /// rule is added, changed or removed and whenever a namespace is declared (a package installed)
    /// — either takes effect in the next emission, with no recycle.
    ///
    /// <para>One synced query (<c>hub.GetQuery</c>), shared by every caller, read as SYSTEM: which
    /// handler acts for a key is platform configuration, never a function of who happens to ask.</para>
    ///
    /// <para>🚨 <b>Only rules in a declared namespace count</b> — the platform's own and each
    /// installed package's own (<see cref="IActionHandlerRuleNamespaceSource"/>). A rule written
    /// anywhere else is refused as <see cref="ActionHandlerRules.UndeclaredNamespace"/>, named in
    /// <see cref="ActionHandlerChain.Rejected"/> and logged once at Warning — never silently
    /// dropped. Create rights in some partition are not a declaration.</para>
    /// </summary>
    /// <param name="hub">The hub reading the chain; its serializer options type the rule content.</param>
    /// <returns>A live stream of chain snapshots.</returns>
    public static IObservable<ActionHandlerChain> ActionHandlerChain(this IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var access = hub.ServiceProvider.GetService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ActionHandlerRuleExtensions));
        var refusals = hub.ServiceProvider.GetRequiredService<ActionHandlerRuleRefusalLog>();
        var rules = access.RunAsSystem(() => hub.GetQuery(QueryId, MeshWideQuery.OfType(ActionHandlerRuleNodeType.NodeType)));
        return rules
            .CombineLatest(hub.DeclaredActionHandlerRuleNamespaces(), (nodes, declared) => ActionHandlerRules.Bind(
                nodes
                    .Where(node => !string.IsNullOrEmpty(node.Path))
                    .Select(node => (Path: node.Path!, Rule: node.ContentAs<ActionHandlerRule>(hub.JsonSerializerOptions, logger))),
                declared))
            .Do(refusals.Report);
    }

    /// <summary>
    /// The namespaces whose rules count, LIVE — the union of every
    /// <see cref="IActionHandlerRuleNamespaceSource"/>, re-emitted when any of them changes.
    /// </summary>
    /// <param name="hub">The hub reading the chain.</param>
    /// <returns>A live stream of the declared namespace set.</returns>
    public static IObservable<ImmutableSortedSet<string>> DeclaredActionHandlerRuleNamespaces(this IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var sources = hub.ServiceProvider.GetServices<IActionHandlerRuleNamespaceSource>()
            .Select(source => source.Namespaces(hub))
            .ToImmutableArray();
        var empty = ImmutableSortedSet.Create<string>(StringComparer.OrdinalIgnoreCase);
        return sources.IsEmpty
            ? Observable.Return(empty)
            : sources.CombineLatest(sets => sets
                .SelectMany(set => set)
                .Where(ns => !string.IsNullOrWhiteSpace(ns))
                .Select(ns => ns.Trim().Trim('/'))
                .ToImmutableSortedSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The handler for <paramref name="invocation"/> on this mesh, or the named outcome that says
    /// why there is none — resolved over the CURRENT chain (<see cref="ActionHandlerChain(IMessageHub)"/>).
    ///
    /// <para>🚨 The chain's terminal is LOUD: an unclaimed key resolves to
    /// <see cref="ActionOutcomeStatus.UnknownHandler"/> whose detail names how many rules were read,
    /// the declared namespaces, and every rule that was refused (an undeclared one included), and it
    /// is logged at Warning with the key. A refused rule is logged once per mesh, even when another
    /// rule resolves the key.</para>
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
                var resolution = chain.Resolve(invocation);
                if (resolution.Outcome.Status == ActionOutcomeStatus.UnknownHandler)
                    logger?.LogWarning(
                        "No action handler rule claims key {HandlerKey} version {HandlerVersion}: {Detail}",
                        invocation.HandlerKey, invocation.HandlerVersion, resolution.Outcome.Detail);
                return resolution;
            });
    }
}
