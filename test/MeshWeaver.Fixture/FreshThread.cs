using System;
using System.Threading;

namespace MeshWeaver.Fixture;

/// <summary>
/// Runs a probe on a thread that is guaranteed to carry no Rx <c>CurrentThreadScheduler</c>
/// trampoline.
///
/// <para>🚨 This is load-bearing, not tidiness. <c>CurrentThreadScheduler.Schedule</c> only OPENS a
/// trampoline when none is running on the thread; on a thread that already carries one it enqueues
/// and returns <b>without running the action at all</b>. The xUnit test thread can genuinely be in
/// that state — that is precisely the leak issue #2377 is about (a <c>.ToTask()</c> resolved inside
/// the hub's Rx pipeline resumes its awaiter inline, and the runner carries on from there) — so a
/// test that opens its trampoline from the test thread would intermittently skip its own body and
/// report a pass, or fail on its own setup guard, for reasons unrelated to what it tests.</para>
///
/// <para>Shared rather than copied per assembly: the same-class residual in
/// <c>MeshWeaver.Graph</c> (#2087) needs exactly this scaffolding, and a second copy is a second
/// thing to get subtly wrong.</para>
/// </summary>
public static class FreshThread
{
    /// <summary>
    /// Runs <paramref name="probe"/> on a new background thread and rethrows whatever it threw, so
    /// assertion failures keep their original message and stack.
    /// </summary>
    /// <param name="probe">The body to run.</param>
    /// <param name="onTimeout">Message when the thread does not finish inside the budget.</param>
    /// <param name="budget">Wall-clock cap; defaults to 30 s.</param>
    /// <exception cref="TimeoutException">The probe thread did not finish inside
    /// <paramref name="budget"/> — which for a trampoline probe means the work it was waiting on
    /// was queued on a foreign trampoline and can never run.</exception>
    public static void Run(Action probe, string onTimeout, TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { probe(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };
        thread.Start();

        if (!thread.Join(budget ?? TimeSpan.FromSeconds(30)))
            throw new TimeoutException(onTimeout);
        if (failure is not null)
            throw failure;
    }
}
