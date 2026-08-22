namespace MeshWeaver.Hosting;

/// <summary>What a single <c>/drain</c> probe is worth saying out loud.</summary>
public enum DrainProbeOutcome
{
    /// <summary>Nothing to report — this probe fell inside the reporting interval.</summary>
    Silent,

    /// <summary>The first probe, with sessions still open: this pod has entered termination.</summary>
    TerminationBegun,

    /// <summary>A periodic progress line while circuits remain.</summary>
    StillDraining,

    /// <summary>The last circuit closed — reported exactly once.</summary>
    Drained
}

/// <summary>
/// One <c>/drain</c> probe, decided. Everything a log line needs is on the record, so the caller
/// formats and does not re-derive.
/// </summary>
/// <param name="Outcome">Whether — and what — to report.</param>
/// <param name="LiveCircuits">Circuits open at this probe.</param>
/// <param name="CircuitsWhenTerminationBegan">Circuits open at the FIRST probe.</param>
/// <param name="CircuitsAtLastReport">Circuits open when a line was last emitted.</param>
/// <param name="Elapsed">Time since the first probe.</param>
/// <param name="ProbeCount">Probes seen so far, this one included.</param>
public sealed record DrainProbeReport(
    DrainProbeOutcome Outcome,
    int LiveCircuits,
    int CircuitsWhenTerminationBegan,
    int CircuitsAtLastReport,
    TimeSpan Elapsed,
    long ProbeCount)
{
    /// <summary>
    /// True when the count FELL since the last reported line. This is the whole diagnostic: a
    /// drain that is progressing ends on its own, a flat one rides the grace ceiling to SIGKILL.
    /// </summary>
    public bool Progressing => LiveCircuits < CircuitsAtLastReport;
}

/// <summary>
/// Turns the <c>/drain</c> poll into a BOUNDED, readable shutdown narrative — the missing half of
/// the session drain (#1342), and what #1794 asks for.
///
/// <para><b>The gap this closes.</b> <see cref="ActiveCircuitTracker"/> already knows exactly how
/// many sessions are holding the pod open, and <c>/drain</c> already returns that number in its 503
/// body. Nobody reads it: the container's preStop probe is
/// <c>curl -sf -m 5 -o /dev/null …</c> — the count goes to <c>/dev/null</c> and nothing is logged.
/// So a pod sitting in <c>Terminating</c> for twenty-nine minutes is, from outside, completely
/// opaque: one forgotten browser tab and a wedged HTTP layer look identical, and the only way to
/// tell was to exec into a pod that is about to be SIGKILLed.</para>
///
/// <para><b>Why the endpoint is the right place, and why there is no timer.</b> <c>preStop</c> runs
/// BEFORE SIGTERM, so during the whole drain the process has not been told anything: nothing has
/// fired <c>ApplicationStopping</c>, and the pod cannot know it is terminating by any other means.
/// A probe of <c>/drain</c> IS that knowledge — the endpoint is polled by preStop and by nothing
/// else. So the signal is already arriving on its own schedule and needs no watchdog, no poller and
/// no timer of ours: this type only decides what each arriving probe is worth saying.</para>
///
/// <para><b>Reading it from outside.</b> Three cases, now distinguishable in the log alone:</para>
/// <list type="bullet">
/// <item><description><b>Draining normally</b> — the count falls line over line and a
/// <see cref="DrainProbeOutcome.Drained"/> line ends it.</description></item>
/// <item><description><b>A forgotten tab</b> — <see cref="DrainProbeReport.Progressing"/> stays
/// false at a steady count until the kubelet SIGKILLs at the grace ceiling. Working as designed,
/// and now visibly so.</description></item>
/// <item><description><b>A wedged HTTP layer</b> — <see cref="DrainProbeOutcome.TerminationBegun"/>
/// appears and then NOTHING follows, because the endpoint that would report is itself not
/// answering. preStop cannot see this difference (<c>curl -sf</c> fails identically on a 503 and on
/// a refused connection); the absence of the follow-up line is the tell.</description></item>
/// </list>
///
/// <para><b>The other thing the first line buys.</b> It is a durable, greppable marker that this
/// process is past its <c>deletionTimestamp</c>. Every line after it — including a
/// <c>LogCritical</c> from <c>RoutingGrain</c> — comes from a replica Kubernetes has already
/// removed from the Service. Two such Criticals were read as live production alarms during the
/// 2026-08-17 incident precisely because nothing in the log distinguished them.</para>
///
/// <para><b>Bounded on purpose.</b> preStop probes every 5 s; at the 1800 s ceiling that is 360
/// probes. Reporting each one would put 360 Information lines per pod termination into Loki, which
/// is a real cost for no extra information. One line per <see cref="ReportInterval"/> caps a full
/// drain at ~30 lines while still resolving movement.</para>
///
/// <para>Lock-free like the tracker beside it: probes are HTTP requests and may arrive on any
/// thread, and the state transition must be exactly-once (two threads must not both announce the
/// start of termination).</para>
/// </summary>
public sealed class DrainProgress
{
    /// <summary>
    /// Minimum spacing between progress lines. Not a poll interval — nothing here ticks; it is the
    /// rate limit applied to probes that arrive anyway.
    /// </summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(60);

