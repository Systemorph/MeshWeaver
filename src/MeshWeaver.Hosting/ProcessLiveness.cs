using System.Globalization;

namespace MeshWeaver.Hosting;

/// <summary>One tick's raw reading of the process: when it was taken, and what the runtime said.</summary>
/// <param name="TickSeq">Monotonic tick number, from 1. A gap in the sequence is a lost line, not a lost tick.</param>
/// <param name="Elapsed">The heartbeat's own monotonic clock at the tick — never wall time, which a clock step moves.</param>
/// <param name="Gen0">Gen-0 collections so far (<see cref="GC.CollectionCount(int)"/>).</param>
/// <param name="Gen1">Gen-1 collections so far.</param>
/// <param name="Gen2">Gen-2 collections so far — the blocking ones.</param>
/// <param name="TotalGcPause">Total time this process has spent paused for GC (<see cref="GC.GetTotalPauseDuration"/>).</param>
/// <param name="HeapBytes">Managed heap bytes, read WITHOUT forcing a collection.</param>
/// <param name="ServerGc">Whether server GC is on. Workstation GC on a multi-GiB heap is the shape behind the long pauses.</param>
/// <param name="ThreadPoolThreads">Thread-pool threads that exist.</param>
/// <param name="ThreadPoolPending">Work items queued and not yet started — the starvation tell.</param>
/// <param name="ThreadPoolCompleted">Work items completed so far — flat WITH pending &gt; 0 is starvation; climbing is a healthy pool.</param>
public sealed record ProcessLivenessSample(
    long TickSeq,
    TimeSpan Elapsed,
    int Gen0,
    int Gen1,
    int Gen2,
    TimeSpan TotalGcPause,
    long HeapBytes,
    bool ServerGc,
    int ThreadPoolThreads,
    long ThreadPoolPending,
    long ThreadPoolCompleted);

/// <summary>
/// One tick, read against the tick before it. Every field a reader needs to say WHICH KIND of
/// silence a quiet window was — see <see cref="ProcessLiveness"/>.
/// </summary>
/// <param name="Current">This tick's raw sample.</param>
/// <param name="Gap">Monotonic time since the previous tick, or null on the first tick. Never wall
/// time — a clock step would forge an overrun, which is why <see cref="ProcessLivenessSample.Elapsed"/>
/// is a <see cref="System.Diagnostics.Stopwatch"/> reading.</param>
/// <param name="Period">The cadence this heartbeat was asked to keep.</param>
/// <param name="Gen0Delta">Gen-0 collections since the previous tick, or null on the first.</param>
/// <param name="Gen1Delta">Gen-1 collections since the previous tick, or null on the first.</param>
/// <param name="Gen2Delta">Gen-2 collections since the previous tick, or null on the first.</param>
/// <param name="GcPauseDelta">GC pause accumulated since the previous tick, or null on the first.</param>
/// <param name="ThreadPoolCompletedDelta">Work items the pool finished since the previous tick, or null on the first.</param>
public sealed record ProcessLivenessReading(
    ProcessLivenessSample Current,
    TimeSpan? Gap,
    TimeSpan Period,
    int? Gen0Delta,
    int? Gen1Delta,
    int? Gen2Delta,
    TimeSpan? GcPauseDelta,
    long? ThreadPoolCompletedDelta)
{
    /// <summary>
    /// How late a tick may be before the line says so. Generous on purpose: a heartbeat that cries
    /// overrun at ordinary scheduling jitter teaches its readers to ignore it.
    /// </summary>
    public TimeSpan OverrunThreshold =>
        Period + (Period > TimeSpan.FromSeconds(2) ? Period / 2 : TimeSpan.FromSeconds(1));

    /// <summary>True when this tick arrived later than <see cref="OverrunThreshold"/> — the process was stopped, and it came back.</summary>
    public bool Overran => Gap is { } gap && gap > OverrunThreshold;

    /// <summary>
    /// The one line. Written to be read in a log file next to the silence it explains, so every
    /// datum a reader needs is ON it rather than derivable from the ones around it.
    /// </summary>
    public string Describe()
    {
        var c = Current;
        var line = new System.Text.StringBuilder(256)
            .Append(CultureInfo.InvariantCulture, $"[LIVENESS] tick={c.TickSeq}")
            .Append(Gap is { } gap
                ? string.Create(CultureInfo.InvariantCulture, $" gap={Seconds(gap)}/{Seconds(Period)}")
                : " gap=first")
            .Append(CultureInfo.InvariantCulture, $" gen0={c.Gen0}{Delta(Gen0Delta)}")
            .Append(CultureInfo.InvariantCulture, $" gen1={c.Gen1}{Delta(Gen1Delta)}")
            .Append(CultureInfo.InvariantCulture, $" gen2={c.Gen2}{Delta(Gen2Delta)}")
            .Append(CultureInfo.InvariantCulture, $" gcPause={Seconds(c.TotalGcPause)}{DeltaSeconds(GcPauseDelta)}")
            .Append(CultureInfo.InvariantCulture, $" heap={Gib(c.HeapBytes)}")
            .Append(CultureInfo.InvariantCulture, $" serverGC={c.ServerGc}")
            .Append(CultureInfo.InvariantCulture, $" poolThreads={c.ThreadPoolThreads}")
            .Append(CultureInfo.InvariantCulture, $" poolPending={c.ThreadPoolPending}")
            .Append(CultureInfo.InvariantCulture, $" poolCompleted={c.ThreadPoolCompleted}{Delta(ThreadPoolCompletedDelta)}");

        if (Overran && Gap is { } late)
        {
            var over = late - Period;
            line.Append(CultureInfo.InvariantCulture, $" — OVERRAN by {Seconds(over)}");
            if (GcPauseDelta is { } pause)
                line.Append(CultureInfo.InvariantCulture,
                    $", of which {Seconds(pause)} was GC pause ({Share(pause, over)} of the overrun)");
            line.Append('.');
        }

        return line.ToString();
    }

    private static string Seconds(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds:0.00}s");

    private static string DeltaSeconds(TimeSpan? value) =>
        value is { } v ? string.Create(CultureInfo.InvariantCulture, $"(+{v.TotalSeconds:0.00}s)") : string.Empty;

    private static string Delta(long? value) =>
        value is { } v ? string.Create(CultureInfo.InvariantCulture, $"(+{v})") : string.Empty;

    private static string Delta(int? value) => Delta((long?)value);

    private static string Gib(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.00}GiB");

    private static string Share(TimeSpan part, TimeSpan whole) =>
        whole <= TimeSpan.Zero
            ? "n/a"
            : string.Create(CultureInfo.InvariantCulture, $"{100.0 * part.TotalSeconds / whole.TotalSeconds:0}%");
}

