using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// 🛡️ The satellite rule's write half (<see cref="SatelliteAccessRule"/>, <see cref="MainNodeTrust.Repoints"/>)
/// at the owner's COMMIT point, the one place every write that commits a node meets: a merge patch from
/// <c>GetMeshNodeStream(path).Update</c>, an upsert's update branch, a data change. The owning hub's
/// delivery check decides access from the STORED node, so a proposal's <c>MainNode</c> is seen only here.
///
/// <para><b>THE INVARIANT</b> (review of #7655). For every write by anyone but System that commits a
/// satellite (a node of a satellite type before the write, after it, or both), the committed
/// <c>MainNode</c> is one of exactly two values:</para>
/// <list type="bullet">
/// <item>the STORED satellite's pointer, unchanged byte for byte;</item>
/// <item>EXACTLY the satellite's path-derived security owner (<see cref="MainNodeTrust.OwnerOf"/>), and
///   only where nothing satellite-shaped was stored: the stored pointer was never set (empty, or the
///   node itself), or the node is entering the satellite class by a retype.</item>
/// </list>
/// <para>A satellite CARRIED to another node's hub is refused outright, because that hub holds no
/// predecessor and cannot tell a create from a rewrite. A create at the satellite's own hub is decided
/// by the creation chain (<see cref="SatelliteAccessRule"/>'s landing check), since nothing is stored to
/// compare with. Values are raw and ordinal: <c>""</c> and <c>"/"</c>, case variants, trailing
/// slashes and <c>..</c> segments are all different pointers.</para>
///
/// <para>So a writer can only ever move a satellite's delegation to its path-derived owner, and only
/// when nothing chose it before. A pointer that differs from the owner is either one the platform
/// (System) or the create chain wrote, or one that has stood unchanged since then.</para>
///
/// <para><b>Refused, not re-validated:</b> a commit guard is synchronous and must not read or wait, so it
/// cannot ask the permission fold about a new target. A satellite here is a node type governed by
/// <see cref="SatelliteAccessRule"/>, asked through the rule set so a module's leased rule counts
/// (<see cref="NodeTypeAccessRuleSet.IsGovernedBy{TRule}"/>), or a standard satellite type
/// (<see cref="PartitionDefinition.IsSatelliteNodeType"/>). Nodes that merely live under an underscore
/// segment (an app tile under <c>_App</c>) keep their owner-chosen pointers.</para>
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
            // 🛡️ CARRIED to another node's hub (a data change whose entity is not the receiving hub's own
            // node): that hub holds no predecessor, so it cannot tell a create from a rewrite, and any
            // pointer it accepted could replace a stored one. Refused outright for every writer but
            // System, as ApiTokenPrincipalCommitGuard refuses a carried token record. A satellite is
            // written through its own stream (GetMeshNodeStream(path).Update), which reaches its owner.
            !PolicyCommitGuard.IsOwnPath(next.Path, ownerPath)
            // A create at its own hub is decided by the creation chain (SatelliteAccessRule's landing
            // check, NormalizeSatelliteMainNode) — there is nothing stored to compare with here.
            || (previous is not null
                && (wasSatellite
                    // An update of a satellite: the invariant (MainNodeTrust.Repoints).
                    ? MainNodeTrust.Repoints(previous, next)
                    // Retyped INTO the satellite class: whatever pointer the ordinary node held was never
                    // compared as a satellite's, so the node enters with exactly its security owner.
                    : !MainNodeTrust.IsOwnerOf(next.MainNode, next.Path)));
        return refused
            ? LocalizationCatalog.Get("access.mainNode.repointRefused", writer.Locale, next.Path,
                previous?.MainNode ?? next.Path)
            : null;
    }

    private bool IsSatellite(string? nodeType) =>
        !string.IsNullOrEmpty(nodeType)
        && (PartitionDefinition.IsSatelliteNodeType(nodeType)
            || rules.Value.IsGovernedBy<SatelliteAccessRule>(nodeType, NodeOperation.Update));
}
