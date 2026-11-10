using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Drives <see cref="ProcessLiveness"/>: one line per <see cref="ProcessLiveness.PeriodSecondsConfigKey"/>
/// seconds, on a thread of its own, for the life of the host.
///
/// <para>🚨 <b>The thread is dedicated, and that is the whole design.</b> A tick scheduled on the
/// ThreadPool (a <c>Timer</c>, <c>Observable.Interval</c>, a hosted <c>BackgroundService</c> loop)
/// is silenced by the very pool starvation it exists to name, so its silence would mean two
/// different things with different owners. A thread of its own is silenced only when every managed
/// thread is — which is the reading. Same argument, and the same precedent, as
/// <c>IoPool.StartCanceller</c>.</para>
///
/// <para>🚨 <b>A fault must not retire the instrument.</b> If a tick throws, the fault is reported
/// and the loop CONTINUES: a heartbeat that dies on one bad read goes silent forever, and its
/// silence then reads as "the process was suspended" — an instrument manufacturing the very verdict
/// it exists to test.</para>
///
/// <para>🚨 <b>It must never keep the host from starting</b>, for the reason
/// <see cref="PlatformMetricsHostedService"/> states: a portal that will not boot because its
/// instrument threw is a bigger outage than the question the instrument answers.</para>
/// </summary>
/// <param name="configuration">Host configuration — read once, at start.</param>
/// <param name="loggerFactory">The logger factory; the heartbeat logs under this type's category.</param>
/// <param name="services">Where the optional <see cref="IThreadPoolWorkerStacks"/> reader is resolved —
/// a host without one still gets the <c>[POOLSTALL]</c> line, saying it cannot name the frame.</param>
internal sealed class ProcessLivenessHeartbeatService(
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    IServiceProvider services) : IHostedService
{
    /// <summary>How often the loop wakes to notice <see cref="stopped"/>, so shutdown is not held by the cadence.</summary>
    private static readonly TimeSpan StopCheckSlice = TimeSpan.FromMilliseconds(250);

    private readonly ILogger logger = loggerFactory.CreateLogger<ProcessLivenessHeartbeatService>();
    private volatile bool stopped;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var period = ProcessLiveness.PeriodOf(configuration[ProcessLiveness.PeriodSecondsConfigKey]);
            if (period <= TimeSpan.Zero)
            {
                logger.LogInformation(
                    "[LIVENESS] disabled ({Key}=0). A silent window in this log can no longer be told "
                    + "apart from a suspended process.",
                    ProcessLiveness.PeriodSecondsConfigKey);
                return Task.CompletedTask;
            }

            new Thread(() => Run(period))
            {
                IsBackground = true,
                Name = ProcessLiveness.ThreadName,
            }.Start();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "[LIVENESS] the process-liveness heartbeat could not be started; this host publishes "
                + "no liveness line. Everything else is unaffected — instrumentation must never keep "
                + "a host from starting.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // No join: the thread is a background thread that notices `stopped` within StopCheckSlice,
        // and holding host shutdown on a diagnostic would be its own defect.
        stopped = true;
        return Task.CompletedTask;
    }

    private void Run(TimeSpan period)
    {
        // Monotonic on purpose. Wall time steps (NTP, a VM resume) and would forge an overrun.
        var clock = Stopwatch.StartNew();
        var slice = period < StopCheckSlice ? period : StopCheckSlice;
        // The allocation sampler that lets a heap step name its allocator (MeshWeaver#5555). Owned by
        // this thread for the life of the host; a sampler that cannot start leaves the heartbeat
        // exactly as it was, and the [HEAPSTEP] line then SAYS it could not name anything.
        var sampler = StartSampler();
        // OPTIONAL by design: the stack reader lives with the Orleans connection (it needs ClrMD and the
        // runtime's DAC), and a host without it still reports the stall — saying it cannot name the frame.
        var workerStacks = ResolveWorkerStacks();
        TimeSpan? lastStallCapture = null;
        ProcessLivenessSample? previous = null;
        var tick = 0L;
        var due = period;

        // The queue-delay half of [POOLSTALL] (MeshWeaver#5388): polled every slice, not every tick,
        // because the stall it mirrors builds and drains inside one period — a 15.6 s Orleans stall
        // on 2026-11-09 fell between two ticks that each read the pool as making progress.
        var queueProbe = new ThreadPoolQueueProbe();

        while (!stopped)
        {
            Thread.Sleep(slice);
            if (stopped)
                continue;

            PollQueueDelay(queueProbe, clock.Elapsed, tick, ref lastStallCapture, workerStacks);
            if (clock.Elapsed < due)
                continue;

            // Re-base off NOW rather than adding a period to `due`: after a long stop the process
            // must resume at the cadence, not fire a burst of backdated ticks.
            due = clock.Elapsed + period;

            try
            {
                var sample = ProcessLiveness.Probe(++tick, clock.Elapsed);
                var reading = ProcessLiveness.Read(previous, sample, period);
                var step = ProcessLiveness.DescribeHeapStep(
                    previous, sample, sampler?.Drain());
                var stalled = ThreadPoolStall.ShouldCapture(previous, reading, lastStallCapture);
                previous = sample;
                logger.LogInformation("{Reading}", reading.Describe());
                if (step is not null)
                    logger.LogWarning("{HeapStep}", step);
                if (stalled
                    && Interlocked.CompareExchange(ref stallCaptureInFlight, 1, 0) == 0)
                {
                    lastStallCapture = sample.Elapsed;
                    StartStallCapture(reading.Current.TickSeq, stacks => DescribeStall(reading, stacks), workerStacks);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "[LIVENESS] tick {Tick} could not be read. The loop continues — a heartbeat that "
                    + "died here would go silent, and its silence reads as a suspended process.",
                    tick);
            }
        }

        sampler?.Dispose();
    }

    /// <summary>
    /// Polls the queue-delay probe and, when a probe item has waited past
    /// <see cref="ThreadPoolStall.QueueDelayCaptureThreshold"/>, starts the capture. Never throws: a
    /// fault here is reported and the heartbeat runs on, for the reason the class remarks give.
    /// </summary>
    private void PollQueueDelay(
        ThreadPoolQueueProbe probe, TimeSpan now, long tick, ref TimeSpan? lastStallCapture,
        IThreadPoolWorkerStacks? workerStacks)
    {
        try
        {
            var wait = probe.Poll(now);
            if (!ThreadPoolStall.ShouldCaptureQueueDelay(wait, now, lastStallCapture))
                return;
            // Sampled BEFORE the in-flight flag is taken: a throwing sample must not leave the flag
            // set with no capture to clear it, which would silence both triggers for good.
            var sample = ProcessLiveness.Probe(tick, now);
            if (Interlocked.CompareExchange(ref stallCaptureInFlight, 1, 0) != 0)
                return;
            lastStallCapture = now;
            StartStallCapture(tick, stacks => DescribeQueueDelay(sample, wait, stacks), workerStacks);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "[POOLSTALL] the ThreadPool queue-delay probe could not be read; the heartbeat continues.");
        }
    }

    /// <summary>1 while a <c>[POOLSTALL]</c> capture runs; at most one is ever in flight.</summary>
    private int stallCaptureInFlight;

    /// <summary>
    /// The optional stack reader, resolved without ever throwing: this runs on a raw thread, where an
    /// unhandled exception (a throwing factory, a provider disposed by a racing shutdown) would end the
    /// process. A failed resolution is reported and the heartbeat runs on with no reader — its
    /// <c>[POOLSTALL]</c> line then says it cannot name the frame.
    /// </summary>
    private IThreadPoolWorkerStacks? ResolveWorkerStacks()
    {
        try
        {
            return services.GetService<IThreadPoolWorkerStacks>();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "[POOLSTALL] the ThreadPool worker stack reader could not be resolved; a pool stall in this "
                + "process will be reported without the frames its workers were in.");
            return null;
        }
    }

    /// <summary>
    /// Runs the capture on a dedicated thread of its own, so a slow (or never-returning) dump write
    /// or ClrMD read can never silence the <c>[LIVENESS]</c> line or forge an overrun on it. The
    /// in-flight flag keeps it to one capture at a time; it is cleared only when the line is logged.
    /// </summary>
    private void StartStallCapture(
        long tick, Func<IThreadPoolWorkerStacks?, string> describe, IThreadPoolWorkerStacks? workerStacks)
    {
        try
        {
            new Thread(() =>
            {
                try
                {
                    logger.LogWarning("{PoolStall}", describe(workerStacks));
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "[POOLSTALL] tick {Tick}: the line could not be written.", tick);
                }
                finally
                {
                    Volatile.Write(ref stallCaptureInFlight, 0);
                }
            })
            {
                IsBackground = true,
                Name = ProcessLiveness.ThreadName + "-poolstall",
            }.Start();
        }
        catch (Exception exception)
        {
            Volatile.Write(ref stallCaptureInFlight, 0);
            logger.LogWarning(exception,
                "[POOLSTALL] tick {Tick}: the capture thread could not be started, so this stall is reported "
                + "without its frames.", tick);
        }
    }

    /// <summary>
    /// Captures the pool workers' stacks for a stalled tick and renders the <c>[POOLSTALL]</c> line.
    /// Runs on the capture's own dedicated thread — never a pool worker, which the stall holds — and
    /// never throws: a capture that fails is reported IN the line, so the stall is still named.
    /// </summary>
    private static string DescribeStall(ProcessLivenessReading reading, IThreadPoolWorkerStacks? workerStacks)
    {
        if (workerStacks is null)
            return ThreadPoolStall.Describe(reading, null, null);
        try
        {
            var stacks = workerStacks.CaptureStacks();
            return ThreadPoolStall.Describe(reading, stacks.Workers, null, stacks.OtherThreads);
        }
        catch (Exception exception)
        {
            return ThreadPoolStall.Describe(reading, null, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>The queue-delay counterpart of <see cref="DescribeStall"/>; same thread, same never-throw rule.</summary>
    private static string DescribeQueueDelay(
        ProcessLivenessSample sample, TimeSpan wait, IThreadPoolWorkerStacks? workerStacks)
    {
        if (workerStacks is null)
            return ThreadPoolStall.DescribeQueueDelay(sample, wait, null, null);
        try
        {
            var stacks = workerStacks.CaptureStacks();
            return ThreadPoolStall.DescribeQueueDelay(sample, wait, stacks.Workers, null, stacks.OtherThreads);
        }
        catch (Exception exception)
        {
            return ThreadPoolStall.DescribeQueueDelay(sample, wait, null, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private AllocationByTypeSampler? StartSampler()
    {
        try
        {
            return new AllocationByTypeSampler();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "[LIVENESS] the allocation sampler could not be started; a heap step in this process "
                + "will be reported without the types that were allocated in it.");
            return null;
        }
    }
}

/// <summary>Wires <see cref="ProcessLivenessHeartbeatService"/> — called by a host whose log is actually kept.</summary>
public static class ProcessLivenessRegistration
{
    /// <summary>
    /// Arms the process-liveness heartbeat for this host.
    ///
    /// <para>🚨 Wired by the HOST, deliberately NOT by <c>MeshHostApplicationBuilder</c> — which
    /// would start a thread inside every mesh in the fleet, including the several thousand a test
    /// run builds, none of whose logs anybody keeps. Same rule, and the same reason, as
    /// <see cref="PlatformMetricsRegistration.AddPlatformMetrics"/>.</para>
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddProcessLivenessHeartbeat(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<ProcessLivenessHeartbeatService>();
        return services;
    }
}
