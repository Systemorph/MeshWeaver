using MeshWeaver.Connection.Orleans;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// The recipient side of <see cref="ISiloDrainNoticeGrain"/> (#6865): records a peer's drain in THIS
/// silo's <see cref="ClusterDrainRoster"/>.
///
/// <para>Keyed by the recipient silo and placed there by the caller's placement hint. An activation
/// that lands anywhere else (the recipient could not be placed — it is leaving, or it is of an older
/// generation that does not know this grain) records nothing and MIGRATES to the recipient its caller's
/// placement hint names, so a stale directory entry cannot keep later notices on the wrong silo. Recording on the wrong silo would teach that silo nothing it needs: only the silo
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
            StepAside();
            return Task.FromResult(false);
        }

        roster.MarkDraining(SiloAddress.FromParsableString(drainingSilo));
        return Task.FromResult(true);
    }

    /// <summary>
    /// Leaves this wrong silo for the recipient. A MIGRATION to the hint the caller supplied
    /// (<c>AnnounceDrainTo</c> names the recipient), never a plain deactivation where one is possible:
    /// a stale directory entry can re-create a deactivated grain on this same silo without running
    /// placement again, and every later notice would land here too (#6875 review). Migration re-points
    /// the directory. Without a usable hint (none carried, or it names this silo) it deactivates, as
    /// <c>PodHubGrain.TryRelocateOnIdle</c> does.
    /// </summary>
    private void StepAside()
    {
        try
        {
            if (RequestContext.Get(IPlacementDirector.PlacementHintKey) is SiloAddress recipient
                && !recipient.Equals(localSilo.SiloAddress))
                MigrateOnIdle();
            else
                DeactivateOnIdle();
        }
        catch (InvalidOperationException ex)
        {
            // The activation died between the check and the call: it has already left this silo.
            logger.LogDebug(ex, "[DRAIN] a misplaced drain notice's activation was already gone");
        }
    }
}
