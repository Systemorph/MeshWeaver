using System.Threading;
using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Testing.InMesh;
using MeshWeaver.Fixture;
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

    [Fact]
    public void Discovers_only_classes_with_cases()
    {
        var classes = MeshTestRunner.TestClasses(typeof(Sample).Assembly);
        Assert.Contains(typeof(Sample), classes);
        Assert.DoesNotContain(typeof(MeshTestRunnerTests), classes);   // xunit [Fact]s are not [MeshFact]s
    }
}
