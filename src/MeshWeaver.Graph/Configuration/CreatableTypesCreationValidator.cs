using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>The create BOUNDARY's half of <c>CreatableTypes</c> (MeshWeaver#4077).</b> Until this
/// validator, a parent NodeType's <see cref="NodeTypeDefinition.CreatableTypes"/> whitelist was
/// honoured by the Create FORM alone (<c>CreateLayoutArea</c> → <see cref="ICreatableTypesProvider"/>),
/// and the authoritative write boundary — <c>CreateNodeRequest</c> in <c>MeshExtensions</c> — asked
/// nothing about it. A caller posting a create directly, an agent tool call, or a client forging the
/// layout area's <c>/data/{form}/type</c> value could write any type the parent did not list. The
/// menu honoured the declaration and the write ignored it.
///
/// <para><b>What this refuses, and only this.</b> A create whose <see cref="MeshNode.Namespace"/>
/// resolves to a node whose NodeType declares an EXPLICIT <c>CreatableTypes</c> list that does not
/// contain the incoming <see cref="MeshNode.NodeType"/> (globals riding along unless the parent set
/// <see cref="NodeTypeDefinition.IncludeGlobalTypes"/> to false). Everything else passes.</para>
///
/// <para>🚨 <b>Why the allowed set needs no discovery query.</b> A whitelist only ever NARROWS
/// auto-discovery — <c>CreatableTypesProvider.BuildInfos</c> filters the query rows and the static
/// bucket down to it — so when one is declared the offered set is exactly
/// <c>CreatableTypes ∪ (globals when IncludeGlobalTypes)</c>, computable from the parent's
/// definition alone. That collapses the cost the issue feared (a discovery fan-out per create) to at
/// most two anchored reads, and only for a parent that actually declares a list.</para>
///
/// <para>🚨 <b>THE OPT-IN IS THE DECLARATION, and that is what keeps the blast radius at zero for
/// everything that exists today.</b> "A parent that declares nothing restricts nothing" is the
/// documented default (<c>Doc/Architecture/CreatableTypes</c>) and stays literally true here: with
/// <c>CreatableTypes</c> absent this validator is a no-op. It is not a host switch, because a host
/// switch would make the same declaration mean two different things on two portals.</para>
///
/// <para>🚨 <b>The import/sync bypass is the SYSTEM identity, not a request flag.</b> Curation
/// describes what a PERSON may create through the product; the platform's own writers — the package
/// installer, GitSync, plugin installs, migrations, repair services, seed providers — are not
/// curated, and they are exactly the writers that fan a create out over hundreds of paths. Keying
/// the bypass on <see cref="WellKnownUsers.System"/> / a hub credential therefore does two jobs at
/// once: it keeps a whitelist from refusing an import that is legal today (the failure the issue
/// named as "would look like corruption"), and it keeps the per-create read off the bulk path
/// entirely. A request flag could not do either — the bulk verb rebuilds an inner
/// <c>CreateNodeRequest</c> per node (<c>MeshExtensions</c> phase 3), so an outer flag does not
/// survive the fan-out.</para>
///
/// <para>🚨 <b>Sibling satellites are exempt</b> (<see cref="SatelliteTableMapping.IsSiblingSatellite"/>).
/// <c>{parent}/_Access</c>, <c>{parent}/_GitSync</c>, <c>{parent}/_Policy</c>, <c>_Entitlements</c> …
/// are governance bookkeeping filed NEXT TO a node, not content created UNDER it. A whitelist curates
/// the latter; refusing the former would break the partition bootstrap on any type that declares one.</para>
///
/// <para>🚨 <b>THE FAIL-OPEN DECISION, recorded (issue #4077 item 3).</b> When the parent — or the
/// parent's type definition — cannot be READ, this validator ALLOWS the create and logs a warning
/// naming the path. That is deliberately the opposite of the house default for a validator
/// (<see cref="NodeRejectionReason.Unavailable"/>, #1446, which fails closed), and the reason is
/// that this control is CURATION and not access control: <c>Permission.Create</c> is what actually
/// gates creation, it runs on the same boundary, and it already fails closed. Failing closed HERE
/// would add nothing to the security posture and would convert a transient read failure into "no
/// creates under this parent at all" — an availability incident wearing a policy decision's clothes,
/// which is the very collapse <c>Unavailable</c> exists to prevent. The asymmetry with
/// <c>CreatableTypesProvider.BuildInfoFromConfig</c> (which fails CLOSED on an unprobed declaration)
/// is intentional and is the same rule applied honestly to two different consequences: there,
/// failing closed means "do not OFFER a type", which costs a user one menu entry; here it would mean
/// "refuse a WRITE".</para>
///
/// <para>The refusal message is English-only, for the reason <c>MeshExtensions</c> already records at
/// the create-rejection site: <c>CreateNodeResponse.Fail</c> carries no <c>ActivityLog</c>, so this
/// path has no keyed surface to render a localized refusal into (MeshWeaver#3917).</para>
/// </summary>
internal sealed class CreatableTypesCreationValidator(
    IMessageHub hub,
    MeshConfiguration meshConfiguration,
    ILogger<CreatableTypesCreationValidator>? logger) : INodeValidator
{
    /// <summary>Create only — an update never moves a node between namespaces.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Create];

    /// <summary>How long one anchored probe may take before it counts as NOT ESTABLISHED. Same
    /// bound the sibling reads in <c>CreatableTypesProvider</c> use for a single lookup.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private static readonly IObservable<NodeValidationResult> Pass =
        Observable.Return(NodeValidationResult.Valid());

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var node = context.Node;
        if (node is null)
            return Pass;

        // The platform's own writers are not curated — see the class remarks. Checked FIRST so a
        // bulk install never pays a read.
        if (IsPlatformWriter(context.AccessContext))
            return Pass;

        // A root-level create has no parent node, so no declaration governs it.
        if (string.IsNullOrEmpty(node.Namespace))
            return Pass;

        // Bookkeeping filed beside a node, not content created under it.
        if (SatelliteTableMapping.IsSiblingSatellite(node)
            || SatelliteTableMapping.IsSatellitePath(node.Path))
            return Pass;

        // 🚨 An untyped node names no type, so a whitelist of TYPES has nothing to say about it —
        // and an untyped node is legal everywhere (NodeTypeResolution.Resolves returns true for an
        // empty type). Refusing it would turn "which types may be created here" into "nothing may be
        // created here", which is not what the declaration says.
        if (string.IsNullOrEmpty(node.NodeType))
            return Pass;

        return ResolveParentDefinition(node.Namespace)
            .Select(outcome => Decide(node, outcome));
    }

    private NodeValidationResult Decide(MeshNode node, ParentOutcome outcome)
    {
        if (!outcome.Established)
        {
            logger?.LogWarning(
                "[CreatableTypes] the parent of '{Path}' ('{Namespace}') could not be read, so the "
                + "parent type's CreatableTypes declaration was NOT applied to this create. The "
                + "create PROCEEDS: CreatableTypes is curation, and Permission.Create — which did "
                + "run — is what gates creation. See Doc/Architecture/CreatableTypes.",
                node.Path, node.Namespace);
            return NodeValidationResult.Valid();
        }

        var declared = outcome.Definition?.CreatableTypes;
        if (declared is null)
            return NodeValidationResult.Valid();

        var allowed = new HashSet<string>(declared, StringComparer.OrdinalIgnoreCase);
        if (outcome.Definition!.IncludeGlobalTypes)
            foreach (var global in meshConfiguration.GlobalCreatableTypes)
                allowed.Add(global);

        if (allowed.Contains(node.NodeType!))
            return NodeValidationResult.Valid();

        return NodeValidationResult.Invalid(
            RejectionMessage(node.Path, node.NodeType!, outcome.ParentType!, allowed),
            NodeRejectionReason.InvalidNodeType);
    }

    /// <summary>
    /// The refusal, written so a caller who reads it once has learned the rule: it names the type it
    /// refused, the parent TYPE that declared the restriction (never the parent instance — the
    /// declaration is the type author's, and that is where the repair is made), and the set that IS
    /// allowed.
    /// </summary>
    internal static string RejectionMessage(
        string path, string nodeType, string parentType, IReadOnlySet<string> allowed) =>
        $"NodeType '{nodeType}' may not be created at '{path}': the parent's NodeType "
        + $"'{parentType}' declares CreatableTypes, and that list does not include it. Allowed "
        + $"here: [{string.Join(", ", allowed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}]. "
        + "Create one of those, create it somewhere the type is allowed, or add it to "
        + $"'{parentType}'s CreatableTypes. (See Doc/Architecture/CreatableTypes — this is the "
        + "type author's curation, not a permission: it is unrelated to what you are allowed to do.)";

    /// <summary>
    /// The platform's own writers: the system identity (<see cref="WellKnownUsers.System"/> — every
    /// <c>ImpersonateAsSystem</c> writer: installer, GitSync, migrations, repair services) and a hub
    /// credential (<c>ImpersonateAsHub</c> — framework plumbing writing its own state).
    ///
    /// <para>A NULL context is exempt too, and that is not a hole: <c>PostPipeline</c> fails closed
    /// when no context is set, so a create that reaches a validator with none came through a
    /// framework seam that is not a person, and a curation check must not be the thing that decides
    /// an unauthenticated write.</para>
    /// </summary>
    private static bool IsPlatformWriter(AccessContext? accessContext) =>
        accessContext is null
        || accessContext.IsHub
        || string.Equals(accessContext.ObjectId, WellKnownUsers.System, StringComparison.Ordinal);

    /// <summary>The parent's type and its definition, plus whether the reads actually ANSWERED.
    /// A non-answer is never folded into "no declaration" — see the fail-open remark on the class:
    /// the two lead to the same outcome but only one of them logs, and telling them apart is what
    /// makes the log honest.</summary>
    private readonly record struct ParentOutcome(
        bool Established, string? ParentType, NodeTypeDefinition? Definition)
    {
        public static readonly ParentOutcome NotEstablished = new(false, null, null);
        public static readonly ParentOutcome NoDeclaration = new(true, null, null);
    }

    private IObservable<ParentOutcome> ResolveParentDefinition(string parentPath)
    {
        // 1. The static registry answers for free and without a read.
        if (hub.ServiceProvider.FindStaticNode(parentPath) is { } staticParent)
            return ResolveDefinition(staticParent.NodeType);

        // 2. 🚨 ONE ANCHORED QUERY, never a point read. The parent MAY NOT EXIST — a create under a
        //    namespace whose node was never materialised is legal — and a point read of an absent
        //    node answers a routing NotFound that terminates the stream AND opens the storm-breaker
        //    on that path, which fast-fails the very WRITE this validator is gating
        //    (Doc/Architecture/CqrsAndContentAccess). A `path:` query answers zero rows instead. One
        //    path per query keeps it ANCHORED on its own partition (#3202).
        return QueryOne($"path:{parentPath}")
            .SelectMany(probe => probe.Established
                ? ResolveDefinition(probe.Node?.NodeType)
                : Observable.Return(ParentOutcome.NotEstablished));
    }

    private IObservable<ParentOutcome> ResolveDefinition(string? parentType)
    {
        if (string.IsNullOrEmpty(parentType)
            || string.Equals(parentType, MeshNode.NodeTypePath, StringComparison.Ordinal))
            return Observable.Return(ParentOutcome.NoDeclaration);

        if (hub.ServiceProvider.FindStaticNode(parentType) is { } staticType)
            return Observable.Return(new ParentOutcome(
                true, parentType, staticType.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions)));

        return QueryOne($"path:{parentType} nodeType:NodeType")
            .Select(probe => probe.Established
                ? new ParentOutcome(
                    true, parentType,
                    probe.Node.ContentAs<NodeTypeDefinition>(hub.JsonSerializerOptions))
                : ParentOutcome.NotEstablished);
    }

    /// <summary>
    /// One anchored query, folded into (answered?, first row).
    ///
    /// <para>🚨 <b><see cref="IMeshQueryCore"/>, i.e. NOT access-filtered</b>, for the same reason
    /// <see cref="CreatableTypesProvider"/> uses it: whether a type may be created under a parent is
    /// the TYPE AUTHOR's statement about the shape of the data, and it cannot depend on whether this
    /// particular caller happens to hold a read grant on the parent node. A filtered read would make
    /// the declaration lapse for exactly the callers who can see least — a curation rule that varies
    /// by reader is not a rule. The caller's entitlements are judged, on this same boundary, by
    /// <c>Permission.Create</c>.</para>
    ///
    /// <para>🚨 A probe that TIMED OUT or FAULTED is not an answer — folding it into the
    /// empty-result case would make a storage hiccup lapse the declaration silently, which is the
    /// one failure mode a curation control must at least be loud about.
    /// <see cref="ParentOutcome.NotEstablished"/> carries that apart.</para>
    /// </summary>
    private IObservable<(bool Established, MeshNode? Node)> QueryOne(string query)
    {
        var queryCore = hub.ServiceProvider.GetService<IMeshQueryCore>();
        // No query core at all is a CONFIGURATION fact, not a read failure: such a host has no
        // persisted nodes, so a non-static parent provably has no declaration. Recorded as ANSWERED.
        if (queryCore is null)
            return Observable.Return((true, (MeshNode?)null));

        return queryCore
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query), hub.JsonSerializerOptions)
            .Take(1)
            .Select(change => (true, change.Items.FirstOrDefault()))
            .Timeout(ProbeTimeout, Observable.Return((false, (MeshNode?)null)))
            .Catch<(bool, MeshNode?), Exception>(_ => Observable.Return((false, (MeshNode?)null)));
    }
}
