using System.Reactive.Concurrency;

namespace MeshWeaver.Messaging;

/// <summary>
/// Hands work to the task pool, and deliberately does NOT implement
/// <see cref="ISchedulerLongRunning"/>.
///
/// <para>🚨 <b>The omission is the whole point — do not "complete" this class by adding it.</b>
/// <c>ObserveOn</c> asks the scheduler for long-running support and, when it is offered, uses
/// <c>ObserveOnObserverLongRunning</c>, which dedicates a DRAIN WORKER PER SUBSCRIPTION. That is
/// tolerable for a handful of long-lived streams and ruinous for a response continuation, which is
/// created once per request in flight: with the hop on every <c>hub.Observe(…)</c> it is a thread
/// per request. Measured on #3759's first CI run — <c>ObserveOnObserverLongRunning.Drain()</c>
/// sitting on a raw <c>Thread.StartHelper.Callback</c> in the crash stack. Hiding the capability
/// keeps Rx on the per-item pooled path, which is what a short observable (one value, then
/// complete) actually wants.</para>
///
/// <para>Rx's own <see cref="TaskPoolScheduler"/> and <see cref="DefaultScheduler"/> both advertise
/// long-running, so neither can be used directly here; this wrapper exists only to withhold that
/// one capability. Everything else delegates.</para>
/// </summary>
internal sealed class PooledContinuationScheduler : IScheduler
{
    /// <summary>The process-wide instance. Immutable — it holds only a delegate scheduler.</summary>
    public static readonly PooledContinuationScheduler Instance = new();

    private static readonly IScheduler Inner = TaskPoolScheduler.Default;

    private PooledContinuationScheduler() { }

    /// <inheritdoc />
    public DateTimeOffset Now => Inner.Now;

    /// <inheritdoc />
    public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
        => Inner.Schedule(state, action);

    /// <inheritdoc />
    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime,
        Func<IScheduler, TState, IDisposable> action)
        => Inner.Schedule(state, dueTime, action);

    /// <inheritdoc />
    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime,
        Func<IScheduler, TState, IDisposable> action)
        => Inner.Schedule(state, dueTime, action);
}
