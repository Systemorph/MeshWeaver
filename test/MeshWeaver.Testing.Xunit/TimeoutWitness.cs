using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Xunit.Sdk;

namespace MeshWeaver.Testing.Xunit;

/// <summary>
/// 🚨 Writes down WHERE a timed-out test was stuck (MeshWeaver.Plugins#4424).
///
/// <para>A timeout ends the wait for a test. It cannot end the test's thread: a test held
/// synchronously inside a call (a lock, a blocking wait, a spin) keeps holding that thread after
/// xunit has failed it. The failure message names the test, but not the call. So when a test fails
/// with <see cref="FailureCause.Timeout"/>, this writes one dump of the process while the stuck thread
/// is still parked in its frame. It uses the runtime's own dump writer over its diagnostic port
/// (<see cref="DiagnosticsClient.WriteDump(DumpType, string, bool)"/>): a <see cref="DumpType.Normal"/>
/// minidump, thread stacks and modules but no heap.</para>
///
/// <para><b>Where it goes.</b> The dump goes to <see cref="DumpDirectoryOverride"/>, else the directory
/// of <c>DOTNET_DbgMiniDumpName</c> (every CI test lane sets it to <c>/tmp/coredumps/…</c> and uploads
/// that directory with a red leg's evidence), else the temp directory. One line naming the test, the
/// timeout that fired (the test's own, which for a test or theory row that declares one differs from
/// the assembly bound) and the dump path is written to the phase trace (<see cref="TracePath"/>, the log that
/// survives a killed host) and to standard error. A dump that cannot be written
/// is reported on that same line, with the reason. The test's verdict is xunit's and is never changed.</para>
///
/// <para><b>Bounded.</b> At most <see cref="MaxDumps"/> dumps per process: a run whose every test
/// times out must not fill the runner's disk. Past the budget the line still names the test and says
/// that no dump was written.</para>
/// </summary>
/// <param name="inner">The sink every message is forwarded to, unchanged.</param>
/// <param name="boundMilliseconds">The assembly's per-test bound, reported only when xunit did not say which
/// timeout the failed test ran under.</param>
public sealed class TimeoutWitness(IMessageSink inner, int boundMilliseconds) : IMessageSink
{
    /// <summary>The environment variable naming the directory timeout dumps are written to.</summary>
    public const string DumpDirectoryOverride = "MESHWEAVER_TEST_DUMP_DIR";

    /// <summary>The phase name of the trace line a timed-out test leaves.</summary>
    public const string Phase = "TEST_TIMEOUT";

    /// <summary>How many dumps one process writes at most.</summary>
    public const int MaxDumps = 3;

    private readonly ConcurrentDictionary<string, (string Class, string Method)> cases = new();
    private readonly ConcurrentDictionary<string, int> timeouts = new();
    private int dumpsWritten;

    /// <inheritdoc/>
    public bool OnMessage(IMessageSinkMessage message)
    {
        switch (message)
        {
            case ITestCaseStarting starting:
                cases[starting.TestCaseUniqueID] = (
                    starting.TestClassSimpleName ?? starting.TestClassName ?? "?",
                    starting.TestMethodName ?? starting.TestCaseDisplayName);
                break;
            case ITestStarting test:
                // The timeout this test actually runs under: its own (a [Fact(Timeout = …)] or a theory
                // row's), else the bound MethodBound gave its case.
                timeouts[test.TestUniqueID] = test.Timeout;
                break;
            case ITestFailed { Cause: FailureCause.Timeout } failed:
                Witness(failed);
                break;
            case ITestFinished finished:
                timeouts.TryRemove(finished.TestUniqueID, out _);
                break;
        }
        return inner.OnMessage(message);
    }

    private void Witness(ITestFailed failed)
    {
        var (testClass, method) = cases.TryGetValue(failed.TestCaseUniqueID, out var known) ? known : ("?", failed.TestUniqueID);
        var timeout = timeouts.TryGetValue(failed.TestUniqueID, out var fired) && fired > 0 ? fired : boundMilliseconds;
        string outcome;
        var ordinal = Interlocked.Increment(ref dumpsWritten);
        if (ordinal > MaxDumps)
        {
            outcome = $"dump=none (the budget of {MaxDumps} dumps for this process is spent)";
        }
        else
        {
            var path = Path.Combine(DumpDirectory(), string.Create(CultureInfo.InvariantCulture,
                $"test-timeout-{Environment.ProcessId}-{ordinal}-{Sanitize(testClass)}.{Sanitize(method)}.dmp"));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                new DiagnosticsClient(Environment.ProcessId).WriteDump(DumpType.Normal, path, logDumpGeneration: false);
                outcome = $"dump={path}";
            }
            catch (Exception ex)
            {
                // Reported, never swallowed: the line says no dump exists and why. The verdict is unchanged.
                outcome = $"dump=none (writing {path} failed: {ex.GetType().Name}: {ex.Message})";
            }
        }
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:HH:mm:ss.fff} pid={Environment.ProcessId} [{testClass}] {Phase} {method} bound={timeout}ms {outcome}");
        Console.Error.WriteLine(line);
        try
        {
            File.AppendAllText(TracePath, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported, and the message is still forwarded below: a trace that cannot be written must
            // never keep the timed-out test's verdict from the reporter.
            Console.Error.WriteLine($"[{Phase}] the line above did not reach {TracePath}: {ex.Message}");
        }
    }

    /// <summary>The environment variable naming the phase trace (the variable the CI kill-time reader uses).</summary>
    public const string TraceOverride = "MESHWEAVER_TEST_TRACE";

    /// <summary>
    /// The phase trace the test fixtures write (<c>MeshWeaver.Fixture.TestTraceLog</c>, the temp
    /// directory's <c>meshweaver-test-trace.log</c>), or <see cref="TraceOverride"/> when set.
    /// </summary>
    public static string TracePath =>
        Environment.GetEnvironmentVariable(TraceOverride) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "meshweaver-test-trace.log");

    /// <summary>The directory a timeout dump is written to.</summary>
    public static string DumpDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(DumpDirectoryOverride);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var crashDumpName = Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName");
        var crashDumpDirectory = string.IsNullOrWhiteSpace(crashDumpName) ? null : Path.GetDirectoryName(crashDumpName);
        return string.IsNullOrEmpty(crashDumpDirectory) ? Path.GetTempPath() : crashDumpDirectory;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c is ' ' or '(' or ')' or ',' or '"' ? '_' : c).ToArray();
        var s = new string(chars);
        return s.Length > 80 ? s[..80] : s;
    }
}
