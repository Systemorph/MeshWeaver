namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What became of every instance-key resolution this replica has answered: how many resolved,
/// were refused, or were UNAVAILABLE, and when each last happened (MeshWeaver#7721).
///
/// <para><b>Why it exists.</b> In #7484 the key exchange (<c>POST /api/instances/token</c>) gave
/// registered keys no answer within the registry hook's ten seconds for about two hours. The
/// symptom that showed was a fleet watch running overdue, a different subsystem on the same
/// instance. <c>/health</c> said nothing about the exchange, because nothing counted what the
/// exchange answered. This tally is that count, and <see cref="InstanceKeyExchangeHealthCheck"/>
/// prints it.</para>
///
/// <para><b>What counts.</b> One entry per instance-key resolution
/// (<see cref="InstanceRegistryAuthenticator.AuthenticateOutcome"/> with an <c>mwi_</c> key): the
/// exchange, and every other registry call that authenticates with the durable key. A request with
/// no credential, or one carrying a short-lived token, is not a key resolution and is not counted.
/// The counts start when the authenticator is first read or first counts, which on a serving
/// replica is its first <c>/health</c> probe or its first key resolution. They are per replica,
/// like every <c>/health</c> reading.</para>
///
/// <para>Instance state on the authenticator (a mesh-scoped singleton), never static: it lives and
/// dies with the mesh (NoStaticState). The reading is one immutable snapshot swapped with a
/// compare-and-exchange, so a reader never sees a count from one outcome next to a timestamp from
/// another.</para>
/// </summary>
/// <param name="clock">The clock the timestamps are read from — the authenticator's deadline
/// scheduler, so a test on virtual time reads virtual timestamps.</param>
public sealed class InstanceKeyResolutionTally(Func<DateTimeOffset> clock)
{
    private InstanceKeyResolutionSnapshot current = InstanceKeyResolutionSnapshot.Empty(clock());

    /// <summary>The current reading.</summary>
    public InstanceKeyResolutionSnapshot Snapshot => Volatile.Read(ref current);

    /// <summary>Records the outcome of one instance-key resolution.</summary>
    /// <param name="outcome">What the resolution answered.</param>
    /// <remarks>The clock is read AFTER the current reading on every attempt, so the timestamp a
    /// successful compare-and-exchange publishes is never older than the one it replaces: a writer
    /// that sampled its time before a concurrent writer published must lose the exchange and
    /// sample again.</remarks>
    public void Record(InstanceAuthResult outcome)
    {
        while (true)
        {
            var before = Volatile.Read(ref current);
            var after = before.With(outcome, clock());
            if (ReferenceEquals(Interlocked.CompareExchange(ref current, after, before), before))
                return;
        }
    }
}

/// <summary>One reading of <see cref="InstanceKeyResolutionTally"/>.</summary>
/// <param name="Since">When counting started on this replica.</param>
/// <param name="Resolved">Resolutions that found the instance.</param>
/// <param name="Refused">Resolutions that ended in a definitive negative.</param>
/// <param name="Unavailable">Resolutions that reached no verdict.</param>
/// <param name="LastResolvedAt">When the newest resolution that found the instance answered.</param>
/// <param name="LastRefusedAt">When the newest definitive negative answered.</param>
/// <param name="LastUnavailableAt">When the newest unavailable resolution answered. Why it reached no
/// verdict is deliberately NOT kept here: the reason names node paths and read failures, and this
/// reading is printed on the unauthenticated <c>/health</c>. The authenticator logs the reason at
/// Warning where it happens.</param>
/// <param name="LatestUnavailable">True when the newest resolution of any kind reached no verdict.</param>
/// <param name="LatestAt">When the newest resolution of any kind answered.</param>
public sealed record InstanceKeyResolutionSnapshot(
    DateTimeOffset Since,
    long Resolved,
    long Refused,
    long Unavailable,
    DateTimeOffset? LastResolvedAt,
    DateTimeOffset? LastRefusedAt,
    DateTimeOffset? LastUnavailableAt,
    bool LatestUnavailable,
    DateTimeOffset? LatestAt)
{
    /// <summary>The reading before any resolution.</summary>
    /// <param name="since">When counting starts.</param>
    public static InstanceKeyResolutionSnapshot Empty(DateTimeOffset since) =>
        new(since, 0, 0, 0, null, null, null, false, null);

    /// <summary>All resolutions counted.</summary>
    public long Total => Resolved + Refused + Unavailable;

    /// <summary>The reading after one more resolution. Unavailable first: a no-verdict outcome
    /// carries no instance, and a definitive negative is one that is neither unavailable nor
    /// resolved.</summary>
    internal InstanceKeyResolutionSnapshot With(InstanceAuthResult outcome, DateTimeOffset at) =>
        outcome.IsUnavailable
            ? this with
            {
                Unavailable = Unavailable + 1, LastUnavailableAt = at, LatestUnavailable = true, LatestAt = at,
            }
            : outcome.Instance is not null || outcome.Build is not null
                ? this with { Resolved = Resolved + 1, LastResolvedAt = at, LatestUnavailable = false, LatestAt = at }
                : this with { Refused = Refused + 1, LastRefusedAt = at, LatestUnavailable = false, LatestAt = at };
}
