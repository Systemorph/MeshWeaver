using MeshWeaver.Connection.Orleans;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// The recipient side of <see cref="ISiloDrainNoticeGrain"/> (#6865): records a peer's drain in THIS
/// silo's <see cref="ClusterDrainRoster"/>.
///
/// <para>Keyed by the recipient silo and placed there by the caller's placement hint. An activation
/// that lands anywhere else (the recipient could not be placed — it is leaving, or it is of an older
/// generation that does not know this grain) records nothing and deactivates, so a later notice is
/// placed afresh. Recording on the wrong silo would teach that silo nothing it needs: only the silo
/// that hands singletons off reads the roster.</para>
/// </summary>
/// <param name="roster">This silo's drain roster.</param>
/// <param name="localSilo">This silo's identity, compared with the grain key.</param>
/// <param name="logger">Logger for misplaced notices.</param>
[global::Orleans.Placement.PreferLocalPlacement]
internal sealed class SiloDrainNoticeGrain(
    ClusterDrainRoster roster,
    ILocalSiloDetails localSilo,
    ILogger<SiloDrainNoticeGrain> logger)
    : Grain, ISiloDrainNoticeGrain
{
    /// <inheritdoc />
    public Task<bool> Draining(string drainingSilo)
    {
        var recipient = this.GetPrimaryKeyString();
        if (!string.Equals(recipient, localSilo.SiloAddress.ToParsableString(), StringComparison.Ordinal))
        {
            logger.LogInformation(
                "[DRAIN] the notice that {Draining} is draining, addressed to silo {Recipient}, landed on silo "
                + "{Here} — not recorded; this activation steps aside",
                drainingSilo, recipient, localSilo.SiloAddress);
            DeactivateOnIdle();
            return Task.FromResult(false);
        }

        roster.MarkDraining(SiloAddress.FromParsableString(drainingSilo));
        return Task.FromResult(true);
    }
}
