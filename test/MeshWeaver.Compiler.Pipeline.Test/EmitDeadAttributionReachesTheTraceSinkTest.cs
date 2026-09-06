using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The #890 attribution line must carry the EXCEPTION OBJECT, because that — and nothing about
/// its wording or its level — is what decides whether any CI artifact can ever show it.
///
/// <para><b>The defect.</b> A poisoned process reports as up to ten unrelated test failures; across
/// 2026-08-22→08-29 that was 9 events wearing 37 distinct test names, 23 % of all failing test names
/// in the week, and every occurrence cost a fresh and always-identical misdiagnosis.
/// <c>PROCESS CANNOT EMIT (#890)</c> is the one line that exists to stop that — *"attribute the
/// failures that follow to this line, not to the change under test"*. It was logged with **no
/// exception**, and a CI shard's two sinks are not equivalent:</para>
///
/// <list type="bullet">
///   <item>the <b>job log</b> is written post-hoc and carries only the output of tests that
///     <b>FAILED</b> — and #890's first canary record is routinely printed under a test that
///     PASSED (measured: <c>CreateLayoutAreaIntegrationTest.CreateArea_WithoutTypeParam_ShowsTypeSelection</c>
///     in the calibrated core occurrence, which was not one of that shard's five failures);</item>
///   <item>the complete sink is the phase trace <c>_meshweaver-test-trace.log</c>, which takes a
///     record <b>if and only if</b> <c>exception is not null &amp;&amp; logLevel &gt;= Warning</c>
///     — <c>XUnitFileLogger.Log</c> → <c>TestTraceLog.AppendFault</c>.</item>
/// </list>
///
/// <para>So the loudest statement this defect produces was the <b>least durable</b> one: absent from
/// the trace by construction, and absent from the job log whenever the test that logged it passed.
/// Meanwhile its quieter sibling — <c>"Compile failure for {HubPath}"</c>, which passes
/// <c>outcome.Error</c> — always survives. The documented triage pattern <c>PROCESS CANNOT EMIT</c>
/// was therefore a guaranteed <b>zero</b> on the sink the triage docs call authoritative.</para>
///
/// <para>🚨 <b>What this file pins is the sink predicate, not the prose.</b> Asserting "we passed an
/// exception" would be tautological. The assertions below evaluate the trace sink's own condition
/// against the captured record, so the test fails for the reason that actually matters — the record
/// would not be written to the file CI keeps. Reverting the fix (dropping the exception argument in
/// <see cref="NodeTypeCompilationHelpers.LogTerminalNonVerdict"/>) turns
/// <c>ProcessCannotEmit_IsRecordedByTheTraceSinkPredicate</c> red naming the sink.</para>
///
/// <para>The <see cref="LogLevel.Information"/> branch is deliberately NOT changed and is pinned
/// here too: an availability fact about one node is not an error, its level is a production cost
/// contract, and nobody needs to find it later. Only the process-wide claim is durable.</para>
/// </summary>
public class EmitDeadAttributionReachesTheTraceSinkTest
{
    private const string Site =
        "NamedTypeSymbol.Microsoft.Cci.ITypeDefinitionMember.get_ContainingTypeDefinition";

    private static string Threw(string site = Site)
        => $"THREW NullReferenceException at {site}: Object reference not set to an instance of an object.";

    /// <summary>The real thing, built the way <c>EmitPipeline</c> builds it: the ORIGINAL exception
    /// with the canary verdict on <see cref="Exception.Data"/>, never a wrapper.</summary>
    private static Exception EmitThrewWith(string verdict)
    {
        var error = new NullReferenceException("Object reference not set to an instance of an object.");
        error.Data[EmitPipeline.EmitCanaryDataKey] = verdict;
        return error;
    }

    // Derived from EmitPipeline.Verdict, never hand-typed, so a wording change cannot leave this
    // file asserting against strings that no longer exist. The two CLAIMING verdicts are the two
    // EmitPipeline.IsProcessEmitFailure admits; DIVERGENT withholds because the legs died in
    // different frames.
    public static TheoryData<string, string> ClaimingVerdicts() => new()
    {
        { EmitPipeline.Verdict(Threw(), Threw()), "canary=BELOW-ROSLYN" },
        { EmitPipeline.Verdict(Threw(), "OK"), "canary=REFERENCES" },
    };

