using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="SelfTypedDeclarationDurableRepair"/> tolerates a faulted read lane on purpose — one
/// partition that cannot answer must not stop the other lanes healing — but a tolerated fault is
/// still a FAILED step of the pass. It used to be visible only as a Warning beside a summary line
/// that said <c>sweep completed</c>, so the one line an operator reads claimed a clean pass while
/// every self-typed row that lane covered stayed unhealed. The lane now records the failure, and
/// the summary turns into an Error naming it.
/// </summary>
public class SelfTypedDeclarationRepairReportsFailedStepsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public void AFaultedReadLane_IsRecordedAsAFailedStep_NotFoldedIntoACleanCompletion()
    {
        var stats = new SelfTypedDeclarationDurableRepair.SweepStats();
        var completed = false;
        Exception? escaped = null;

        SelfTypedDeclarationDurableRepair.Sweep(
                Observable.Throw<MeshNode>(new InvalidOperationException("42P01: relation \"auth.mesh_nodes\" does not exist")),
                Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
                "partition 'Auth' via test",
                Mesh.JsonSerializerOptions,
                stats,
                logger: null)
            .Subscribe(_ => { }, ex => escaped = ex, () => completed = true);

        escaped.Should().BeNull("the lane is fault-tolerant by design — the next lane must still run");
        completed.Should().BeTrue("Observable.Throw faults synchronously, so the tolerated lane ends synchronously");
        stats.Failures.Should().ContainSingle(
                "a tolerated read fault is a failed step of the pass, and the summary is built from this list")
            .Which.Should().Contain("partition 'Auth' via test").And.Contain("42P01");
    }

    /// <summary>
    /// The second tolerated-fault kind: the lane READS a self-typed declaration row but its retype
    /// WRITE is refused. The row-level Catch keeps the lane going (the next row must still heal),
    /// and records the refused retype as a failed step naming the row and the cause.
    /// </summary>
    [Fact]
    public void ARefusedRetype_IsRecordedAsAFailedStep_NotFoldedIntoACleanCompletion()
    {
        var stats = new SelfTypedDeclarationDurableRepair.SweepStats();
        var completed = false;
        Exception? escaped = null;
        // A declaration row typed as an instance of ITSELF — exactly what the repair retypes.
        var fossil = new MeshNode("SelfTypedFossil")
        {
            NodeType = "SelfTypedFossil",
            Content = new NodeTypeDefinition(),
        };
        NodeTypeDeclarationSelfTypingValidator.IsSelfTypedDeclaration(fossil)
            .Should().BeTrue("the row must be one the sweep tries to retype, or the write is never reached");

        SelfTypedDeclarationDurableRepair.Sweep(
                Observable.Return(fossil),
                RefusingWrites(Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(), out var refusal),
                "path routing via test",
                Mesh.JsonSerializerOptions,
                stats,
                logger: null)
            .Subscribe(_ => { }, ex => escaped = ex, () => completed = true);

        refusal.ArrivalCount.Should().Be(1, "the premise: the retype write reached the store and was refused");
        escaped.Should().BeNull("the row is fault-tolerant by design — the next row must still heal");
        completed.Should().BeTrue("both the source and the refused write fault or complete synchronously");
        stats.Read.Should().Be(1);
        stats.Retyped.Should().Be(0, "the write was refused, so nothing was retyped");
        stats.Failures.Should().ContainSingle(
                "a refused retype is a failed step of the pass, and the summary is built from this list")
            .Which.Should().Contain("retype 'SelfTypedFossil'").And.Contain("path routing via test")
            .And.Contain("write refused by test");
    }

    /// <summary>
    /// The summary line names at most <see cref="SelfTypedDeclarationDurableRepair.RenderedFailureCap"/>
    /// failed steps and folds the rest into a count, so a systematic failure cannot produce one line
    /// long enough for a sink to truncate or drop — and the stats keep only those names plus a
    /// count, so a systematic failure cannot grow memory by one string per failed row either.
    /// </summary>
    [Fact]
    public void TheSummary_NamesTheFirstFailures_AndCountsTheRest_WithBoundedStorage()
    {
        var cap = SelfTypedDeclarationDurableRepair.RenderedFailureCap;
        var stats = new SelfTypedDeclarationDurableRepair.SweepStats();
        foreach (var i in Enumerable.Range(0, cap + 5))
            stats.RecordFailure($"step-{i:D3}");

        stats.FailureCount.Should().Be(cap + 5);
        stats.Failures.Should().HaveCount(cap, "only the names the summary renders are kept");

        var rendered = SelfTypedDeclarationDurableRepair.RenderFailures(stats.Failures, stats.FailureCount);
        rendered.Should().Contain($"step-{cap - 1:D3}").And.NotContain($"step-{cap:D3}")
            .And.Contain("+ 5 more");

        var few = Enumerable.Range(0, cap).Select(i => $"step-{i:D3}").ToImmutableList();
        SelfTypedDeclarationDurableRepair.RenderFailures(few, few.Count)
            .Should().NotContain("more", "a list within the cap is rendered whole");
    }

    /// <summary>
    /// The headline behaviour: the end-of-pass line is Information for a clean pass and Error —
    /// naming the failed step — for a pass that tolerated any fault. Executes the real summary,
    /// so inverting the branch fails here.
    /// </summary>
    [Fact]
    public void TheSummary_IsError_WhenAnyStepFailed_AndInformation_Otherwise()
    {
        var clean = new SelfTypedDeclarationDurableRepair.SweepStats();
        var cleanLog = new CapturingLogger();
        SelfTypedDeclarationDurableRepair.LogSummary(cleanLog, "completed", clean, ["A/B"], "");
        cleanLog.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);

        var failed = new SelfTypedDeclarationDurableRepair.SweepStats();
        failed.RecordFailure("partition 'Auth' via test: 42P01");
        var failedLog = new CapturingLogger();
        SelfTypedDeclarationDurableRepair.LogSummary(failedLog, "completed", failed, ["A/B"], "");
        var entry = failedLog.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error, "a pass with a failed step is not a clean completion");
        entry.Message.Should().Contain("1 FAILED step(s)").And.Contain("partition 'Auth' via test: 42P01");
    }

    /// <summary>
    /// The real adapter with every WRITE (<c>Write</c>, <c>WriteMany</c>, <c>WriteIfVersion</c>)
    /// refused — the fault the per-row Catch exists for. Every other surface forwards, so no interface
    /// default silently replaces the inner adapter's behaviour. Refused synchronously, which the
    /// cases rely on.
    /// </summary>
    private static IStorageAdapter RefusingWrites(IStorageAdapter inner, out FaultSwitch refusal)
    {
        var store = new FaultInjectingStorageAdapter(inner);
        refusal = store.FailOperations(_ => true, StorageLegs.Write,
            _ => new InvalidOperationException("write refused by test"), description: "every path");
        return store;
    }

    /// <summary>Records each line's level and rendered message.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public ImmutableList<(LogLevel Level, string Message)> Entries { get; private set; } =
            ImmutableList<(LogLevel Level, string Message)>.Empty;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries = Entries.Add((logLevel, formatter(state, exception)));
    }
}
