using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

/// <summary>
/// The kinds of product-side timeout terminal a <see cref="ITimeoutTerminalObserver"/> is told
/// about. An OPEN vocabulary (policy <c>open-vocabulary-string-constants</c>): a module that adds
/// its own terminal declares its own constant and reports it through the same seam; nothing
/// validates a kind against this list.
/// </summary>
public static class TimeoutTerminalKinds
{
    /// <summary>
    /// A hub request got no reply within <c>MessageHubConfiguration.RequestTimeout</c> of silence
    /// (<c>MessageHub.ApplyTimeout</c>).
    /// </summary>
    public const string RequestTimeout = nameof(RequestTimeout);

    /// <summary>
    /// A query's provider fan-in did not emit an Initial within
    /// <c>MeshOperationOptions.QueryInitialBudget</c> and the query was terminated as stalled.
    /// </summary>
    public const string QueryInitial = nameof(QueryInitial);

    /// <summary>
    /// A remote mesh-node write got no verdict from its owner within the write verdict bound
    /// (<c>LatePatchResponseRegistry.WriteVerdictBound</c>) — <c>OwnerUnreachable</c> by deadline.
    /// </summary>
    public const string WriteVerdict = nameof(WriteVerdict);

    /// <summary>
    /// A write refused by an owner that was shutting down could not be re-asked, because that
    /// owner's teardown did not settle within the caller's verdict bound.
    /// </summary>
    public const string WriteTeardownJoin = nameof(WriteTeardownJoin);

    /// <summary>
    /// The inner <c>CreateNodeRequest</c> of a create got no verdict within
    /// <c>MeshExtensions.InnerCreateVerdictBound</c> (derived from <c>MeshOperationOptions.Timeout</c>).
    /// </summary>
    public const string CreateVerdict = nameof(CreateVerdict);

    /// <summary>
    /// A create, move or copy did not reach a verdict within its <c>MeshOperationOptions.Timeout</c>
    /// and answered with its stalled verdict (<c>LifecycleVerdictStalledException</c>).
    /// </summary>
    public const string LifecycleStalled = nameof(LifecycleStalled);

    /// <summary>
    /// A create's row was written but a post-creation step had not finished within
    /// <c>MeshOperationOptions.Timeout</c>, so the create answered "outcome unknown".
    /// </summary>
    public const string CreatePostCreation = nameof(CreatePostCreation);
}

/// <summary>
/// One product-side timeout that FIRED: a bound the product armed against a stall elapsed and the
/// product took its timeout path (threw, terminated, or reported).
/// </summary>
/// <param name="Kind">Which terminal fired — a <see cref="TimeoutTerminalKinds"/> value or a
/// module's own constant.</param>
/// <param name="Origin">Where it fired — the hub address, path or query it was armed for.</param>
/// <param name="Message">The terminal's own diagnostic text.</param>
public sealed record TimeoutTerminal(string Kind, string Origin, string Message);

/// <summary>
/// 🚨 Told every time a product-side timeout terminal fires. <b>Production registers none</b>, so a
/// report costs one empty enumeration and nothing else. The test harness registers one per test
/// mesh and FAILS a test in which a terminal fired that the test did not declare — "timeout ==
/// error": a test that passes only because the product recovered from a stall through its own
/// timeout is a stall nobody fixed.
///
/// <para>Implementations are registered as ordinary DI services (an instance owned by the mesh's
/// container, never static) and must not throw: the report is made from inside the product's
/// timeout path.</para>
/// </summary>
public interface ITimeoutTerminalObserver
{
    /// <summary>Called when a timeout terminal fires.</summary>
    /// <param name="terminal">What fired, where, and its message.</param>
    void OnTimeoutTerminal(TimeoutTerminal terminal);
}

/// <summary>
/// The one way product code reports a timeout terminal.
/// </summary>
public static class TimeoutTerminalReporting
{
    private static readonly Action<TimeoutTerminal> NoObservers = static _ => { };

    /// <summary>
    /// Resolves the observers registered in <paramref name="serviceProvider"/> ONCE and returns the
    /// action that tells each of them about a terminal. Resolve it while the container is alive —
    /// where the bound is ARMED — and invoke the returned action where the bound FIRES, which may be
    /// after the container is gone.
    /// </summary>
    /// <param name="serviceProvider">The container the observers are registered in; null yields a
    /// no-op.</param>
    /// <returns>The reporting action; a shared no-op when nothing observes.</returns>
    public static Action<TimeoutTerminal> TimeoutTerminalReporter(this IServiceProvider? serviceProvider)
    {
        if (serviceProvider is null)
            return NoObservers;
        var observers = serviceProvider.GetServices<ITimeoutTerminalObserver>().ToImmutableArray();
        if (observers.IsEmpty)
            return NoObservers;
        return terminal =>
        {
            foreach (var observer in observers)
                observer.OnTimeoutTerminal(terminal);
        };
    }
}
