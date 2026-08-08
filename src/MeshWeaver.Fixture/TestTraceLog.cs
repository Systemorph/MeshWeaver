using Microsoft.Extensions.Logging;

namespace MeshWeaver.Fixture;

/// <summary>
/// The one cross-process test trace file — <c>%TEMP%/meshweaver-test-trace.log</c>.
///
/// <para>This is the ONLY test log CI keeps. The workflow's "Collect test logs for
/// artifact" step copies it to <c>collected-logs/_meshweaver-test-trace.log</c> and
/// uploads it, and — critically — it survives the case where nothing else does: when a
/// shard is killed by its wall-clock cap (<c>exit=124</c>) no <c>.trx</c> is written at
/// all, so a wedge's only surviving evidence is whatever reached this file.</para>
///
/// <para>Two writers share it, which is why the path and the lock live here rather than
/// on either one: <c>MonolithMeshTestBase</c>'s per-test phase trace, and
/// <see cref="AppendFault"/> below. A second writer holding its OWN lock would interleave
/// mid-line with the first and corrupt both.</para>
/// </summary>
public static class TestTraceLog
{
    /// <summary>
    /// Fixed path so the CI collect step can always find it, and so a developer can
    /// <c>tail -f</c> it during a hung suite run.
    /// </summary>
    public static string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meshweaver-test-trace.log");

    private static readonly object Gate = new();

    /// <summary>
    /// Appends one already-formatted line. Best-effort: tracing must never throw out of
    /// the test pipeline.
    /// </summary>
    /// <param name="line">The line to append (a newline is added).</param>
    public static void Append(string line)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, line + Environment.NewLine);
        }
        catch
        {
            // Best-effort — never break a test on a trace I/O failure.
        }
    }

    /// <summary>
    /// Flushes/creates the file so preceding lines reach disk before an
    /// <c>Environment.FailFast</c>.
    /// </summary>
    public static void Touch()
    {
        try { File.AppendAllText(Path, string.Empty); } catch { /* best-effort */ }
    }

    // Bound on the number of fault records this process may append. A test process that
    // storms (a resubscribe loop logging a warning per iteration) would otherwise write
    // the artifact until the runner's disk fills — the 2026-07 colima ENOSPC failure mode.
    // This bounds the ARTIFACT, not the diagnosis: the first faults in a cascade are the
    // ones that name the cause; the thousandth repeat adds nothing.
    // Sized against measurement, not a guess: one deliberately-failing compile test
    // (CodeEditRecompileTest.FailedCompile_…) emits 15 fault records, so a per-shard budget
    // has to be in the thousands to still be holding the record that names a LATE fault.
    private const int MaxFaultRecords = 1000;
    private const int MaxExceptionChars = 2500;
    private static int _faultRecordsWritten;

    /// <summary>
    /// Appends one fault record — level, category, message and the exception's full
    /// <c>ToString()</c> (type + stack).
    ///
    /// <para>🚨 This exists because an exception logged through <see cref="ILogger"/>
    /// reaches NO artifact CI keeps. <see cref="XUnitFileLogger"/> forwards to xUnit's
    /// <c>ITestOutputHelper</c> (which the trx logger does not persist) and to a
    /// per-test-method file that is opt-in via <c>MESHWEAVER_TEST_FILE_LOGS</c> and
    /// therefore OFF in CI — and it drops the record entirely when no test method is
    /// active, which is exactly the wedge case. That is why the recurring compile abort
    /// of issue #890 was undiagnosable: the framework logged the exception object with
    /// its stack, and every sink that carried the stack was invisible to CI.</para>
    /// </summary>
    /// <param name="category">The logger category name.</param>
    /// <param name="level">The record's severity.</param>
    /// <param name="message">The formatted log message.</param>
    /// <param name="exception">The exception whose type and stack to record.</param>
    public static void AppendFault(string category, LogLevel level, string message, Exception exception)
    {
        var written = Interlocked.Increment(ref _faultRecordsWritten);
        if (written > MaxFaultRecords)
        {
            if (written == MaxFaultRecords + 1)
                Append($"{DateTime.UtcNow:HH:mm:ss.fff} pid={Environment.ProcessId} [FAULT] "
                    + $"further fault records suppressed after {MaxFaultRecords} in this process");
            return;
        }

        var detail = exception.ToString();
        if (detail.Length > MaxExceptionChars)
            detail = detail[..MaxExceptionChars] + "… (truncated)";

        // pid on the header line for the same reason the phase trace carries it: every
        // test project in a shard appends to this one file, and a core dump is named
        // dotnet-<pid>.dmp.
        Append($"{DateTime.UtcNow:HH:mm:ss.fff} pid={Environment.ProcessId} [FAULT] "
            + $"[{level}] [{category}] {message}{Environment.NewLine}{detail}");
    }
}
