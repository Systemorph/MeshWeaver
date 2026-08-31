using System.Collections.Immutable;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The dependency cascade behind <c>mw-plugin-test build</c>: every node OBSERVES the result
/// streams of the nodes it depends on and starts itself the moment the last of them completes
/// green. Nothing is scheduled by a level table or a topological list; the graph itself is the
/// schedule, so two packages with no edge between them build at the same time and a package with
/// three dependencies starts the instant the slowest one lands — the natural cascade the
/// maintainer asked for (2026-08-30: "we always observe stream for dependencies then start
/// ourselves").
///
/// <para><b>Red breaks, green continues.</b> A node whose dependency did not end green never
/// starts: it completes at once as <see cref="NodeOutcome.Blocked"/>, naming the dependency that
/// stopped it, and that verdict cascades to its own dependents. A failure is therefore reported
/// exactly once at the node that produced it, and every node above it reads "blocked by X" rather
/// than a second, derived failure.</para>
///
/// <para><b>One execution per node, however many dependents.</b> A node's stream is
/// <see cref="Observable.PublishLast{TSource}(IObservable{TSource})"/>-shared: the first subscriber
/// starts the work, every later subscriber receives the same single result, and nobody ever
/// re-runs a package because two packages both required it.</para>
///
/// <para><b>Bounded parallelism without a blocking wait.</b> Work is handed to one queue and
/// executed through <c>Merge(maxParallel)</c>: Rx subscribes at most that many work items at a
/// time and takes the next when one completes. No semaphore, no thread parked on a wait — a
/// blocking bridge is exactly what the production inventory refuses.</para>
///
/// <para><b>Timings are part of the result.</b> Each outcome carries when the node became
/// READY (its last dependency completed), when it STARTED (a slot was granted) and when it
/// FINISHED, so a report can show queue time separately from work time and compute the critical
/// path — the maintainer wants the numbers, not a green wall.</para>
///
/// <para>The cascade is generic and pure so it can be tested without a compiler: the work
/// function is injected and the graph is plain ids.</para>
/// </summary>
public static class Cascade
{
    /// <summary>The verdict of one node of the cascade.</summary>
    public enum NodeOutcome
    {
        /// <summary>The work ran and succeeded; dependents may start.</summary>
        Green,
        /// <summary>The work ran and failed; dependents are blocked.</summary>
        Red,
        /// <summary>The work never ran because a dependency was not green.</summary>
        Blocked,
        /// <summary>The work threw — treated as red for dependents, but reported apart.</summary>
        Faulted,
    }

    /// <summary>One node's result: the verdict, the work's own payload, and the three timestamps.</summary>
    /// <typeparam name="T">The payload the work function produces on a run (null when blocked).</typeparam>
    /// <param name="Id">The node id.</param>
    /// <param name="Outcome">The verdict.</param>
    /// <param name="Result">The work's payload, when the work ran.</param>
    /// <param name="BlockedBy">The dependency that stopped this node, when blocked.</param>
    /// <param name="Error">The exception text, when faulted.</param>
    /// <param name="Ready">When the last dependency completed (relative to the cascade's start).</param>
    /// <param name="Started">When the work began — equals <paramref name="Ready"/> when no slot wait.</param>
    /// <param name="Finished">When the work ended (or the block was decided).</param>
    public sealed record NodeResult<T>(
        string Id,
        NodeOutcome Outcome,
        T? Result,
        string? BlockedBy,
        string? Error,
        TimeSpan Ready,
        TimeSpan Started,
        TimeSpan Finished)
    {
        /// <summary>Time spent waiting for a slot after every dependency was in.</summary>
        public TimeSpan Queued => Started - Ready;

        /// <summary>Time the work itself took.</summary>
        public TimeSpan Work => Finished - Started;

        /// <summary>True when dependents may start.</summary>
        public bool IsGreen => Outcome == NodeOutcome.Green;
    }

