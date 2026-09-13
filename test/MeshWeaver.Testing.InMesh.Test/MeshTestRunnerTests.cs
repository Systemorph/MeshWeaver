using System.Threading;
using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Testing.InMesh;
using Xunit;

namespace MeshWeaver.Testing.InMesh.Test;

/// <summary>
/// The runner's own contract: it finds [MeshFact]/[MeshTheory] cases, runs sync and async bodies,
/// reports a failure with the assertion's message, a timeout by name, a skip as ⏭ (never green),
/// expands inline data, and renders the "N/M passed" title plus the ✅/❌ table the plugin gate parses.
/// </summary>
public class MeshTestRunnerTests
{
    public class Sample
    {
        [MeshFact] public void Passes() { }
        [MeshFact(DisplayName = "an async case")] public async Task PassesAsync() { await Task.Delay(1); }
        [MeshFact] public void Fails() => throw new InvalidOperationException("the assertion message");
        [MeshFact(Skip = "not today")] public void Skipped() => throw new Exception("never runs");
        [MeshFact(TimeoutSeconds = 1)] public async Task Hangs() => await Task.Delay(Timeout.InfiniteTimeSpan);
        [MeshTheory] [MeshInlineData(1, 2)] [MeshInlineData(2, 3)] public void Adds(int a, int expected) { if (a + 1 != expected) throw new Exception($"{a}+1 != {expected}"); }
    }

    [Fact]
    public async Task Runs_every_case_shape_and_renders_the_gate_contract()
    {
        var results = await MeshTestRunner.Run(null, [typeof(Sample)], TimeSpan.FromSeconds(5)).ToList();
        var byName = results.ToDictionary(r => r.Name);
        Assert.Equal(7, results.Count);
        Assert.True(byName["Passes"].Passed);
        Assert.True(byName["an async case"].Passed);
        Assert.StartsWith("❌", byName["Fails"].Result); Assert.Contains("the assertion message", byName["Fails"].Detail);
        Assert.True(byName["Skipped"].Skipped); Assert.Equal("not today", byName["Skipped"].Detail);
        Assert.StartsWith("❌", byName["Hangs"].Result); Assert.Contains("no verdict within 1s", byName["Hangs"].Detail);
        Assert.True(byName["Adds(1, 2)"].Passed); Assert.True(byName["Adds(2, 3)"].Passed);

        var list = results.ToList();
        Assert.Equal("Sample tests — 4/6 passed · 1 skipped", MeshTestRunner.Summary("Sample", list));   // 7 cases, 1 skipped → 6 counted, 4 green
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
