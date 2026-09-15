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
/// listing and a stream read, and every caller asks only for a TOP-LEVEL create — the only create
/// that can root a partition.</para>
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
