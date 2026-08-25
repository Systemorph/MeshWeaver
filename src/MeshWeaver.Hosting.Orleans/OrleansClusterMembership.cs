using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// <see cref="IClusterMembership"/> over Orleans cluster membership — the authoritative,
/// level-triggered answer to "is that process still running?", which the platform already computes
/// (probes, indirect probes, the membership table) and which no file timestamp can approximate.
///
/// <para>The identity is the silo's own <see cref="SiloAddress"/> in parsable form. Deliberately NOT
/// a pod or machine name: <c>SiloOptions.SiloName</c> is not set in any of this repo's deployments,
/// so a name-based lookup would be guesswork, while an address round-trips exactly through
/// <see cref="SiloAddress.FromParsableString"/> and is what the membership snapshot is keyed by.</para>
///
/// <para>🚨 <b>Absence from the snapshot is <see cref="ClusterMemberState.Unknown"/>, never
/// <see cref="ClusterMemberState.Gone"/>.</b> Orleans keeps departed silos in the table as
/// <see cref="SiloStatus.Dead"/> until the defunct-cleanup window elapses, so a silo that actually
/// died IS in the snapshot and resolves to Gone. A silo that is absent entirely is either long gone
/// or — the case that matters — our snapshot is not hydrated yet, and reading that as "gone" on a
/// freshly-started silo would license taking over from a peer that is perfectly alive. Unknown falls
/// back to whatever the caller's fallback is, which is always the safe direction.</para>
///
/// <para>🚨 <b>…and the mirror-image rule: a row that is merely PRESENT is not
/// <see cref="ClusterMemberState.Alive"/> either</b> — see <see cref="Classify"/> for why
/// <see cref="SiloStatus.Created"/> / <see cref="SiloStatus.Joining"/> report Unknown (#2076).</para>
/// </summary>
public sealed class OrleansClusterMembership(
    IClusterMembershipService membership,
    ILocalSiloDetails localSilo,
    ILogger<OrleansClusterMembership>? logger = null) : IClusterMembership
{
    /// <inheritdoc />
    public string LocalIdentity => localSilo.SiloAddress.ToParsableString();

    /// <inheritdoc />
    public ClusterMemberState StateOf(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return ClusterMemberState.Unknown;

        SiloAddress address;
        try
        {
            address = SiloAddress.FromParsableString(identity);
        }
        catch (Exception ex)
        {
            // Not one of ours — a stamp from an older build, or a different identity scheme.
            logger?.LogDebug(ex,
                "Cluster membership cannot parse the identity {Identity} — reporting Unknown", identity);
            return ClusterMemberState.Unknown;
        }

        // Ourselves: we are demonstrably running, and the snapshot may not list us yet during join.
        if (address.Equals(localSilo.SiloAddress))
            return ClusterMemberState.Alive;

        SiloStatus status;
        try
        {
            status = membership.CurrentSnapshot.GetSiloStatus(address);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex,
                "Cluster membership could not be read for {Identity} — reporting Unknown", identity);
            return ClusterMemberState.Unknown;
        }

        return Classify(status);
    }

    /// <summary>
    /// The <see cref="SiloStatus"/> → <see cref="ClusterMemberState"/> mapping, pure so the rule can
    /// be pinned without standing up a cluster.
    ///
    /// <para>🚨 <b><see cref="SiloStatus.Created"/> and <see cref="SiloStatus.Joining"/> are NOT
    /// <see cref="ClusterMemberState.Alive"/></b> (#2076). <c>Alive</c> is not "a row exists" — it is
    /// a POSITIVE verdict that the member is running, and its consumers treat it as
    /// permission-denied-forever: <c>BuildNodeType.HolderStillHoldsIt</c> reads Alive as <i>"never
    /// take over, however old the heartbeat looks"</i>, deliberately skipping the
    /// <c>ClaimStaleAfter</c> clock. Orleans only probes ACTIVE silos, so a process that died before
    /// finishing its join leaves a <c>Created</c>/<c>Joining</c> row that no failure detector will
    /// ever move to <c>Dead</c>. Mapping those to Alive therefore made the clock fallback
    /// STRUCTURALLY UNREACHABLE for the one case it exists to cover — and that is exactly what
    /// happened on memex-cloud (2026-08-22): a pod deleted MID-BOOT held the build claim, every
    /// other pod sat in <c>FollowGo</c> for 25+ minutes, and with <c>PreWarm:GateReadiness=true</c>
    /// that held the whole rollout.</para>
    ///
    /// <para>Reporting them as <see cref="ClusterMemberState.Unknown"/> is the honest answer — the
    /// cluster genuinely has no opinion about a member that never joined — and it hands the decision
    /// to the caller's fallback rather than to a takeover. It does NOT license an immediate steal: a
    /// holder that IS mid-join and working keeps its claim through the heartbeat it writes, and only
    /// a holder that has been silent for the full staleness budget is displaced. The states that
    /// mean "this process reached Active and is still executing" — including while it drains — stay
    /// Alive, which is what the original rule was actually reaching for.</para>
    /// </summary>
    /// <param name="status">The membership status read from the snapshot.</param>
    /// <returns>What this cluster knows about that member.</returns>
    internal static ClusterMemberState Classify(SiloStatus status) => status switch
    {
        // Reached Active and still executing — including while it drains. Anything it holds, it
        // still holds, and Orleans' failure detector is watching it.
        SiloStatus.Active or SiloStatus.ShuttingDown or SiloStatus.Stopping => ClusterMemberState.Alive,
        // The ONE positive departure verdict.
        SiloStatus.Dead => ClusterMemberState.Gone,
        // SiloStatus.Created / Joining — never became a probed member, so nothing will ever move it
        // to Dead (see above). SiloStatus.None — not in this snapshot; absence is not death.
        _ => ClusterMemberState.Unknown
    };
}