    private sealed record State(
        DateTimeOffset StartedAt,
        DateTimeOffset LastReportedAt,
        int CircuitsWhenTerminationBegan,
        int CircuitsAtLastReport,
        long ProbeCount,
        bool DrainedReported);

    private State? state;

    /// <summary>True once a probe has been seen — i.e. preStop is polling and this pod is going away.</summary>
    public bool TerminationBegun => Volatile.Read(ref state) is not null;

    /// <summary>
    /// Records one probe and decides what it is worth reporting. Pure with respect to
    /// <paramref name="now"/> — the caller supplies the clock, so the whole narrative is unit
    /// testable with no host, no HTTP and no waiting.
    /// </summary>
    /// <param name="liveCircuits">
    /// <see cref="ActiveCircuitTracker.Count"/> at this probe. Negative values are treated as zero:
    /// the tracker clamps, but this type must never turn a bad count into a missing Drained line.
    /// </param>
    /// <param name="now">The probe's timestamp.</param>
    public DrainProbeReport Probe(int liveCircuits, DateTimeOffset now)
    {
        if (liveCircuits < 0)
            liveCircuits = 0;

        while (true)
        {
            var current = Volatile.Read(ref state);
            var previousReported = current?.CircuitsAtLastReport ?? liveCircuits;

            DrainProbeOutcome outcome;
            State next;

            if (current is null)
            {
                // First probe. If nobody is connected the pod is free to go immediately, and that
                // is worth exactly one line — it is the healthy rollout, and its absence is what
                // makes a slow one hard to spot.
                outcome = liveCircuits == 0 ? DrainProbeOutcome.Drained : DrainProbeOutcome.TerminationBegun;
                next = new State(now, now, liveCircuits, liveCircuits, 1, outcome == DrainProbeOutcome.Drained);
            }
            else if (current.DrainedReported)
            {
                // preStop exits on the first success, so a probe after "drained" means something
                // else is polling. Count it, say nothing — the narrative already has its ending.
                outcome = DrainProbeOutcome.Silent;
                next = current with { ProbeCount = current.ProbeCount + 1 };
            }
            else if (liveCircuits == 0)
            {
                outcome = DrainProbeOutcome.Drained;
                next = current with
                {
                    LastReportedAt = now,
                    CircuitsAtLastReport = 0,
                    ProbeCount = current.ProbeCount + 1,
                    DrainedReported = true
                };
            }
            else if (now - current.LastReportedAt >= ReportInterval)
            {
                outcome = DrainProbeOutcome.StillDraining;
                next = current with
                {
                    LastReportedAt = now,
                    CircuitsAtLastReport = liveCircuits,
                    ProbeCount = current.ProbeCount + 1
                };
            }
            else
            {
                outcome = DrainProbeOutcome.Silent;
                next = current with { ProbeCount = current.ProbeCount + 1 };
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref state, next, current), current))
                return new DrainProbeReport(
                    outcome,
                    liveCircuits,
                    next.CircuitsWhenTerminationBegan,
                    previousReported,
                    now - next.StartedAt,
                    next.ProbeCount);
        }
    }

    /// <summary>
    /// 🚨 <b>The line that only exists if the process was allowed to shut down at all</b> — SIGTERM
    /// arrived, and this is what it found.
    ///
    /// <para><b>Why it matters more than it reads.</b> <c>preStop</c> used to poll <c>/drain</c>
    /// with no bound of its own, so a pod whose sessions outlived
    /// <c>terminationGracePeriodSeconds</c> was SIGKILLed WITH A LIVE ORLEANS SILO: the host's 90 s
    /// <c>ShutdownTimeout</c> never ran, <c>ApplicationStopping</c> never fired, and the silo never
    /// departed membership. The deployment's own
    /// <c>cluster-autoscaler.kubernetes.io/safe-to-evict: "false"</c> annotation records what that
    /// costs — <i>"each abrupt departure left a ZOMBIE entry in the Orleans membership table: the
    /// cluster kept placing new grain activations on the dead silo, so writes timed out mesh-wide"</i>
    /// (#1971). Riding to the ceiling was the NORMAL outcome of a roll, not the exception.</para>
    ///
    /// <para>With preStop bounded to <c>drainSeconds − shutdownMarginSeconds</c>, SIGTERM arrives
    /// inside the grace and this line is emitted. Its ABSENCE after a termination is therefore the
    /// evidence of a hard kill — which is exactly the question #1971 could not answer by reading
    /// Loki, because there was nothing to read either way.</para>
    ///
    /// <para>Pure with respect to <paramref name="now"/>, like <see cref="Probe"/>, and safe to
    /// call when no probe was ever seen (a pod SIGTERMed without a preStop, e.g. a node
    /// eviction).</para>
    /// </summary>
    /// <param name="liveCircuits">Circuits still open when shutdown began.</param>
    /// <param name="now">Shutdown's timestamp.</param>
    public DrainAbandonReport Abandon(int liveCircuits, DateTimeOffset now)
    {
        if (liveCircuits < 0)
            liveCircuits = 0;

        var current = Volatile.Read(ref state);
        return new DrainAbandonReport(
            liveCircuits,
            current is null ? TimeSpan.Zero : now - current.StartedAt,
            current?.CircuitsWhenTerminationBegan ?? liveCircuits,
            TerminationWasObserved: current is not null);
    }
}

/// <summary>
/// What SIGTERM found — the shutdown counterpart of <see cref="DrainProbeReport"/>.
/// </summary>
/// <param name="LiveCircuits">Circuits still open when shutdown began. Above zero means these
/// sessions are being cut off: the drain window expired before they closed.</param>
/// <param name="Elapsed">How long the drain ran, or <see cref="TimeSpan.Zero"/> when no probe was
/// ever seen.</param>
/// <param name="CircuitsWhenTerminationBegan">Circuits open at the first probe.</param>
/// <param name="TerminationWasObserved">Whether a <c>/drain</c> probe was ever seen. False means
/// SIGTERM arrived with no preStop at all — a node eviction, a local Ctrl-C, or a chart that lost
/// its lifecycle hook. Distinguished because "the drain ran and gave up" and "there was no drain"
/// need different responses.</param>
public sealed record DrainAbandonReport(
    int LiveCircuits,
    TimeSpan Elapsed,
    int CircuitsWhenTerminationBegan,
    bool TerminationWasObserved)
{
    /// <summary>True when sessions are being cut off by this shutdown.</summary>
    public bool CutSessionsOff => LiveCircuits > 0;
}
