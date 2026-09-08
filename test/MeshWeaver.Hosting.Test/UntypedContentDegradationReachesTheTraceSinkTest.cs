using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The reachability control for <c>check-untyped-content.sh</c>.</b> The gate greps a shard's
/// <c>collected-logs/</c> for a content degradation. For its first five days it could not fire at
/// all, and the reason had nothing to do with the grep: the record it looks for could not reach the
/// directory it scans (MeshWeaver#3625).
///
/// <para><b>The mechanism.</b> Both degradation warnings in <c>MeshNodeStreamCache</c> were logged
/// at <see cref="LogLevel.Warning"/> with <b>no exception object</b>. The sink that writes
/// <c>collected-logs/_meshweaver-test-trace.log</c> takes a record <b>if and only if</b>
/// <c>exception is not null &amp;&amp; logLevel &gt;= Warning</c> (<c>XUnitFileLogger.Log</c> and
/// <c>LoggingBuilderExtensions.Log</c> → <c>TestTraceLog.AppendFault</c>). So the phrase could reach
/// that file by construction never — measured on a run that really did degrade content: 817 trace
/// records naming the test class, ZERO occurrences of the phrase it emitted. The other route into
/// the directory (per-method <c>test-logs/*.log</c>) is opt-in via <c>MESHWEAVER_TEST_FILE_LOGS</c>,
/// which core's <c>dotnet-test.yml</c> never sets.</para>
///
/// <para><b>Why the job log is not an equivalent sink</b> — the same half that made #890
/// undiagnosable: it carries only the output of tests that <b>FAILED</b>, and a content degradation
/// is overwhelmingly logged under a test that PASSES. That is the entire point of the defect class:
/// nothing throws.</para>
///
/// <para>🚨 <b>What this file pins is the SINK PREDICATE, not the call.</b> Asserting "we passed an
/// exception" would be tautological — it restates the fix in the fix's own terms. The assertions
/// below restate the sink's own condition and evaluate it against a record captured from the
/// <b>production</b> converter, so the test fails for the reason that actually matters: the record
/// would not be written to the file CI keeps. Modelled on
/// <c>EmitDeadAttributionReachesTheTraceSinkTest</c>, which exists for the same trap one diagnostic
/// over — and which the maintainer named as the template for this one.</para>
///
/// <para>🚨 It captures through its OWN recording logger and never touches <c>TestTraceLog</c>.
/// That is deliberate: a control arm that wrote a real degradation into the shared trace file would
/// red every shard, which is the objection that kept this control from being written in the first
/// place (<c>UntypedContentDegradationGate</c> says so in its own summary). The chain is therefore
/// split cleanly — <b>production emitter → record</b> is exercised here, <b>record → file</b> is
/// <c>TestTraceLog.AppendFault</c>'s own contract, and <b>file → verdict</b> is the script,
/// falsified directly against a trace file carrying a real record.</para>
///
/// <para><b>No mocking.</b> The discriminating variable is a REAL
/// <see cref="MeshContentTypeRegistry"/> — empty in the degraded arm, carrying the probe type in the
/// readable one. Same node, same converter, same options; the only difference is whether the mesh
/// knows the type, which is precisely the condition the warning reports.</para>
/// </summary>
public class UntypedContentDegradationReachesTheTraceSinkTest
{
    /// <summary>A content record that exists only for this test — a stand-in for the
    /// dynamically-compiled NodeType content the registry was built to recover.</summary>
    public record DegradationProbeContent(string Title);

    private const string ProbeNodeType = "Probe/ContentDegradationNodeType";
    private const string ProbePath = "Space/ARenderedEmptyPage";

    /// <summary>The node's content, as storage hands it to the cache: raw JSON carrying the short-name
    /// <c>$type</c> discriminator the owning hub stamped.</summary>
    private static MeshNode ProbeNode() =>
        new("ARenderedEmptyPage", "Space")
        {
            NodeType = ProbeNodeType,
            Content = JsonSerializer.Deserialize<JsonElement>(
                $$"""{"$type":"{{nameof(DegradationProbeContent)}}","title":"a view that renders empty"}"""),
        };

    /// <summary>
    /// The trace sink's gate, restated exactly as <c>XUnitFileLogger.Log</c> and
    /// <c>LoggingBuilderExtensions.Log</c> apply it before <c>TestTraceLog.AppendFault</c>. A record
    /// that does not satisfy this NEVER appears in <c>collected-logs/_meshweaver-test-trace.log</c>,
    /// whatever it says.
    /// </summary>
    private static bool ReachesTheTraceSink(Record record)
        => record.Exception is not null && record.Level >= LogLevel.Warning;

    /// <summary>The two read seams that can degrade content — <c>GetStream</c>'s converter and
    /// <c>GetQuery</c>'s. A degradation reachable through only one of them is still a view that
    /// renders empty, so both are driven.</summary>
    public static TheoryData<string> Seams() => ["GetStream", "GetQuery"];