    public static TheoryData<string> WithholdingVerdicts() =>
    [
        EmitPipeline.Verdict(Threw(), Threw("SomethingElse.Unrelated")),  // DIVERGENT
        "canary=OK (a trivial nested-generic emit against the SAME reference set still succeeds)",
        "canary=INCONCLUSIVE shared:THREW … pristine:UNAVAILABLE(no CoreLib on disk)",
    ];

    /// <summary>
    /// The trace sink's gate, restated exactly as <c>XUnitFileLogger.Log</c> applies it before
    /// <c>TestTraceLog.AppendFault</c>. A record that does not satisfy this NEVER appears in
    /// <c>collected-logs/_meshweaver-test-trace.log</c>, whatever it says.
    /// </summary>
    private static bool ReachesTheTraceSink(Record record)
        => record.Exception is not null && record.Level >= LogLevel.Warning;

    /// <summary>
    /// The fix, asserted at the sink rather than at the call. A confirmed process-wide emit failure
    /// must produce a record the trace sink will actually write.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClaimingVerdicts))]
    public void ProcessCannotEmit_IsRecordedByTheTraceSinkPredicate(string verdict, string expected)
    {
        var logger = new RecordingLogger();
        var error = EmitThrewWith(verdict);

        NodeTypeCompilationHelpers.LogTerminalNonVerdict(logger, "TestData/BrokenType", error);

        var record = Assert.Single(logger.Records);

        Assert.True(ReachesTheTraceSink(record),
            "the #890 attribution line must satisfy the trace sink's gate "
            + "(exception is not null && level >= Warning — XUnitFileLogger.Log → "
            + "TestTraceLog.AppendFault). Without it the line reaches ONLY the post-hoc job log, "
            + "which carries the output of FAILED tests only — and this defect's first canary "
            + "record is routinely logged under a test that passed, so the occurrence would be "
            + $"attributable from no CI artifact at all. Captured: level={record.Level}, "
            + $"exception={record.Exception?.GetType().Name ?? "<none>"}.");

        // It must be THE terminal exception, not a fresh one: the stack is what a recurrence is
        // triaged from, and #612's whole finding was that message-only logging left this
        // undiagnosable.
        Assert.Same(error, record.Exception);

        // And it must still be the line triage greps for, carrying the verdict it is about.
        Assert.Contains("PROCESS CANNOT EMIT (#890)", record.Message);
        Assert.Contains(expected, record.Message);
    }

    /// <summary>
    /// 🚨 Non-vacuity. Only two of the canary's five verdicts prove the PROCESS is at fault. A
    /// change that made every emit-phase abort shout would hand the bake gate the blind spot
    /// <c>SourceSnapshotEstablishmentTest.EveryOtherCompileFailure_StillStampsError</c> exists to
    /// refuse — so the withholding verdicts must stay quiet, and stay OUT of the trace.
    /// </summary>
    [Theory]
    [MemberData(nameof(WithholdingVerdicts))]
    public void AWithholdingVerdict_StaysInformation_AndDoesNotReachTheTraceSink(string verdict)
    {
        var logger = new RecordingLogger();

        NodeTypeCompilationHelpers.LogTerminalNonVerdict(
            logger, "TestData/BrokenType", EmitThrewWith(verdict));

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.DoesNotContain("PROCESS CANNOT EMIT", record.Message);
        Assert.False(ReachesTheTraceSink(record),
            "an availability fact about ONE node is not a process-wide claim and must not be "
            + "promoted into the fault sink — that widening is the blind spot the verdict-reading "
            + "predicate exists to refuse.");
    }

    /// <summary>
    /// The other availability non-verdict shape — the compile never reached Roslyn at all, so there
    /// is no canary and nothing process-wide to claim. Pinned so the extraction cannot start
    /// shouting about an unestablished source set.
    /// </summary>
    [Fact]
    public void AnUnestablishedSourceSet_IsNotAProcessWideClaim()
    {
        var logger = new RecordingLogger();

        NodeTypeCompilationHelpers.LogTerminalNonVerdict(
            logger, "TestData/BrokenType", new InvalidOperationException("source set not established"));

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.False(ReachesTheTraceSink(record));
    }

    private sealed record Record(LogLevel Level, string Message, Exception? Exception);

    /// <summary>Captures level, formatted message AND the exception — the third is the whole
    /// point: the two existing recording loggers in this project drop it, which is exactly how a
    /// missing exception argument stayed invisible.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<Record> records = [];

        public IReadOnlyList<Record> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => records.Add(new Record(logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
