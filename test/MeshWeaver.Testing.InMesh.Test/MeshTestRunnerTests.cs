using System.Threading;
using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Testing.InMesh;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Testing.InMesh.Test;

/// <summary>
/// The runner's own contract: it finds [MeshFact]/[MeshTheory] cases, runs sync and async bodies,
/// reports a failure with the assertion's message, a timeout by name, a skip as ⏭ (never green),
/// expands inline data, and renders the "N/M passed" title plus the ✅/❌ table the plugin gate parses.
/// </summary>
public class MeshTestRunnerTests
{
    // 🚨 THE CASES BELOW ARE THE SUBJECT, not tests of this assembly — the in-mesh runner discovers
    // and runs them, and this file asserts what it reports. `Hangs` is untokened ON PURPOSE (#4378):
    // it parks forever so the runner's OWN TimeoutSeconds can be observed, and giving it xunit's
    // token would end it for the wrong reason and prove nothing. It is the xUnit1069 shape — a timed
    // case whose work outlives its verdict — and the runner must NAME it, not merely time out.
    // `HangsButObserves` / `TheoryObserves` are the fixed shape: the runner's token ends their wait.
    public class Sample
    {
        [MeshFact] public void Passes() { }
        [MeshFact(DisplayName = "an async case")] public async Task PassesAsync() { await Task.Delay(1); }
        [MeshFact] public void Fails() => throw new InvalidOperationException("the assertion message");
        [MeshFact(Skip = "not today")] public void Skipped() => throw new Exception("never runs");
        [MeshFact(TimeoutSeconds = 1)] public async Task Hangs() => await Task.Delay(Timeout.InfiniteTimeSpan);
        [MeshFact(TimeoutSeconds = 1)] public async Task HangsButObserves(CancellationToken ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        [MeshTheory] [MeshInlineData(1, 2)] [MeshInlineData(2, 3)] public void Adds(int a, int expected) { if (a + 1 != expected) throw new Exception($"{a}+1 != {expected}"); }
        [MeshTheory(TimeoutSeconds = 1)] [MeshInlineData(3)] public async Task TheoryObserves(int n, CancellationToken ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    [Fact]
    public async Task Runs_every_case_shape_and_renders_the_gate_contract()
    {
        var results = await MeshTestRunner.Run(null, [typeof(Sample)], TestTimeouts.Quick).ToList();
        var byName = results.ToDictionary(r => r.Name);
        Assert.Equal(9, results.Count);
        Assert.True(byName["Passes"].Passed);
        Assert.True(byName["an async case"].Passed);
        Assert.StartsWith("❌", byName["Fails"].Result); Assert.Contains("the assertion message", byName["Fails"].Detail);
        Assert.True(byName["Skipped"].Skipped); Assert.Equal("not today", byName["Skipped"].Detail);
        Assert.StartsWith("❌", byName["Hangs"].Result); Assert.Contains("no verdict within 1s", byName["Hangs"].Detail);
        Assert.True(byName["Adds(1, 2)"].Passed); Assert.True(byName["Adds(2, 3)"].Passed);

        // The bound CANCELS: a case that observes the runner's token unwinds and the verdict says so;
        // one that ignores it is still running after the grace and the verdict names THAT — the
        // runner cannot stop what the case started, so the report is the whole remedy.
        Assert.Contains("IGNORED its cancellation token", byName["Hangs"].Detail);
        Assert.StartsWith("❌", byName["HangsButObserves"].Result); Assert.Contains("cancelled and unwound", byName["HangsButObserves"].Detail);
        Assert.DoesNotContain("IGNORED", byName["HangsButObserves"].Detail);
        Assert.StartsWith("❌", byName["TheoryObserves(3)"].Result); Assert.Contains("cancelled and unwound", byName["TheoryObserves(3)"].Detail);
        // Elapsed separates the two shapes without a literal: an observing case's verdict lands at its
        // 1 s bound, inside the grace; an ignoring case's verdict lands only after the grace expired.
        Assert.True(byName["HangsButObserves"].Elapsed < MeshTestRunner.CancellationGrace, $"an observing case ends with its verdict, not after the grace: {byName["HangsButObserves"].Elapsed}");
        Assert.True(byName["Hangs"].Elapsed >= MeshTestRunner.CancellationGrace, $"an ignoring case is reported only after the grace: {byName["Hangs"].Elapsed}");

        var list = results.ToList();
        Assert.Equal("Sample tests — 4/8 passed · 1 skipped", MeshTestRunner.Summary("Sample", list));   // 9 cases, 1 skipped → 8 counted, 4 green
        var table = MeshTestRunner.Table(list);
        Assert.Contains("| ✅ pass |", table); Assert.Contains("| ❌ FAIL |", table); Assert.Contains("| ⏭ skipped |", table);
        Assert.NotNull(MeshTestRunner.Render("Sample", list));
    }

    // 🚨 SUBJECT, not a test of this assembly. `AIgnoresItsToken` is the xUnit1069 shape with an END:
    // it never observes the runner's token, so it outlives its 1 s bound and the grace, and it still
    // holds its pool slot while the next case is due — but it finishes by itself, so the pool below
    // can be disposed. `ZRunsAfterTheLeak` sorts after it and is what the leak must not take down.
    public class Leaky
    {
        [MeshFact(TimeoutSeconds = 1)] public async Task AIgnoresItsToken() => await Task.Delay(MeshTestRunner.CancellationGrace * 3);
        [MeshFact] public void ZRunsAfterTheLeak() { }
    }

    /// <summary>
    /// #4719 review: on a ONE-slot Tests pool a case that ignores its token kept the only slot, and
    /// every later case queued behind it and died as an anonymous "no verdict" without executing —
    /// invisible to the host-less test above, which runs on <see cref="IoPool.Unbounded"/>. The pool
    /// is not the serializer (the runner is), so a pool with room runs the next case normally.
    /// </summary>
    [Fact]
    public async Task A_case_that_ignores_its_token_does_not_take_the_next_case_down_with_it()
    {
        using var pool = new IoPool(new IoPoolOptions().MaxConcurrencyFor(IoPoolNames.Tests));
        var results = await MeshTestRunner.Run(null, [typeof(Leaky)], TestTimeouts.Quick, pool).ToList();
        var byName = results.ToDictionary(r => r.Name);

        Assert.Contains("IGNORED its cancellation token", byName["AIgnoresItsToken"].Detail);
        Assert.True(byName["ZRunsAfterTheLeak"].Passed,
            $"the configured Tests pool has room for a leaked case, so the next case must RUN and pass: {byName["ZRunsAfterTheLeak"].Detail}");
        Assert.True(new IoPoolOptions().MaxConcurrencyFor(IoPoolNames.Tests) > 1,
            "a one-slot Tests pool lets a single token-ignoring case block every later case of every suite on the mesh");
    }

    /// <summary>
    /// And when leaks DO fill the pool, the next case is reported at once as not run, naming the
    /// leaked case — never as an anonymous timeout thirty seconds later.
    /// </summary>
    [Fact]
    public async Task A_pool_filled_by_leaked_cases_is_named_not_timed_out()
    {
        using var pool = new IoPool(1);
        var results = await MeshTestRunner.Run(null, [typeof(Leaky)], TestTimeouts.Quick, pool).ToList();
        var blocked = results.Single(r => r.Name == "ZRunsAfterTheLeak");

        Assert.StartsWith("❌", blocked.Result);
        Assert.Contains("not run", blocked.Detail);
        Assert.Contains("Leaky.AIgnoresItsToken", blocked.Detail);
        Assert.DoesNotContain("no verdict within", blocked.Detail);
        Assert.Equal(TimeSpan.Zero, blocked.Elapsed);
    }

    [Fact]
    public void Discovers_only_classes_with_cases()
    {
        var classes = MeshTestRunner.TestClasses(typeof(Sample).Assembly);
        Assert.Contains(typeof(Sample), classes);
        Assert.DoesNotContain(typeof(MeshTestRunnerTests), classes);   // xunit [Fact]s are not [MeshFact]s
    }
}
