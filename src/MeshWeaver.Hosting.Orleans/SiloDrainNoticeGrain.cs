using System.Collections.Immutable;
using MeshWeaver.Connection.Orleans;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// The recipient side of <see cref="ISiloDrainNoticeGrain"/> (#6865): records a peer's drain in THIS
/// silo's <see cref="ClusterDrainRoster"/>.
///
/// <para>Keyed by the recipient silo and placed there by the caller's placement hint. Recording on any
/// other silo would teach that silo nothing it needs — only the silo that hands singletons off reads the
/// roster.</para>
///
/// <para>🚨 <b>A notice that lands on the wrong silo is CARRIED to the recipient, never dropped</b>
/// (#6875 review). That happens when a stale directory entry keeps an earlier activation of the key
/// elsewhere. Answering <c>false</c> and leaving it there would leave the recipient unaware for as long
/// as membership does not change — the sender re-announces only on a membership change — so it could
/// still hand singletons to the draining silo. Instead the activation MIGRATES to the silo its KEY names
/// (the key is the recipient, so no caller hint is needed) and takes the pending notices with it
/// (<see cref="IGrainMigrationParticipant"/>); the new activation records them on arrival. Migration
/// also re-points the directory, so later notices land on the recipient directly. If the recipient
/// cannot be placed (it is leaving, or of an older generation without this grain) the arriving
/// activation is misplaced again and logs the notices it could not deliver instead of moving again —
/// bounded, never a loop.</para>
/// </summary>
/// <param name="roster">This silo's drain roster.</param>
/// <param name="localSilo">This silo's identity, compared with the grain key.</param>
/// <param name="logger">Logger for misplaced notices.</param>
[global::Orleans.Placement.PreferLocalPlacement]
internal sealed class SiloDrainNoticeGrain(
    ClusterDrainRoster roster,
    ILocalSiloDetails localSilo,
    ILogger<SiloDrainNoticeGrain> logger)
    : Grain, ISiloDrainNoticeGrain, IGrainMigrationParticipant
{
    private const string PendingKey = "pending-drain-notices";

    /// <summary>Notices received on the wrong silo, carried to the recipient by the migration.</summary>
    private ImmutableHashSet<string> pending = ImmutableHashSet<string>.Empty;

    private bool OnRecipient =>
        string.Equals(this.GetPrimaryKeyString(), localSilo.SiloAddress.ToParsableString(), StringComparison.Ordinal);

    /// <inheritdoc />
    public Task<bool> Draining(string drainingSilo)
    {
        if (OnRecipient)
        {
            roster.MarkDraining(SiloAddress.FromParsableString(drainingSilo));
            return Task.FromResult(true);
        }

        pending = pending.Add(drainingSilo);
        logger.LogInformation(
            "[DRAIN] the notice that {Draining} is draining, addressed to silo {Recipient}, landed on silo "
            + "{Here} — carrying it to the recipient",
            drainingSilo, this.GetPrimaryKeyString(), localSilo.SiloAddress);
        MoveToTheRecipient();
        return Task.FromResult(false);
    }

    /// <summary>Migrates this activation to the silo its key names, the recipient.</summary>
    private void MoveToTheRecipient()
    {
        var hintKey = IPlacementDirector.PlacementHintKey;
        var previous = RequestContext.Get(hintKey);
        try
        {
            RequestContext.Set(hintKey, SiloAddress.FromParsableString(this.GetPrimaryKeyString()));
            MigrateOnIdle();
        }
        catch (InvalidOperationException ex)
        {
            // The activation died between the check and the call: it has already left this silo.
            logger.LogDebug(ex, "[DRAIN] a misplaced drain notice's activation was already gone");
        }
        finally
        {
            if (previous is null)
                RequestContext.Remove(hintKey);
            else
                RequestContext.Set(hintKey, previous);
        }
    }

    /// <inheritdoc />
    void IGrainMigrationParticipant.OnDehydrate(IDehydrationContext dehydrationContext)
    {
        if (!pending.IsEmpty)
            dehydrationContext.TryAddValue(PendingKey, pending.ToArray());
    }

    /// <inheritdoc />
    void IGrainMigrationParticipant.OnRehydrate(IRehydrationContext rehydrationContext)
    {
        if (!rehydrationContext.TryGetValue<string[]>(PendingKey, out var carried) || carried is null)
            return;
        if (!OnRecipient)
        {
            logger.LogWarning(
                "[DRAIN] {Count} drain notice(s) for silo {Recipient} could not be delivered — the migration "
                + "placed them on silo {Here} (the recipient is not placeable); the next membership change "
                + "announces again: {Notices}",
                carried.Length, this.GetPrimaryKeyString(), localSilo.SiloAddress, string.Join(", ", carried));
            return;
        }
        foreach (var drainingSilo in carried)
            roster.MarkDraining(SiloAddress.FromParsableString(drainingSilo));
    }
}