/// <summary>
/// The process-liveness heartbeat's reading — the instrument that says WHICH KIND of silence a
/// quiet window in a log was.
///
/// <para>🚨 <b>Why an unconditional periodic line, when every other stall instrument here reports
/// on an event.</b> Orleans' watchdog, the disposal stall detector and the pending-callback report
/// all fire when something is noticed, and every one of them reports ON RESUME. A pause that never
/// resumes before the process is killed therefore prints NOTHING — and that is exactly the window a
/// wedge investigation is looking at. MeshWeaver#4234 is the worked case: a portal logged nothing
/// for the final 90&#160;s of an e2e shard, the page showed <c>"Subscribing to {path}…"</c>, and the
/// report read the silence as a layout subscription that never completed. The run's own Playwright
/// trace then showed six ordinary STATIC-FILE GETs and the Blazor reconnect's
/// <c>POST /_blazor/negotiate</c> hanging in the same window — nothing mesh-shaped at all — so the
/// failing unit was the whole HTTP server, not a subscription. The distinction was available only
/// because a second instrument happened to be recording; nothing in the process itself could say
/// it.</para>
///
/// <para>🚨 <b>Absence is the reading, so the line may never be conditional.</b> An instrument that
/// prints only when it is unhappy makes "nothing happened" and "I was not running" the same bytes —
/// the ambiguity the <c>/health</c> census tag exists to remove, one layer down. The cost is stated
/// rather than minimised: one ~200-byte line per <see cref="DefaultPeriod"/> per process (≈1.7&#160;MB
/// per pod per day at the default), which buys the difference between "the process was stopped" and
/// "one hub was stuck" in every artifact and every Loki window the fleet already keeps.
/// <c>Diagnostics:LivenessHeartbeatSeconds = 0</c> turns it off.</para>
///
/// <para>🚨 <b>A DEDICATED thread, never the ThreadPool</b> — same argument
/// <c>IoPool.StartCancelOffCallerThread</c> makes: a tick scheduled on the pool is silenced BY pool
/// starvation, so it could not tell starvation from suspension, and those have different owners. A
/// thread of its own is silenced only when every managed thread is.</para>
///
/// <para><b>How to read a silent window.</b></para>
/// <list type="table">
/// <item><term>the line keeps printing, <c>gcPause</c> delta ≈ 0, <c>poolCompleted</c> climbing</term>
///   <description>the process is ALIVE and doing work — the silence is one wedged hub or stream, and
///   <c>target pump now:</c> on the pending-callback report names it.</description></item>
/// <item><term>the line keeps printing, <c>poolPending</c> climbing while <c>poolCompleted</c> is flat</term>
///   <description>thread-pool starvation — a blocking bridge, not a stream defect.</description></item>
/// <item><term>the line stops with everything else, and never resumes</term>
///   <description>every managed thread was suspended. A GC pause that outlived the process, or a
///   runtime-level freeze. Not a wedge, and not the mesh's to fix.</description></item>
/// <item><term>the line resumes with <c>OVERRAN</c> and a matching <c>gcPause</c> delta</term>
///   <description>a GC pause of exactly that length, self-reported in band.</description></item>
/// </list>
///
/// <para>🚨 <b>Do not read CPU% instead.</b> On the MeshWeaver#4234 artifact a CONFIRMED 13.64&#160;s
/// GC pause coincides with a <c>docker stats</c> sample of <b>2.14 %</b> — the positive control that
/// falsifies "single-digit CPU, therefore not GC". CPU sampled from outside the process is not a
/// discriminator on this host.</para>
///
/// <para>Pure over a probe seam, so the derivation is testable without waiting for a real tick.</para>
/// </summary>
public static class ProcessLiveness
{
    /// <summary>Config key: seconds between ticks. <c>0</c> (or negative) turns the heartbeat off.</summary>
    public const string PeriodSecondsConfigKey = "Diagnostics:LivenessHeartbeatSeconds";

