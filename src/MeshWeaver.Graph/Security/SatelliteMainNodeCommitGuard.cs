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
/// <para><b>Three ways round the comparison are closed too</b> (review of #7655). Each would otherwise
/// commit a pointer no update may choose without the stored-vs-proposed comparison ever seeing it:</para>
/// <list type="bullet">
/// <item><b>A carried write.</b> A data change sent to ANOTHER node's hub can carry a satellite; that hub
///   holds no predecessor under the satellite's key, so the comparison has nothing to compare. A
///   satellite committed anywhere but its own hub may carry only a path-derived pointer
///   (<see cref="CarriesOnlyADerivedPointer"/>), the same narrowing the token guard applies to a carried
///   token record (<c>ApiTokenPrincipalCommitGuard</c>).</item>
/// <item><b>A retype into the satellite class.</b> Retyping a satellite to an ordinary type keeps its
///   pointer and passes; the pointer can then change while neither snapshot is a satellite; retyping it
///   back would restore satellite delegation over the chosen pointer. So a node that BECOMES a satellite
///   may only carry a path-derived pointer.</item>
/// <item><b>A module's satellite type.</b> A rule a module contributes is handed out behind a lease
///   wrapper, so its concrete type cannot be read off <see cref="NodeTypeAccessRuleSet.Find"/>; the
///   classification asks the rule set, which reads the leased rule itself
///   (<see cref="NodeTypeAccessRuleSet.IsGovernedBy{TRule}"/>).</item>
/// </list>
///
/// <para>A node is a satellite here when its node type is governed by <see cref="SatelliteAccessRule"/>
/// or is one of the standard satellite types (<see cref="PartitionDefinition.IsSatelliteNodeType"/>).
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
            || after.As<MeshNode>(options) is not { } next)
            return null;
        var previous = before?.As<MeshNode>(options);
        var wasSatellite = previous is not null && IsSatellite(previous.NodeType);
        var isSatellite = IsSatellite(next.NodeType);
        if (!wasSatellite && !isSatellite)
            return null;

        var refused =
            // Carried to another node's hub: no predecessor to compare with, so only a derived pointer.
            (!PolicyCommitGuard.IsOwnPath(next.Path, ownerPath) && !CarriesOnlyADerivedPointer(next))
            // Retyped INTO the satellite class: the pointer it brings was never compared as a satellite's.
            // Only its SECURITY owner (or itself): the first-segment storage owner can sit above a
            // nested satellite's owner and above a policy that breaks inheritance there (review of #7655).
            || (previous is not null && !wasSatellite && isSatellite && !PointsAtItsSecurityOwnerOrItself(next))
            // The update itself repoints a satellite.
            || (previous is not null && MainNodeTrust.Repoints(previous, next));
        return refused
            ? LocalizationCatalog.Get("access.mainNode.repointRefused", writer.Locale, next.Path,
                previous?.MainNode ?? next.Path)
            : null;
    }

    /// <summary>
    /// Whether the node's <c>MainNode</c> is one its PATH implies: unset or the node itself, the
    /// satellite's owner (<see cref="MainNodeTrust.IsOwnerOf"/>), or the owner of the first satellite
    /// segment of its namespace (what the create and upsert normalisation writes).
    /// </summary>
    private static bool CarriesOnlyADerivedPointer(MeshNode node)
    {
        var main = (node.MainNode ?? "").Trim('/');
        return main.Length == 0
               || string.Equals(main, node.Path.Trim('/'), StringComparison.Ordinal)
               || MainNodeTrust.IsOwnerOf(main, node.Path)
               || (!string.IsNullOrEmpty(node.Namespace)
                   && string.Equals(main, SatelliteTableMapping.OwnerOfSatellitePath(node.Namespace),
                       StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether the node's <c>MainNode</c> is unset, the node itself, or its SECURITY owner
    /// (<see cref="MainNodeTrust.IsOwnerOf"/>), never the storage owner of an outer satellite segment.
    /// </summary>
    private static bool PointsAtItsSecurityOwnerOrItself(MeshNode node)
    {
        var main = (node.MainNode ?? "").Trim('/');
        return main.Length == 0
               || string.Equals(main, node.Path.Trim('/'), StringComparison.Ordinal)
               || MainNodeTrust.IsOwnerOf(main, node.Path);
    }

    private bool IsSatellite(string? nodeType) =>
        !string.IsNullOrEmpty(nodeType)
        && (PartitionDefinition.IsSatelliteNodeType(nodeType)
            || rules.Value.IsGovernedBy<SatelliteAccessRule>(nodeType, NodeOperation.Update));
}