    private static void RunSeam(
        string seam, MeshNode node, ILogger logger, IMeshContentTypeRegistry? registry)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _ = seam switch
        {
            "GetStream" => MeshNodeStreamCache.ConvertContentJsonElementToTyped(node, options, logger, registry),
            "GetQuery" => MeshNodeStreamCache.DeserializeContent(node, options, logger, registry),
            _ => throw new ArgumentOutOfRangeException(nameof(seam), seam, "unknown read seam"),
        };
    }

    /// <summary>
    /// The fix, asserted at the sink rather than at the call: a real degradation, produced by the
    /// production converter, must yield a record the trace sink will actually write — otherwise the
    /// shard gate that scans that file has nothing to match and passes having checked nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seams))]
    public void ADegradationIsRecordedByTheTraceSinkPredicate(string seam)
    {
        var logger = new RecordingLogger();

        // An EMPTY registry — the mesh has never seen this content type, which is the whole
        // condition the warning reports.
        RunSeam(seam, ProbeNode(), logger, new MeshContentTypeRegistry());

        // 🚨 The POPULATION is "records the sink would write", not "records the logger saw"
        // (Copilot review). Asserting Single over everything couples this control to any future
        // Info/Debug line the converter might add — a record the sink drops anyway, so a failure
        // there would say nothing about reachability. Filtering by the sink's own predicate keeps
        // the control sharp where it matters: a SECOND warning-with-exception still fails it,
        // because two records reaching the sink is a real change to what CI sees.
        var record = Assert.Single(logger.Records.Where(ReachesTheTraceSink));

        Assert.True(ReachesTheTraceSink(record),
            "a content degradation must satisfy the trace sink's gate "
            + "(exception is not null && level >= Warning — XUnitFileLogger.Log → "
            + "TestTraceLog.AppendFault). Without it the warning reaches NO artifact CI keeps: not "
            + "collected-logs/_meshweaver-test-trace.log, and not the job log either, which carries "
            + "only the output of tests that FAILED — while a degradation does not throw and is "
            + "therefore logged under a test that PASSES. check-untyped-content.sh scans exactly "
            + "that directory, so a record that cannot reach it leaves the gate permanently green "
            + $"(#3625). Captured: level={record.Level}, "
            + $"exception={record.Exception?.GetType().Name ?? "<none>"}.");

        // The gate's PRIMARY key is this type, so the record must actually carry it — any other
        // exception would satisfy the sink predicate and still leave the gate matching nothing.
        var degraded = Assert.IsType<MeshNodeContentDegradedException>(record.Exception);
        Assert.Equal(ProbePath, degraded.NodePath);
        Assert.Equal(ProbeNodeType, degraded.NodeType);
        Assert.Contains("renders empty", degraded.RawJson);
        Assert.Contains(seam, degraded.Seam);

        // …and the prose net still works, so a per-test file log (MESHWEAVER_TEST_FILE_LOGS, which
        // carries the formatted message with no exception attached at all) stays covered.
        Assert.Contains("stayed an untyped JsonElement", record.Message);
    }

    /// <summary>
    /// 🚨 Non-vacuity, and it is a genuine discrimination rather than a different input: the SAME
    /// node through the SAME converter, with the content type REGISTERED. The registry recovers it,
    /// the content is typed, and nothing is logged. Without this arm "the record reaches the sink"
    /// would also be satisfied by a converter that shouted on every node — which would red every
    /// shard and get the gate deleted, costing exactly as much as one that never fires.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seams))]
    public void RecoveredContentProducesNoRecordAtAll(string seam)
    {
        var logger = new RecordingLogger();
        var registry = new MeshContentTypeRegistry();
        registry.Register(typeof(DegradationProbeContent), ProbeNodeType);

        RunSeam(seam, ProbeNode(), logger, registry);

        // Same population as the positive arm, for the same reason: what must be empty is what the
        // trace sink would WRITE. (It is in fact empty of everything — the seams log nothing on the
        // recovered path — but pinning the whole set would make this arm fail for reasons that have
        // no bearing on the gate.)
        Assert.Empty(logger.Records.Where(ReachesTheTraceSink));
    }

    private sealed record Record(LogLevel Level, string Message, Exception? Exception);

    /// <summary>Captures level, formatted message AND the exception — the third is the whole point:
    /// a recording logger that drops the exception is exactly how a missing exception argument
    /// stays invisible.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<Record> records = [];

        public IReadOnlyList<Record> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        // 🚨 The sink's own floor, and the recorder HONOURS it (Copilot review). Two notes, because
        // the obvious reading of this change is wrong: on its own, narrowing IsEnabled would have
        // changed nothing at all — ILogger.LogWarning(...) and friends call ILogger.Log
        // unconditionally, so a recorder whose Log records everything never consults IsEnabled. The
        // brittleness Copilot names is real, but the thing that fixes it is filtering the ASSERTED
        // population by the sink predicate (above); this pair makes the recorder a faithful logger
        // rather than a tee, so what it holds is what the sink would consider.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            records.Add(new Record(logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
