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

        return status switch
        {
            // Still executing — including while it drains. Anything it holds, it still holds.
            SiloStatus.Created or SiloStatus.Joining or SiloStatus.Active
                or SiloStatus.ShuttingDown or SiloStatus.Stopping => ClusterMemberState.Alive,
            // The ONE positive departure verdict.
            SiloStatus.Dead => ClusterMemberState.Gone,
            // SiloStatus.None — not in this snapshot. See the class remarks: absence is not death.
            _ => ClusterMemberState.Unknown
        };
    }
}