    /// <summary>
    /// Runs the cascade over <paramref name="ids"/>, where <paramref name="dependenciesOf"/> names
    /// each id's direct dependencies (ids outside the set are ignored — they are external, and
    /// whoever built the set decided how they are satisfied) and <paramref name="run"/> does one
    /// node's work, returning true for green. Emits every node's result once all have settled.
    /// </summary>
    /// <param name="ids">The node ids in the cascade.</param>
    /// <param name="dependenciesOf">Direct dependencies per id.</param>
    /// <param name="run">The work: given the id and its dependencies' results, produce the payload
    /// and whether it is green. Runs on the scheduler, at most <paramref name="maxParallel"/> at a
    /// time.</param>
    /// <param name="maxParallel">Concurrency cap for the work function. Blocking a node costs no slot.</param>
    /// <param name="scheduler">Where the work runs; the task pool by default.</param>
    /// <typeparam name="T">The work's payload type.</typeparam>
    public static IObservable<ImmutableArray<NodeResult<T>>> Run<T>(
        IReadOnlyCollection<string> ids,
        Func<string, IReadOnlyList<string>> dependenciesOf,
        Func<string, IReadOnlyList<NodeResult<T>>, (T Result, bool Green)> run,
        int maxParallel,
        IScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(dependenciesOf);
        ArgumentNullException.ThrowIfNull(run);
        if (maxParallel < 1)
            throw new ArgumentOutOfRangeException(nameof(maxParallel), maxParallel, "at least one slot");

        return Observable.Defer(() =>
        {
            var idSet = ids.ToImmutableHashSet(StringComparer.Ordinal);
            if (idSet.IsEmpty)
                return Observable.Return(ImmutableArray<NodeResult<T>>.Empty);
            if (FindCycle(idSet, dependenciesOf) is { } cycle)
                return Observable.Throw<ImmutableArray<NodeResult<T>>>(new InvalidOperationException(
                    $"the dependency graph has a cycle: {cycle} — a cascade over it would never "
                    + "start, because every node in the cycle waits for another one in it"));

            var clock = Stopwatch.StartNew();
            var pool = scheduler ?? TaskPoolScheduler.Default;

            // 🚨 FAIL-FAST across the whole graph ("when build fails => exit", maintainer,
            // 2026-09-01): dependents were always Blocked on a red dependency, but INDEPENDENT
            // nodes kept earning slots after the run's verdict was already RED — on CI that is
            // minutes of work whose only possible outcome is a redder red. The first failure
            // stamps itself here; a work item that reaches the gate afterwards refuses its slot
            // and reports Blocked by that first failure. Nodes already RUNNING finish (their
            // verdicts are real); nothing new starts.
            var firstFailure = new string[1];

            // The concurrency gate. Every node hands its work here when it becomes ready;
            // Merge(maxParallel) subscribes at most that many at a time and takes the next as one
            // completes. The gate is subscribed BEFORE any node, so nothing is handed to a queue
            // nobody is listening to.
            var queue = new Subject<IObservable<Unit>>();
            var gate = queue.Merge(maxParallel).Subscribe(_ => { }, _ => { });

            var nodes = new Dictionary<string, IObservable<NodeResult<T>>>(StringComparer.Ordinal);

            // Build the streams lazily and memoise them: a node's stream is created once and the
            // same instance is handed to every dependent, so PublishLast shares one execution.
            IObservable<NodeResult<T>> NodeOf(string id)
            {
                if (nodes.TryGetValue(id, out var existing))
                    return existing;

                var deps = dependenciesOf(id).Where(idSet.Contains).Distinct(StringComparer.Ordinal).ToArray();
                var upstream = deps.Length == 0
                    ? Observable.Return<IList<NodeResult<T>>>(Array.Empty<NodeResult<T>>())
                    : deps.Select(NodeOf).CombineLatest();

                var node = upstream
                    .Take(1)
                    .SelectMany(depResults =>
                    {
                        var ready = clock.Elapsed;
                        var stopper = depResults.FirstOrDefault(d => !d.IsGreen);
                        if (stopper is not null)
                        {
                            // Blocked at once, no slot: the verdict is derived, not earned.
                            return Observable.Return(new NodeResult<T>(
                                id, NodeOutcome.Blocked, default, stopper.Id, null,
                                ready, ready, ready));
                        }
                        var ordered = depResults.ToArray();
                        // The result travels back through its own subject; the work item handed
                        // to the gate is deferred so it starts when the gate subscribes it —
                        // that moment is 'Started', and Started - Ready is the slot wait.
                        var result = new AsyncSubject<NodeResult<T>>();
                        var work = Observable.Defer(() => Observable.Start(() =>
                            {
                                var started = clock.Elapsed;
                                if (Volatile.Read(ref firstFailure[0]) is { } failedFirst)
                                    return new NodeResult<T>(
                                        id, NodeOutcome.Blocked, default, failedFirst, null,
                                        ready, started, clock.Elapsed);
                                try
                                {
                                    var (payload, green) = run(id, ordered);
                                    if (!green)
                                        Interlocked.CompareExchange(ref firstFailure[0], id, null);
                                    return new NodeResult<T>(
                                        id, green ? NodeOutcome.Green : NodeOutcome.Red, payload, null, null,
                                        ready, started, clock.Elapsed);
                                }
                                catch (Exception ex)
                                {
                                    Interlocked.CompareExchange(ref firstFailure[0], id, null);
                                    return new NodeResult<T>(
                                        id, NodeOutcome.Faulted, default, null,
                                        $"{ex.GetType().Name}: {ex.Message}", ready, started, clock.Elapsed);
                                }
                            }, pool))
                            .Do(r =>
                            {
                                result.OnNext(r);
                                result.OnCompleted();
                            })
                            .Select(_ => Unit.Default);
                        queue.OnNext(work);
                        return result;
                    })
                    .PublishLast()
                    .AutoConnect();
                nodes[id] = node;
                return node;
            }

            var all = idSet.OrderBy(i => i, StringComparer.Ordinal).Select(NodeOf).ToArray();
            return all.CombineLatest()
                .Take(1)
                .Select(results => results.OrderBy(r => r.Id, StringComparer.Ordinal).ToImmutableArray())
                .Finally(() =>
                {
                    queue.OnCompleted();
                    gate.Dispose();
                });
        });
    }

