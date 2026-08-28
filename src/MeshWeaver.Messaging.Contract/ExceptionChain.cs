using System.Collections.Generic;

namespace MeshWeaver.Messaging;

/// <summary>
/// Classifies a failure by walking the exception GRAPH — not the chain.
///
/// <para>🚨 <c>for (var e = ex; e is not null; e = e.InnerException)</c> is the shape everyone
/// writes and it is a TREE flattened to a line. <c>AggregateException.InnerException</c>
/// yields <c>InnerExceptions[0]</c> only, so a fault that carries the interesting exception at any
/// other index is invisible to it — and reactive pipelines produce exactly that: a
/// <c>Merge</c>/<c>WhenAll</c> that saw a real error alongside a teardown hands back an aggregate
/// whose ordering nobody controls. The classification then depends on which fault happened to
/// arrive first, which is the definition of a race.</para>
///
/// <para>Both <see cref="HubDisposingException.IsHubDisposal"/> and MessageHub's scope-teardown
/// classifier route through here so the two cannot drift: they answer different questions about
/// the same graph, and a walker fixed in one place used to leave the other one wrong.</para>
/// </summary>
public static class ExceptionChain
{
    /// <summary>
    /// Bound on how many exceptions one classification will look at.
    ///
    /// <para>🚨 This is a NODE budget, not a depth limit, and the difference is the whole point.
    /// The obvious walker — a <c>for</c> loop over <c>InnerException</c> that also recurses into
    /// <c>InnerExceptions</c> — visits the same chain once per position, because
    /// <c>AggregateException.InnerException</c> IS <c>InnerExceptions[0]</c>. On a graph of nested
    /// aggregates that is exponential: 200 of them pinned a core at 100% CPU indefinitely (caught
    /// by <c>ExceptionChainTest.A_pathologically_deep_graph_terminates</c> while extracting this
    /// walker). A depth cap does not help, since the blow-up is in BREADTH of re-walking, not
    /// depth. The traversal below is iterative, visits each exception at most once, and stops at
    /// this many — so its cost is linear in the graph and bounded regardless of shape.</para>
    /// </summary>
    private const int MaxNodes = 256;

    /// <summary>True when <paramref name="exception"/> is — or carries, at any depth and through
    /// any <see cref="AggregateException"/> branch — an exception of type
    /// <typeparamref name="TException"/>.</summary>
    public static bool Contains<TException>(Exception? exception)
        where TException : Exception
        => Contains(exception, static e => e is TException);

    /// <summary>True when any exception in <paramref name="exception"/>'s graph satisfies
    /// <paramref name="predicate"/>.</summary>
    public static bool Contains(Exception? exception, Func<Exception, bool> predicate)
    {
        if (exception is null)
            return false;

        // Reference identity, because an exception graph is caller-supplied data: it can share
        // nodes or be outright cyclic, and Equals on an exception is reference equality anyway.
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(exception);

        while (pending.Count > 0 && seen.Count < MaxNodes)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
                continue;   // already classified — a shared or cyclic edge, not new information

            if (predicate(current))
                return true;

            if (current is AggregateException aggregate)
            {
                // InnerExceptions is the complete fan-out and its [0] IS InnerException, so
                // pushing both would be the double-walk this method exists to avoid.
                foreach (var inner in aggregate.InnerExceptions)
                    pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return false;
    }
}
