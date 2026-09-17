using System.Globalization;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// The ONE answer to "does this NodeType own its partition?" — for a type registered in
/// <c>src/</c> AND for one declared in mesh content (<c>Crm/Client</c>), which is compiled live and
/// therefore invisible to <see cref="StaticNodeProviderExtensions.FindStaticNode"/>.
///
/// <para>🚨 <b>Why this exists.</b> Three create-path checks each asked the STATIC registry alone —
/// <see cref="RlsNodeValidator"/> (who may create a top-level instance),
/// <see cref="PartitionWriteGuardValidator"/> rule 3 (only an owning type may sit at the root) and
/// <see cref="OwnsPartitionProvisioningValidator"/> (provision the schema first). A type declared in
/// a package's content, with <c>ownsPartition: true</c> on its <see cref="NodeTypeDefinition"/>, was
/// therefore not a partition-owning type to any of them: its top-level create was refused for
/// everyone, platform admins included (memex.systemorph.com, 2026-09-15 — a new CRM client could
/// not be created by anybody). The declaration is the authority; this reads it wherever it lives.</para>
///
/// <para><b>How an in-mesh declaration is read — CQRS, both halves.</b> The definition is ONE known
/// path we GATE on, so its content comes from <c>GetMeshNodeStream</c>, which is authoritative —
/// never from a query, whose index trails the store. A point read of an ABSENT node is a framework
/// defect (a routing NotFound that opens the storm-breaker on that path), so existence is
/// established first by a <c>namespace:</c> listing of the definition's parent, matched on the
/// EXACT path. Both reads run unfiltered / as System, for the reason
/// <see cref="CreatableTypesCreationValidator"/> gives: whether a type owns a partition is the TYPE
/// AUTHOR's statement about the shape of the data, and it cannot depend on whether the caller holds a
/// read grant on the package that declares it. The caller's entitlements are judged separately.</para>
///
/// <para>🚨 The type is taken from the node being CREATED, i.e. from the caller. It is used only as
/// a literal path (<see cref="IsLiteralTypePath"/>) and matched exactly — never interpolated into a
/// <c>path:</c> term, where a wildcard or alternation could select some other type's definition.</para>
///
/// <para><b>Cost.</b> A static type answers synchronously, with no read. An in-mesh type costs a
/// listing and a stream read, and every caller of <see cref="OwnsPartition"/> asks only for a
/// TOP-LEVEL create — the only create that can root a partition. A NESTED create asks
/// <see cref="OwnsPartitionWithoutActivating"/> instead: one read of the definition's durable row,
/// never an activation of the type's hub.</para>
/// </summary>
public static class PartitionOwningTypes
{
    /// <summary>How long an in-mesh type's definition may take to answer before the check reports
    /// that it could not be established (never "not owning" — see <see cref="OwnsPartition"/>).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether <paramref name="nodeType"/> declares <see cref="NodeTypeDefinition.OwnsPartition"/>:
    /// <c>true</c> / <c>false</c> when the declaration was read, <c>null</c> when it could not be —
    /// the reads timed out or faulted.
    ///
    /// <para>🚨 Three answers, not two. Folding "could not be read" into <c>false</c> would refuse a
    /// legitimate create with a message about the type that is simply untrue; folding it into
    /// <c>true</c> would let a non-owning node land at the root. Callers answer <c>null</c> as an
    /// availability failure (<see cref="Undetermined"/>).</para>
    /// </summary>
    public static IObservable<bool?> OwnsPartition(IMessageHub hub, string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType)
            || string.Equals(nodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
            return Observable.Return<bool?>(false);

        var options = hub.JsonSerializerOptions;
        if (hub.ServiceProvider.FindStaticNode(nodeType) is { } staticType)
            return Observable.Return<bool?>(
                staticType.ContentAs<NodeTypeDefinition>(options) is { OwnsPartition: true });

        // Only a literal path can name a definition. A definition node always lives INSIDE a
        // partition (it is not itself a partition root), so a slash-less type is not an in-mesh one.
        var slash = nodeType.LastIndexOf('/');
        if (!IsLiteralTypePath(nodeType) || slash <= 0)
            return Observable.Return<bool?>(false);

        // No query core is a CONFIGURATION fact, not a read failure: such a host has no persisted
        // nodes, so a non-static type provably has no declaration there.
        var queryCore = hub.ServiceProvider.GetService<IMeshQueryCore>();
        if (queryCore is null)
            return Observable.Return<bool?>(false);
        var access = hub.ServiceProvider.GetService<AccessService>();

        return queryCore
            .Query<MeshNode>(
                MeshQueryRequest.FromQuery(
                    $"namespace:{nodeType[..slash]} nodeType:{MeshNode.NodeTypePath}"), options)
            .Where(change => change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Select(change => change.Items.Any(n =>
                string.Equals(n.Path, nodeType, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(exists => exists
                ? access
                    .RunAsSystem(() => hub.GetMeshNodeStream(nodeType).Take(1))
                    .Select(node => (bool?)(node.ContentAs<NodeTypeDefinition>(options) is { OwnsPartition: true }))
                : Observable.Return<bool?>(false))
            .Timeout(ProbeTimeout, Observable.Return<bool?>(null))
            .Catch<bool?, Exception>(_ => Observable.Return<bool?>(null));
    }

    /// <summary>
    /// <see cref="OwnsPartition"/> resolved ONCE for the whole operation
    /// (<see cref="NodeValidationContext.PartitionOwnership"/>) — the form every VALIDATOR uses.
    ///
    /// <para>The three create-path validators (<see cref="RlsNodeValidator"/>,
    /// <see cref="PartitionWriteGuardValidator"/>, <see cref="OwnsPartitionProvisioningValidator"/>)
    /// run back to back on ONE context, so asking three times cost three resolutions — six reads for
    /// an in-mesh type — of a fact that cannot meaningfully change between them. They now share one.
    /// The answer is the same tri-state, <c>null</c> included, so every caller still fails closed via
    /// <see cref="Undetermined"/>.</para>
    ///
    /// <para>🚨 <b><see cref="InMeshPartitionOwnerPostCreationHandler"/> deliberately does NOT use
    /// this.</b> It runs after the row is written, and its job is to re-establish ownership
    /// POSITIVELY before granting the creator Admin — across the one window in a create that is
    /// actually wide. Sharing the pre-write view with it would remove the only disagreement check
    /// worth keeping. See <c>Doc/Architecture/PartitionOwnershipResolution</c>.</para>
    /// </summary>
    /// <param name="hub">The hub whose services resolve the declaration.</param>
    /// <param name="context">The operation's validation context, which carries the memo.</param>
    /// <returns>The tri-state: owns / does not own / could not be established.</returns>
    public static IObservable<bool?> OwnsPartitionOnce(IMessageHub hub, NodeValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var nodeType = context.Node.NodeType;
        // An empty type answers synchronously with no read — nothing to share, and nothing a later
        // check could observe differently.
        return string.IsNullOrEmpty(nodeType)
            ? OwnsPartition(hub, nodeType)
            : context.PartitionOwnership.Once(nodeType, () => OwnsPartition(hub, nodeType));
    }

    /// <summary>
    /// Whether <paramref name="nodeType"/> declares <see cref="NodeTypeDefinition.OwnsPartition"/>,
    /// answered WITHOUT activating the type's hub — the resolution a NESTED create uses to refuse an
    /// instance of an owning type below the root (#4449 item 1).
    ///
    /// <para>🚨 <b>Why not <see cref="OwnsPartition"/>.</b> Its second half is
    /// <c>GetMeshNodeStream(&lt;type&gt;)</c>, and for a per-node hub the read IS the activation: a cold
    /// NodeType compiles, which can outlast <see cref="ProbeTimeout"/>, and a timeout fails closed.
    /// The top-level path accepts that — a top-level create of an owning type is a rare
    /// partition-creation act. A nested create of a type declared in mesh content is the ordinary
    /// content path, where it would put an intermittent refusal, triggered by whether a hub happened
    /// to be warm, in front of routine work.</para>
    ///
    /// <para>🚨 <b>The denominator is complete by construction, which is what makes this fail-closed
    /// without being fail-intermittent.</b> The answer comes from exactly the two sources
    /// <see cref="NodeTypeResolution.Resolves"/> consults, in the same order: the static registry,
    /// then the DURABLE row (<see cref="IStorageAdapter.ReadMany"/>, the repair-free read seam). A
    /// type in neither is refused as unregistered by the create's existence check, so every type a
    /// create can land with has a row this read reaches — from every process, whichever hubs are
    /// warm. A hub-fed projection (the <see cref="NodeTypeInstanceLocations"/> shape) cannot say
    /// that: its denominator is the hubs live on this process. See
    /// <c>Doc/Architecture/PartitionOwnershipResolution</c>.</para>
    ///
    /// <para>🚨 <b>What "without activating" does and does not claim</b> (review on #4589). It
    /// claims the TYPE's own per-node hub — the one whose activation COMPILES the NodeType — is
    /// never addressed. It does not claim the read is process-local: where partition storage hubs
    /// are configured the store itself is reached by a routed request to the partition's storage
    /// hub. That hub is the one <see cref="NodeTypeResolution.Resolves"/> asks moments later through
    /// the same adapter, so this check adds no activation and no availability dependency the create
    /// did not already have.</para>
    ///
    /// <list type="bullet">
    ///   <item><c>true</c> — the static or stored definition declares <c>ownsPartition</c>.</item>
    ///   <item><c>false</c> — it declares otherwise; the row is not a NodeType definition; the row is
    ///     ABSENT (a verdict the store gave — the existence check then refuses the type as
    ///     unregistered, which is the true reason); or the host has no storage adapter (a
    ///     configuration fact: such a host persists nothing, and refuses every non-static type).</item>
    ///   <item><c>null</c> — the store faulted or did not answer within <see cref="ProbeTimeout"/>.
    ///     The caller refuses as <see cref="Undetermined"/>: the same store answers the existence
    ///     probe a moment later, so this adds no availability dependency the create did not have.</item>
    /// </list>
    ///
    /// <para>Unlike <see cref="OwnsPartition"/> the type is never matched against
    /// <see cref="IsLiteralTypePath"/>: it is not interpolated into a query here, only handed to the
    /// store as a key, and answering <c>false</c> for a non-literal type would let a nested instance
    /// of such a type through — the fail-OPEN direction.</para>
    /// </summary>
    /// <param name="hub">The hub whose services resolve the declaration.</param>
    /// <param name="nodeType">The created node's NodeType.</param>
    /// <returns>The tri-state: owns / does not own / could not be established.</returns>
    public static IObservable<bool?> OwnsPartitionWithoutActivating(IMessageHub hub, string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType)
            || string.Equals(nodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
            return Observable.Return<bool?>(false);

        var options = hub.JsonSerializerOptions;
        if (hub.ServiceProvider.FindStaticNode(nodeType) is { } staticType)
            return Observable.Return<bool?>(
                staticType.ContentAs<NodeTypeDefinition>(options) is { OwnsPartition: true });

        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Observable.Return<bool?>(false);

        // 🚨 ReadMany, NOT Read — the seam that carries no repair (review on #4589). Where partition
        // storage hubs are configured, IStorageAdapter is RoutingProxyAdapter and its `Read` is
        // wrapped in LegacyUserPartitionRepair: an absent row at a partition-root-shaped path
        // triggers a legacy-twin probe and can durably WRITE a repaired root. An ownership CHECK may
        // not write anything. Both that proxy and PersistenceService override `ReadMany` for exactly
        // that reason ("the repair is for a bare partition-ROOT point read"), so this asks the store
        // the same question with no side effect; a path the store does not hold is simply absent.
        return Observable.Defer(() => storage.ReadMany([nodeType], options))
            .Take(1)
            .Select(row => (bool?)DeclaresOwnership(row, options))
            // ABSENT is the store's verdict, not a missing answer: there is no row, so nothing
            // declares ownership, and the create's own existence check refuses an unregistered type
            // next — with the true reason. A FAULT is the other case and is caught below.
            .DefaultIfEmpty(false)
            .Timeout(ProbeTimeout, Observable.Return<bool?>(null))
            .Catch<bool?, Exception>(_ => Observable.Return<bool?>(null));
    }

    /// <summary>Whether a durable row is a NodeType definition that declares ownership. Pure.</summary>
    private static bool DeclaresOwnership(MeshNode? row, System.Text.Json.JsonSerializerOptions options) =>
        row is not null
        && string.Equals(row.NodeType, MeshNode.NodeTypePath, StringComparison.OrdinalIgnoreCase)
        && row.ContentAs<NodeTypeDefinition>(options) is { OwnsPartition: true };

    /// <summary>
    /// The refusal of a NESTED instance of a partition-owning type — registered in <c>src/</c> or
    /// declared in mesh content. An owning instance IS a partition root, so its path is just its id.
    /// <see cref="NodeRejectionReason.InvalidPath"/>, which the importer classifies as a verdict about
    /// the content (the same bytes break it on every pass). Worded in the CALLER's language
    /// (<see cref="AccessContext.Locale"/>).
    /// </summary>
    /// <param name="context">The create being refused.</param>
    /// <returns>The refusal.</returns>
    public static NodeValidationResult NestedInstanceRefused(NodeValidationContext context) =>
        NodeValidationResult.Invalid(
            LocalizationCatalog.Get(
                "access.partitionCreate.nestedOwningType", context.AccessContext?.Locale,
                context.Node.Path, context.Node.NodeType, context.Node.Id, context.Node.Namespace),
            NodeRejectionReason.InvalidPath);

    /// <summary>A literal NodeType path: ASCII letters, digits, <c>. _ -</c> and single inner
    /// slashes — nothing a query parser could read as a wildcard, alternation or separator. Pure.</summary>
    public static bool IsLiteralTypePath(string path) =>
        path.Length > 0
        && path[0] != '/' && path[^1] != '/'
        && !path.Contains("//", StringComparison.Ordinal)
        && path.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '/' or '.' or '_' or '-');

    /// <summary>
    /// The result when <see cref="OwnsPartition"/> answered <c>null</c>: an availability failure,
    /// NOT a decision — the create was not evaluated and may be retried. Fail-closed all the same.
    /// Worded in the CALLER's language (<see cref="AccessContext.Locale"/>).
    /// </summary>
    public static NodeValidationResult Undetermined(NodeValidationContext context) =>
        NodeValidationResult.Unavailable(LocalizationCatalog.Get(
            "access.partitionCreate.undetermined", context.AccessContext?.Locale,
            context.Node.NodeType,
            ProbeTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture),
            context.Node.Path));
}