    /// <summary>
    /// The longest chain of work in the results (by finish time of the last node on it) — the
    /// wall-clock floor no amount of parallelism can beat. Returned leaf-first.
    /// </summary>
    public static ImmutableArray<string> CriticalPath<T>(
        ImmutableArray<NodeResult<T>> results, Func<string, IReadOnlyList<string>> dependenciesOf)
    {
        if (results.IsEmpty)
            return [];
        var byId = results.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var last = results.MaxBy(r => r.Finished)!;
        var path = new List<string>();
        var current = last;
        while (true)
        {
            path.Add(current.Id);
            var deps = dependenciesOf(current.Id)
                .Where(byId.ContainsKey)
                .Select(d => byId[d])
                .ToArray();
            if (deps.Length == 0)
                break;
            // The dependency that finished last is the one this node waited for.
            current = deps.MaxBy(d => d.Finished)!;
        }
        path.Reverse();
        return [.. path];
    }

    private static string? FindCycle(
        ImmutableHashSet<string> ids, Func<string, IReadOnlyList<string>> dependenciesOf)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 visiting, 2 done
        var stack = new List<string>();
        string? Visit(string id)
        {
            if (state.TryGetValue(id, out var s))
            {
                if (s != 1)
                    return null;
                var at = stack.IndexOf(id);
                return string.Join(" → ", stack.Skip(at).Append(id));
            }
            state[id] = 1;
            stack.Add(id);
            foreach (var dep in dependenciesOf(id).Where(ids.Contains))
            {
                if (Visit(dep) is { } found)
                    return found;
            }
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
            return null;
        }
        foreach (var id in ids)
        {
            if (Visit(id) is { } found)
                return found;
        }
        return null;
    }
}
