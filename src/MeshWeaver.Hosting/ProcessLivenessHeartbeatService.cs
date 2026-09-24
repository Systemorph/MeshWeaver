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
internal sealed class ProcessLivenessHeartbeatService(
    IConfiguration configuration,
    ILoggerFactory loggerFactory) : IHostedService
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
        ProcessLivenessSample? previous = null;
        var tick = 0L;
        var due = period;

        while (!stopped)
        {
            Thread.Sleep(slice);
            if (stopped || clock.Elapsed < due)
                continue;

            // Re-base off NOW rather than adding a period to `due`: after a long stop the process
            // must resume at the cadence, not fire a burst of backdated ticks.
            due = clock.Elapsed + period;

            try
            {
                var sample = ProcessLiveness.Probe(++tick, clock.Elapsed);
                var reading = ProcessLiveness.Read(previous, sample, period);
                var step = ProcessLiveness.DescribeHeapStep(
                    previous, sample, sampler?.Drain() ?? AllocationWindow.Empty);
                previous = sample;
                logger.LogInformation("{Reading}", reading.Describe());
                if (step is not null)
                    logger.LogWarning("{HeapStep}", step);
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
