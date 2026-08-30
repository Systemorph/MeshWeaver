using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The cascade's contract, tested without a compiler: the graph is ids, the work is a delegate.
/// Every invariant the maintainer stated on 2026-08-30 has a case here — build = one unit per
/// node; on red we break; on green we continue; a node observes its dependencies and starts
/// itself; independent nodes run in parallel; every node runs exactly once; timings are real.
/// </summary>
public class CascadeTest
{
    private static readonly IReadOnlyDictionary<string, string[]> Diamond =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Store"] = [],
            ["AI"] = ["Store"],
            ["Observability"] = ["Store"],
            ["Edu"] = ["Store", "AI"],
        };

    private static IReadOnlyList<string> DepsOf(string id) =>
        Diamond.TryGetValue(id, out var d) ? d : [];

    [Fact]
    public void ANodeStartsOnlyAfterEveryDependencyIsGreen_AndDependentsSeeTheirResults()
    {
        var seen = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
        var results = Cascade.Run<string>(
            Diamond.Keys.ToArray(), DepsOf,
            (id, deps) =>
            {
                seen[id] = deps.Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                return ($"built {id}", true);
            },
            maxParallel: 4).Wait();

        Assert.Equal(4, results.Length);
        Assert.All(results, r => Assert.True(r.IsGreen, $"{r.Id}: every node's work returned green, so nothing is red or blocked"));
        Assert.Empty(seen["Store"]);
        Assert.Equal(["Store"], seen["AI"]);
        Assert.Equal(["AI", "Store"], seen["Edu"]); // Edu observes both dependencies' streams and starts only after both completed

        var byId = results.ToDictionary(r => r.Id, StringComparer.Ordinal);
        Assert.True(byId["AI"].Ready >= byId["Store"].Finished, "a node is READY no earlier than its last dependency FINISHED — that is the cascade");
        Assert.True(byId["Edu"].Ready >= byId["AI"].Finished);
        Assert.Equal("built Edu", byId["Edu"].Result);
    }

    [Fact]
    public void OnRedWeBreak_TheFailureIsReportedOnceAndEveryDependentIsBlockedByName()
    {
        var ran = new ConcurrentBag<string>();
        var results = Cascade.Run<string>(
            Diamond.Keys.ToArray(), DepsOf,
            (id, _) =>
            {
                ran.Add(id);
                return (id, id != "AI"); // AI fails
            },
            maxParallel: 4).Wait();

        var byId = results.ToDictionary(r => r.Id, StringComparer.Ordinal);
        Assert.Equal(Cascade.NodeOutcome.Green, byId["Store"].Outcome);
        Assert.Equal(Cascade.NodeOutcome.Red, byId["AI"].Outcome); // AI's own work failed
        Assert.Equal(Cascade.NodeOutcome.Blocked, byId["Edu"].Outcome); // Edu requires AI; on red we break
        Assert.Equal("AI", byId["Edu"].BlockedBy); // the block names the dependency that stopped it
        Assert.Equal(Cascade.NodeOutcome.Green, byId["Observability"].Outcome); // requires only Store: on green we continue
        Assert.DoesNotContain("Edu", ran); // a blocked node's work must never run
        Assert.Equal(TimeSpan.Zero, byId["Edu"].Work); // a derived verdict costs no work time
    }

    [Fact]
    public void AFaultingWorkFunctionIsReportedAsFaulted_NotAsACrashOfTheWholeBuild()
    {
        var results = Cascade.Run<string>(
            Diamond.Keys.ToArray(), DepsOf,
            (id, _) => id == "Store" ? throw new InvalidOperationException("disk on fire") : (id, true),
            maxParallel: 2).Wait();

        var byId = results.ToDictionary(r => r.Id, StringComparer.Ordinal);
        Assert.Equal(Cascade.NodeOutcome.Faulted, byId["Store"].Outcome);
        Assert.Contains("InvalidOperationException", byId["Store"].Error); Assert.Contains("disk on fire", byId["Store"].Error);
        Assert.All(results.Where(r => r.Id != "Store"), r => { Assert.Equal(Cascade.NodeOutcome.Blocked, r.Outcome); Assert.Equal("Store", r.BlockedBy); }); // all require Store
    }

    [Fact]
    public void EveryNodeRunsExactlyOnce_HoweverManyDependentsShareIt()
    {
        var runs = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var wide = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["Base"] = [] };
        for (var i = 0; i < 20; i++)
            wide[$"Leaf{i:D2}"] = ["Base"];

        Cascade.Run<int>(
            wide.Keys.ToArray(), id => wide[id],
            (id, _) =>
            {
                runs.AddOrUpdate(id, 1, (_, n) => n + 1);
                return (0, true);
            },
            maxParallel: 8).Wait();

        Assert.Equal(21, runs.Count);
        Assert.All(runs.Values, n => Assert.Equal(1, n)); // twenty dependents, ONE execution of Base
    }

    [Fact]
    public void IndependentNodesRunInParallel_BoundedByTheSlotCount()
    {
        const int nodes = 6;
        const int slots = 3;
        var inFlight = 0;
        var peak = 0;
        var flat = Enumerable.Range(0, nodes).Select(i => $"N{i}").ToArray();

        var results = Cascade.Run<int>(
            flat, _ => [],
            (_, _) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                int seen;
                do
                {
                    seen = Volatile.Read(ref peak);
                } while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);
                Thread.Sleep(80);
                Interlocked.Decrement(ref inFlight);
                return (0, true);
            },
            maxParallel: slots).Wait();

        Assert.Equal(nodes, results.Length); Assert.All(results, r => Assert.True(r.IsGreen));
        Assert.True(peak > 1, "nodes with no edge between them run at the same time");
        Assert.True(peak <= slots, $"peak {peak} exceeded the slot cap {slots}");
        Assert.Contains(results, r => r.Queued > TimeSpan.Zero); // six nodes on three slots: somebody waited
    }

    [Fact]
    public void ACycleIsRefusedUpFront_NamingIt()
    {
        var cyclic = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["A"] = ["B"],
            ["B"] = ["C"],
            ["C"] = ["A"],
        };
        Action act = () => _ = Cascade.Run<int>(cyclic.Keys.ToArray(), id => cyclic[id], (_, _) => (0, true), 2).Wait();
        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("cycle", ex.Message); Assert.Contains("A", ex.Message); Assert.Contains("C", ex.Message);
    }

    [Fact]
    public void DependenciesOutsideTheSelectionAreExternal_AndDoNotBlock()
    {
        // 'AI' requires 'Store', but only 'AI' is in the cascade: Store is somebody else's
        // (the image, --module, another repo). AI must start on its own.
        var results = Cascade.Run<int>(
            ["AI"], DepsOf, (_, _) => (1, true), 2).Wait();
        Assert.True(Assert.Single(results).IsGreen);
    }

    [Fact]
    public void CriticalPathIsTheChainThatEndsLast_LeafFirst()
    {
        var results = Cascade.Run<int>(
            Diamond.Keys.ToArray(), DepsOf,
            (id, _) =>
            {
                Thread.Sleep(id == "AI" ? 120 : 20);
                return (0, true);
            },
            maxParallel: 4).Wait();

        var path = Cascade.CriticalPath(results, DepsOf);
        Assert.Equal(["Store", "AI", "Edu"], path); // Edu finishes last; waited for AI (slow), which waited for Store
    }

    [Fact]
    public void AnEmptySelectionIsAnEmptyReport()
    {
        Assert.Empty(Cascade.Run<int>([], _ => [], (_, _) => (0, true), 1).Wait());
    }
}
