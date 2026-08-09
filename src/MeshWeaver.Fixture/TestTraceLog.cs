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
/// <see cref="AppendFault(string, LogLevel, string, Exception)"/> below.
/// A second writer holding its OWN lock would interleave
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

    // ── Fault-record budget ────────────────────────────────────────────────────────────
    // Bounds the write RATE, because a storm (a resubscribe loop logging a warning per
    // iteration filling the runner's disk — the 2026-07 colima ENOSPC failure mode) IS a
    // rate. It deliberately does NOT bound the lifetime total: a total, once spent, silences
    // the sink for the rest of the process and takes the late fault next to the wedge with
    // it, which is the only record anyone ever reads (issue #982). See FaultRecordBudget for
    // the full argument, including why keeping the tail in a ring buffer is not an option
    // here (a process killed by `timeout` never flushes one).
    //
    // Sized against the #982 measurement, not a guess: a healthy shard process emits ~1500
    // fault records across a ≤20-minute shard — order 1.3/s. 100 per 10s is ~7x that, so a
    // healthy run never suppresses, while a genuine storm (thousands/s) is clamped to 10/s.
    // The artifact cost is then bounded by WALL CLOCK rather than by an arbitrary total:
    // 10 records/s x ~3 KB ≈ 1.8 MB/min, and a shard's own 20-minute cap (its projects run
    // sequentially in one loop) puts the ceiling near 36 MB uncompressed — a few MB in the
    // artifact at the workflow's compression-level 9.
    private const int FaultRecordsPerWindow = 100;
    private static readonly TimeSpan FaultWindow = TimeSpan.FromSeconds(10);

    // Process-wide, and deliberately so, despite the no-static-state rule: this sink IS a
    // per-process artifact (CI collects one file per runner) and its writers — fixture
    // construction, teardown, and reactive continuations on background schedulers — belong to
    // no mesh and outlive every mesh in the process. A mesh-scoped budget would reset on each
    // test class and so would not bound a storm that spans classes, which is the hazard. It
    // needs no Clear()-for-test-isolation hook either: the policy is exercised in tests
    // through its own instances (FaultRecordBudget), never through this one.
    private static readonly FaultRecordBudget FaultBudget = new(FaultRecordsPerWindow, FaultWindow);

    private const int MaxExceptionChars = 2500;

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
    ///
    /// <para>Records are rate-bounded by <see cref="FaultRecordBudget"/>, so a storming process
    /// has some suppressed — but never a late one, and never silently: each suppressed stretch
    /// is announced in the file by a <c>[FAULT-BUDGET]</c> line carrying the count, so
    /// <c>grep FAULT-BUDGET</c> answers "is this log complete?".</para>
    /// </summary>
    /// <param name="category">The logger category name.</param>
    /// <param name="level">The record's severity.</param>
    /// <param name="message">The formatted log message.</param>
    /// <param name="exception">The exception whose type and stack to record.</param>
    public static void AppendFault(string category, LogLevel level, string message, Exception exception)
        => AppendFault(FaultBudget, DateTime.UtcNow, category, level, message, exception);

    /// <summary>
    /// The <see cref="AppendFault(string, LogLevel, string, Exception)"/> body with its budget
    /// and clock supplied — the seam tests use to exercise suppression deterministically
    /// against their OWN <see cref="FaultRecordBudget"/>, without sleeping and without
    /// spending the process's real budget (which would suppress a genuine fault logged later
    /// in the same run).
    /// </summary>
    /// <param name="budget">The budget to claim from.</param>
    /// <param name="now">The record's timestamp.</param>
    /// <param name="category">The logger category name.</param>
    /// <param name="level">The record's severity.</param>
    /// <param name="message">The formatted log message.</param>
    /// <param name="exception">The exception whose type and stack to record.</param>
    public static void AppendFault(
        FaultRecordBudget budget,
        DateTime now,
        string category,
        LogLevel level,
        string message,
        Exception exception)
    {
        var verdict = budget.Claim(now);

        // The notice is what stops a truncated log from reading like a complete one, so it is
        // written whether or not the record itself survives — and it is tagged so a reader can
        // answer "did this file lose anything?" with one grep.
        if (verdict.Notice is not null)
            Append($"{now:HH:mm:ss.fff} pid={Environment.ProcessId} [FAULT-BUDGET] {verdict.Notice}");

        if (!verdict.WriteRecord)
            return;

        var detail = exception.ToString();
        if (detail.Length > MaxExceptionChars)
            detail = detail[..MaxExceptionChars] + "… (truncated)";

        // pid on the header line for the same reason the phase trace carries it: every
        // test project in a shard appends to this one file, and a core dump is named
        // dotnet-<pid>.dmp.
        Append($"{now:HH:mm:ss.fff} pid={Environment.ProcessId} [FAULT] "
            + $"[{level}] [{category}] {message}{Environment.NewLine}{detail}");
    }
}
