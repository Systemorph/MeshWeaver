using MeshWeaver.Fixture;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// Pins the ONE property that made issue #890 undiagnosable for a month: an exception logged
/// by the framework must leave its TYPE and its STACK in a file CI actually keeps.
///
/// <para>Before this, every stack-bearing sink was invisible to CI — the compile abort's
/// activity-log entry lives on a MeshNode that is never uploaded, and its
/// <c>ILogger.LogWarning(exception, …)</c> reached only xUnit's <c>ITestOutputHelper</c>
/// (which the trx logger does not persist) and an opt-in per-method file that is off in CI.
/// So a recurrence reported <c>Object reference not set to an instance of an object.</c> and
/// nothing else. <see cref="TestTraceLog.AppendFault(string, LogLevel, string, Exception)"/>
/// is the sink that closes that gap.</para>
/// </summary>
public class TestTraceLogFaultTest
{
    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception ex)
        {
            // Thrown-and-caught so the exception carries a real stack trace — a
            // constructed-but-never-thrown exception has StackTrace == null and would
            // make this test pass while proving nothing.
            return ex;
        }
    }

    [Fact]
    public void AppendFault_WritesExceptionTypeAndStackToTheCollectedTraceFile()
    {
        var marker = $"fault-probe-{Guid.NewGuid():N}";
        var exception = Thrown(marker);

        TestTraceLog.AppendFault(
            "MeshWeaver.Graph.Configuration.MeshNodeCompilationService",
            LogLevel.Warning,
            "Compile failure for TestData/SomeType",
            exception);

        // Read with FileShare.ReadWrite: this file is append-only and shared by every test
        // host in a shard, so another writer may hold it open.
        using var stream = new FileStream(
            TestTraceLog.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var contents = reader.ReadToEnd();

        Assert.Contains(marker, contents, StringComparison.Ordinal);
        // The exception TYPE — the single field whose absence forced #890's triage to guess.
        Assert.Contains(nameof(InvalidOperationException), contents, StringComparison.Ordinal);
        // A stack frame naming this test — proves frames survive, not just the message.
        Assert.Contains(nameof(Thrown), contents, StringComparison.Ordinal);
        // The category and level, so a reader can tell which component faulted.
        Assert.Contains("MeshNodeCompilationService", contents, StringComparison.Ordinal);
    }

    private static string ReadTraceLog()
    {
        // Read with FileShare.ReadWrite: this file is append-only and shared by every test
        // host in a shard, so another writer may hold it open.
        using var stream = new FileStream(
            TestTraceLog.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Issue #982: a fault the budget drops must leave the file SAYING so. The file is the only
    /// diagnostic that survives a wedge, so a reader who cannot tell a truncated log from a
    /// complete one draws the wrong conclusion from a silence that is really a discard.
    ///
    /// <para>Uses its own tiny <see cref="FaultRecordBudget"/> rather than the process-wide one:
    /// exhausting the real budget here would suppress a genuine fault logged later in this same
    /// test host — the test would create the defect it is pinning.</para>
    /// </summary>
    [Fact]
    public void ASuppressedFault_IsAnnouncedInTheCollectedTraceFile()
    {
        var budget = new FaultRecordBudget(recordsPerWindow: 1, TimeSpan.FromMinutes(5));
        var now = DateTime.UtcNow;
        var kept = $"fault-kept-{Guid.NewGuid():N}";
        var dropped = $"fault-dropped-{Guid.NewGuid():N}";

        TestTraceLog.AppendFault(budget, now, "Probe", LogLevel.Warning, kept, Thrown(kept));
        TestTraceLog.AppendFault(budget, now, "Probe", LogLevel.Warning, dropped, Thrown(dropped));

        var contents = ReadTraceLog();

        Assert.Contains(kept, contents, StringComparison.Ordinal);
        // Really dropped — otherwise this test would pass on a sink that never suppresses.
        Assert.DoesNotContain(dropped, contents, StringComparison.Ordinal);

        // …and the drop is stated, in a line a reader can find with one grep. Asserted on the
        // slice AFTER our own kept record so a notice written by another test in this shared
        // file cannot stand in for the one this test is about.
        var tail = contents[(contents.IndexOf(kept, StringComparison.Ordinal) + kept.Length)..];
        Assert.Contains("[FAULT-BUDGET]", tail, StringComparison.Ordinal);
        Assert.Contains("this log is NOT a complete record of faults",
            tail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of #982: the budget refills, so the fault that fires next to the wedge —
    /// long after an earlier storm spent the allowance — still reaches the file. Under the
    /// lifetime cap this record was dropped for the rest of the process.
    /// </summary>
    [Fact]
    public void AFaultAfterAnEarlierStorm_StillReachesTheCollectedTraceFile()
    {
        var window = TimeSpan.FromSeconds(10);
        var budget = new FaultRecordBudget(recordsPerWindow: 1, window);
        var now = DateTime.UtcNow;
        var late = $"fault-late-{Guid.NewGuid():N}";

        TestTraceLog.AppendFault(budget, now, "Probe", LogLevel.Warning, "burst", Thrown("burst"));
        for (var i = 0; i < 50; i++)
            TestTraceLog.AppendFault(budget, now, "Probe", LogLevel.Warning, "burst", Thrown("burst"));

        // The wedge, ten minutes later.
        TestTraceLog.AppendFault(
            budget, now + TimeSpan.FromMinutes(10), "Probe", LogLevel.Warning, late, Thrown(late));

        var contents = ReadTraceLog();

        Assert.Contains(late, contents, StringComparison.Ordinal);
        // And the record that survived says how much of the burst was lost before it.
        Assert.Contains("resuming fault records after suppressing 50 fault records",
            contents, StringComparison.Ordinal);
    }

    /// <summary>
    /// The file the assertion above reads is the one the CI workflow copies into
    /// <c>collected-logs/</c>; if the path ever drifts from that contract the artifact silently
    /// stops carrying faults again.
    /// </summary>
    [Fact]
    public void TraceLogPath_IsTheFileCiCollects()
        => Assert.Equal("meshweaver-test-trace.log", Path.GetFileName(TestTraceLog.Path));
}
