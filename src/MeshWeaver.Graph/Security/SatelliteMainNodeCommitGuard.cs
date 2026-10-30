using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// 🛡️ The satellite rule's update half (<see cref="SatelliteAccessRule"/>, <see cref="MainNodeTrust.Repoints"/>)
/// at the owner's COMMIT point, against the merged node and the one it replaces. A routed write — a
/// merge patch from <c>GetMeshNodeStream(path).Update</c>, an upsert's update branch, a data change —
/// reaches the owning hub, whose delivery check decides access from the STORED node; the proposal's
/// <c>MainNode</c> is seen only here. So an update that repoints a satellite's main node is refused here
/// for every writer except System, whatever route it came through.
///
/// <para><b>Refused, not re-validated:</b> a commit guard is synchronous and must not read or wait, so it
/// cannot ask the permission fold about a new target. Refusing is also the rule itself — an update never
/// chooses a satellite's main node, the same way a create never trusts a proposed one (#7601). The one
/// change allowed is the path-derived normalisation of an unset self-default to the satellite's owner.</para>
///
/// <para>A node is a satellite here when its node type is governed by <see cref="SatelliteAccessRule"/>
/// or is one of the standard satellite types (<see cref="PartitionDefinition.IsSatelliteNodeType"/>,
/// which also covers a module's satellite types whose rule the rule set hands out behind a lease).
/// Nodes that merely live under an underscore segment (an app tile under <c>_App</c>) keep their
/// owner-chosen pointers.</para>
/// </summary>
/// <param name="services">The mesh's service provider, read lazily for the access rule set.</param>
public sealed class SatelliteMainNodeCommitGuard(IServiceProvider services) : IEntityCommitGuard
{
    private readonly Lazy<NodeTypeAccessRuleSet> rules = new(services.GetRequiredService<NodeTypeAccessRuleSet>);

    /// <inheritdoc />
    public string? Refusal(string collection, object? before, object after, string ownerPath,
        AccessContext? writer, JsonSerializerOptions options)
    {
        // An unattributed commit is an internal one (activation saves, replays); a person's write
        // always reaches the owner with its context. System derives and repairs pointers itself.
        if (writer is null
            || string.Equals(writer.ObjectId, WellKnownUsers.System, StringComparison.Ordinal)
            || !string.Equals(collection, nameof(MeshNode), StringComparison.Ordinal)
            || before?.As<MeshNode>(options) is not { } previous
            || after.As<MeshNode>(options) is not { } next
            || !(IsSatellite(previous.NodeType) || IsSatellite(next.NodeType))
            || !MainNodeTrust.Repoints(previous, next))
            return null;
        return LocalizationCatalog.Get("access.mainNode.repointRefused", writer.Locale, next.Path, previous.MainNode);
    }

    private bool IsSatellite(string? nodeType) =>
        !string.IsNullOrEmpty(nodeType)
        && (PartitionDefinition.IsSatelliteNodeType(nodeType)
            || rules.Value.Find(nodeType, NodeOperation.Update) is SatelliteAccessRule);
}