    /// <summary>Ten seconds — nine readings inside the 90&#160;s window MeshWeaver#4234 could not read.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(10);

    /// <summary>The name of the heartbeat thread, so a stack dump names it.</summary>
    public const string ThreadName = "mw-liveness";

    /// <summary>
    /// Read the cadence from configuration. Absent or malformed ⇒ <see cref="DefaultPeriod"/>;
    /// zero or negative ⇒ <see cref="TimeSpan.Zero"/>, which means OFF. Malformed deliberately does
    /// NOT mean off: a typo must not silently remove an instrument.
    /// </summary>
    /// <param name="configured">The raw configured value, or null.</param>
    /// <returns>The cadence, or <see cref="TimeSpan.Zero"/> when the heartbeat is switched off.</returns>
    public static TimeSpan PeriodOf(string? configured)
    {
        // 🚨 `double.TryParse` SUCCEEDS on "NaN", "Infinity" and "1e400", and
        // `TimeSpan.FromSeconds` THROWS on every one of them. The throw is caught where this is
        // called, so the host still boots — and the heartbeat silently does not run, which is the
        // exact outcome the paragraph above says a typo must never produce. A value that is not a
        // finite number is a malformed value and lands on the default like any other; so does one
        // past `MaximumPeriod`, where the configured number is a typo rather than an intention.
        if (!double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds)
            || seconds > MaximumPeriod.TotalSeconds)
            return DefaultPeriod;

        return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>One day — a cadence past this is a typo, and the instrument would publish nothing.</summary>
    public static readonly TimeSpan MaximumPeriod = TimeSpan.FromDays(1);

    /// <summary>The production probe: what the runtime says right now.</summary>
    /// <param name="tickSeq">This tick's number.</param>
    /// <param name="elapsed">The heartbeat's monotonic clock at this tick.</param>
    /// <returns>The raw sample.</returns>
    public static ProcessLivenessSample Probe(long tickSeq, TimeSpan elapsed) =>
        new(tickSeq,
            elapsed,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalPauseDuration(),
            GC.GetTotalMemory(forceFullCollection: false),
            System.Runtime.GCSettings.IsServerGC,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            ThreadPool.CompletedWorkItemCount);

    /// <summary>Derive one reading from this tick and the one before it.</summary>
    /// <param name="previous">The previous tick's sample, or null for the first tick.</param>
    /// <param name="current">This tick's sample.</param>
    /// <param name="period">The cadence the heartbeat was asked to keep.</param>
    /// <returns>The reading, whose <see cref="ProcessLivenessReading.Describe"/> is the logged line.</returns>
    public static ProcessLivenessReading Read(
        ProcessLivenessSample? previous, ProcessLivenessSample current, TimeSpan period) =>
        previous is null
            ? new ProcessLivenessReading(current, null, period, null, null, null, null, null)
            : new ProcessLivenessReading(
                current,
                current.Elapsed - previous.Elapsed,
                period,
                current.Gen0 - previous.Gen0,
                current.Gen1 - previous.Gen1,
                current.Gen2 - previous.Gen2,
                current.TotalGcPause - previous.TotalGcPause,
                current.ThreadPoolCompleted - previous.ThreadPoolCompleted);
}
