using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.PathResolution.Test;

/// <summary>
/// Isolated diagnostic for the reactive <see cref="IMeshService.Autocomplete"/> conversion.
/// The old test drained <c>AutocompleteAsync(...).ToListAsync()</c> (the COMPLETE merged set);
/// the reactive form emits progressively (each provider <c>.StartWith(empty)</c> via CombineLatest).
/// This records every emission + completion so we can see whether the provider's index lags the
/// just-created nodes (emits empty then completes → <c>.Match(count&gt;=1)</c> never matches).
/// </summary>
public class AutocompleteIsolationTest : MonolithMeshTestBase
{
    private readonly ITestOutputHelper output;

    public AutocompleteIsolationTest(ITestOutputHelper output) : base(output) => this.output = output;

    [Fact]
    public void Autocomplete_Email_RecordsEveryEmission()
    {
        // Arrange — minimal hierarchy (one matching leaf: "Email Triage").
        NodeFactory.CreateNode(MeshNode.FromPath("Systemorph/Marketing") with
        { Name = "Marketing", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing") with
        { Name = "Claims Processing", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(MeshNode.FromPath("Systemorph/Marketing/ClaimsProcessing/EmailTriage") with
        { Name = "Email Triage", NodeType = "Markdown" }).Should().Emit();

        // Act — subscribe directly and record the raw emission stream (count + names + timing).
        var emissions = new List<(double ms, int count, string names)>();
        var sw = Stopwatch.StartNew();
        var done = new ManualResetEventSlim(false);
        var completed = false;
        Exception? error = null;

        using var sub = MeshQuery.Autocomplete("Systemorph/Marketing", "Email", limit: 10)
            .Subscribe(
                r =>
                {
                    emissions.Add((sw.Elapsed.TotalMilliseconds, r.Count, string.Join(", ", r.Select(x => x.Name))));
                    if (r.Count >= 1) done.Set();
                },
                ex => { error = ex; done.Set(); },
                () => { completed = true; done.Set(); });

        done.Wait(TimeSpan.FromSeconds(10));
        // Give a beat for any trailing emission to land so the log is complete.
        Thread.Sleep(200);

        output.WriteLine($"=== reactive Autocomplete('Systemorph/Marketing','Email') ===");
        foreach (var e in emissions)
            output.WriteLine($"  +{e.ms,6:F0}ms  count={e.count}  [{e.names}]");
        output.WriteLine($"completed={completed}  error={error?.GetType().Name ?? "none"}  totalEmissions={emissions.Count}");

        emissions.Should().Contain(e => e.count >= 1,
            "reactive Autocomplete must eventually surface 'Email Triage' (if it only ever emits empty then completes, the index lags the create and the conversion needs a retry/poll)");
    }
}
