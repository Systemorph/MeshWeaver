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
/// <para><b>Cost.</b> A static type answers synchronously, with no read. An in-mesh type costs ONE
/// anchored query of its definition node, and every caller asks only for a TOP-LEVEL create — the
/// only create that can root a partition — so nested creates of in-mesh types never pay it.</para>
///
/// <para>🚨 The read goes through <see cref="IMeshQueryCore"/>, i.e. NOT access-filtered, for the
/// reason <see cref="CreatableTypesCreationValidator"/> gives: whether a type owns a partition is
/// the TYPE AUTHOR's statement about the shape of the data, and it cannot depend on whether the
/// caller happens to hold a read grant on the package that declares it. The caller's entitlements
/// are judged separately, on the same create.</para>
/// </summary>
public static class PartitionOwningTypes
{
    /// <summary>How long an in-mesh type's definition may take to answer before the check reports
    /// that it could not be established (never "not owning" — see <see cref="OwnsPartition"/>).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether <paramref name="nodeType"/> declares <see cref="NodeTypeDefinition.OwnsPartition"/>:
    /// <c>true</c> / <c>false</c> when the declaration was read, <c>null</c> when it could not be —
    /// the read timed out or faulted.
    ///
    /// <para>🚨 Three answers, not two. Folding "could not be read" into <c>false</c> would refuse a
    /// legitimate create with a message about the type ("does not own a partition") that is simply
    /// untrue; folding it into <c>true</c> would let a non-owning node land at the root. Callers
    /// answer <c>null</c> as an availability failure (<see cref="Undetermined"/>).</para>
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

        // No query core is a CONFIGURATION fact, not a read failure: such a host has no persisted
        // nodes, so a non-static type provably has no declaration there.
        var queryCore = hub.ServiceProvider.GetService<IMeshQueryCore>();
        if (queryCore is null)
            return Observable.Return<bool?>(false);

        // ONE ANCHORED QUERY, never a point read: the type node may not exist, and a point read of an
        // absent node answers a routing NotFound that opens the storm-breaker on that path.
        return queryCore
            .Query<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{nodeType} nodeType:{MeshNode.NodeTypePath}"), options)
            .Take(1)
            .Select(change => (bool?)(change.Items.FirstOrDefault()
                ?.ContentAs<NodeTypeDefinition>(options) is { OwnsPartition: true }))
            .Timeout(ProbeTimeout, Observable.Return<bool?>(null))
            .Catch<bool?, Exception>(_ => Observable.Return<bool?>(null));
    }

    /// <summary>
    /// The result when <see cref="OwnsPartition"/> answered <c>null</c>: an availability failure,
    /// NOT a decision — the create was not evaluated and may be retried. Fail-closed all the same.
    /// </summary>
    public static NodeValidationResult Undetermined(NodeValidationContext context) =>
        NodeValidationResult.Unavailable(
            $"Whether '{context.Node.NodeType}' owns its partition could not be established: its "
            + $"NodeType definition did not answer within {ProbeTimeout.TotalSeconds:0}s. The create of "
            + $"'{context.Node.Path}' was not evaluated and may be retried.");
}
