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
/// nothing else. <see cref="TestTraceLog.AppendFault"/> is the sink that closes that gap.</para>
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

    /// <summary>
    /// The file the assertion above reads is the one the CI workflow copies into
    /// <c>collected-logs/</c>; if the path ever drifts from that contract the artifact silently
    /// stops carrying faults again.
    /// </summary>
    [Fact]
    public void TraceLogPath_IsTheFileCiCollects()
        => Assert.Equal("meshweaver-test-trace.log", Path.GetFileName(TestTraceLog.Path));
}
