#pragma warning disable CS1591

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The gate binary's STARTUP-FAILURE contract, asserted on the real process rather than on a
/// method — because the defect these pin (#1741) is not in any method's return value, it is that
/// the process never ends.
///
/// <para>🚨 Why a process test and why every wait here is BOUNDED. Every consumer runs this
/// binary as a container's PID 1 (<c>docker run … --entrypoint /app/mw-plugin-test</c>). An
/// exception escaping <c>Main</c> there does not terminate the process: the runtime prints the
/// trace and calls <c>abort()</c>, whose SIGABRT the kernel DISCARDS for a PID-namespace init with
/// the default disposition (<c>SIGNAL_UNKILLABLE</c>); <c>abort()</c> falls through to its trap
/// instruction, the runtime's SIGTRAP handler returns to the instruction that trapped, and the
/// main thread re-traps forever. Measured 2026-08-17: two containers "Up" 36 and 57 minutes at
/// ~100% CPU, whose entire output was one <see cref="FileNotFoundException"/> printed in their
/// first second. On CI that reads as a HANG — the job burns its whole timeout and reports nothing
/// about the bad argument that caused it.</para>
///
/// <para>So an unbounded <c>WaitForExit()</c> here would reproduce the bug rather than catch it:
/// the test would hang exactly as CI does. Every wait carries <see cref="ExitBudget"/>, and
/// blowing it FAILS the test naming the spin.</para>
/// </summary>
public class StartupFailureProcessTest
{
    /// <summary>
    /// How long the tool gets to fail and exit. Deliberately generous — the assertion is
    /// "terminates at all", not "terminates in N ms", and a tight bound would turn a loaded CI box
    /// into a flake. The bug it catches is unbounded, so any finite budget catches it.
    /// </summary>
    private static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(60);

    // ── the missing --allow file ─────────────────────────────────────────────────────────────

    [Fact]
    public void MissingAllowFile_ExitsNonZero_NamingTheFlagAndThePath()
    {
        var missing = Path.Combine(
            Path.GetTempPath(), $"mw-plugin-test-no-such-ratchet-{Guid.NewGuid():N}.allow");
        Assert.False(File.Exists(missing), "the fixture path must not exist");

        var run = Run("/nonexistent-repo-root", "--allow", missing);

        Assert.Equal(2, run.ExitCode);
        // The flag that named it, and the path it looked for — the two facts the raw
        // FileNotFoundException stack trace made the reader dig for.
        Assert.Contains("--allow", run.Output, StringComparison.Ordinal);
        Assert.Contains(missing, run.Output, StringComparison.Ordinal);
        // …and how "no known debt" is actually spelled, so the message is actionable.
        Assert.Contains("empty file", run.Output, StringComparison.Ordinal);
        // Nothing escaped Main: no unhandled-exception banner, no stack frames.
        Assert.DoesNotContain("Unhandled exception", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedAllowFile_ExitsNonZero_NamingTheOffendingLine()
    {
        var file = Path.Combine(
            Path.GetTempPath(), $"mw-plugin-test-malformed-{Guid.NewGuid():N}.allow");
        File.WriteAllText(file, "Claims flakiness\n");   // 'flakiness' is not a check name
        try
        {
            var run = Run("/nonexistent-repo-root", "--allow", file);

            Assert.Equal(2, run.ExitCode);
            Assert.Contains("malformed", run.Output, StringComparison.Ordinal);
            Assert.Contains("line 1", run.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", run.Output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ── the guard itself: an UNANTICIPATED startup throw ─────────────────────────────────────

    /// <summary>
    /// The other half of #1741, and the half that generalises: a bad <c>--compile-timeout</c>
    /// value throws <see cref="FormatException"/> out of <c>double.Parse</c>. Before the guard
    /// that escaped <c>Main</c> and spun the container exactly like the missing allow file did —
    /// same defect, different argument. It must now be a printed error and an exit code.
    /// </summary>
    [Fact]
    public void UnparseableTimeout_ExitsNonZero_WithAFatalMessageRatherThanEscapingMain()
    {
        var run = Run("/nonexistent-repo-root", "--compile-timeout", "not-a-number");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("FATAL", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", run.Output, StringComparison.Ordinal);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────

    private sealed record RunResult(int ExitCode, string Output);

    /// <summary>
    /// Runs the gate binary that sits beside this test assembly (the ProjectReference copies its
    /// apphost, deps.json and runtimeconfig.json into this output directory) and returns its exit
    /// code plus stdout+stderr, waiting at most <see cref="ExitBudget"/>.
    /// </summary>
    private static RunResult Run(params string[] arguments)
    {
        var exe = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "mw-plugin-test.exe" : "mw-plugin-test");
        Assert.True(
            File.Exists(exe),
            $"the gate binary is not beside the test assembly ('{exe}') — the ProjectReference to "
            + "tools/MeshWeaver.PluginTester should copy it; without it this test verifies nothing.");

        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        // Event-based reads, not ReadToEnd(): a child that filled a redirected pipe could not
        // exit, which would look like the very hang under test.
        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(ExitBudget))
        {
            var text = Text(output);
            process.Kill(entireProcessTree: true);
            Assert.Fail(
                $"'{Path.GetFileName(exe)} {string.Join(' ', arguments)}' had not exited after "
                + $"{ExitBudget.TotalSeconds:N0}s — this is the #1741 spin: a failure that does not "
                + "become an exit code leaves the process alive (and, as a container's PID 1, "
                + $"unkillable by its own abort) burning a CPU core. Output so far:\n{text}");
        }
        // The bounded wait above does not flush the async readers; this one does, and cannot block
        // indefinitely because the process has already exited.
        process.WaitForExit();
        return new RunResult(process.ExitCode, Text(output));
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null)
            return;
        lock (builder)
            builder.AppendLine(line);
    }

    private static string Text(StringBuilder builder)
    {
        lock (builder)
            return builder.ToString();
    }
}
