using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;

namespace MeshWeaver.ComboVerifier;

/// <summary>
/// The production gate seam for <see cref="InstanceComboVerifier"/>: runs <c>mw-plugin-test</c>
/// over the assembled work root INSIDE the candidate image, via docker.
///
/// <para>The contract mirrors the plugins repo's <c>test-repos</c> CI job exactly — <c>docker run</c>
/// with the root mounted and <c>--entrypoint /app/mw-plugin-test</c> (a <c>container:</c> job does
/// NOT work: the tool is not on PATH, the published user is non-root, and the base image may go
/// chiseled — see <c>MeshWeaver.PluginTester.csproj</c>). The tester writes its structured report to
/// the mount (<see cref="GateRunReport.FileName"/>); the image's non-root user needs write access
/// there, so the work root is made world-writable before the run.</para>
///
/// <para>Expected failures — docker absent, pull denied, the run exceeding its budget — come back
/// as <see cref="CandidateGateRun.Error"/>, never as a fault: the verifier folds them into a
/// NotVerifiable verdict. Each docker invocation is a blocking <see cref="Process"/> leaf on the
/// <see cref="IoPoolNames.Process"/> pool, the sanctioned boundary for sync-blocking work; the
/// public surface is <see cref="IObservable{T}"/>.</para>
/// </summary>
public sealed class DockerImageGate(IoPoolRegistry pools, TimeSpan runBudget, TextWriter output)
{
    /// <summary>How many trailing characters of the combined docker output the run keeps for
    /// diagnostics.</summary>
    private const int LogTailLength = 8 * 1024;

    private IIoPool Pool => pools.Get(IoPoolNames.Process);

    /// <summary>Runs the gate: (candidate image, assembled work root) → one
    /// <see cref="CandidateGateRun"/>. Cold — subscribe to run; cancellation kills the docker
    /// client process so a pool slot is never leaked.</summary>
    public IObservable<CandidateGateRun> Run(string imageRef, string workRoot) =>
        Pool.InvokeBlocking(ct => Execute(imageRef, workRoot, ct));

    private CandidateGateRun Execute(string imageRef, string workRoot, CancellationToken ct)
    {
        var fullRoot = Path.GetFullPath(workRoot);
        var reportFile = Path.Combine(fullRoot, GateRunReport.FileName);
        // A stale report from an earlier run must never masquerade as this run's evidence.
        if (File.Exists(reportFile))
            File.Delete(reportFile);

        // The image's tester runs as a non-root user and writes its report to the mount.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fullRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        // Ensure the image is present (docker run would pull too, but a separate pull keeps its
        // output out of the gate log and makes an auth failure a SPEAKING error).
        var present = Docker(["image", "inspect", "--format", "present", imageRef], ct,
            TimeSpan.FromMinutes(1));
        if (present.ExitCode != 0)
        {
            output.WriteLine($"pulling {imageRef} …");
            var pull = Docker(["pull", imageRef], ct, runBudget);
            if (pull.TimedOut)
                return new CandidateGateRun
                {
                    Error = $"docker pull '{imageRef}' exceeded the {runBudget} budget.",
                    LogTail = Tail(pull.Output),
                };
            if (pull.ExitCode != 0)
                return new CandidateGateRun
                {
                    Error = $"docker pull '{imageRef}' failed (exit {pull.ExitCode}).",
                    LogTail = Tail(pull.Output),
                };
        }

        var digest = ResolveDigest(imageRef, ct);

        output.WriteLine($"running mw-plugin-test inside {imageRef} over '{fullRoot}' …");
        var run = Docker(
            [
                "run", "--rm",
                "-v", $"{fullRoot}:/work",
                "--entrypoint", "/app/mw-plugin-test",
                imageRef,
                "/work",
                "--report", $"/work/{GateRunReport.FileName}",
            ],
            ct, runBudget);
        if (run.TimedOut)
            return new CandidateGateRun
            {
                ImageDigest = digest,
                Error = $"the gate run exceeded its {runBudget} budget and was killed — "
                        + "something inside the candidate did not complete.",
                LogTail = Tail(run.Output),
            };

        return new CandidateGateRun
        {
            ExitCode = run.ExitCode,
            ImageDigest = digest,
            Report = ReadReport(reportFile),
            LogTail = Tail(run.Output),
        };
    }

    /// <summary>The repo digest the ref resolves to (the identity of what was verified), else the
    /// local image id (a locally built candidate has no repo digest), else null.</summary>
    private string? ResolveDigest(string imageRef, CancellationToken ct)
    {
        var repoDigest = Docker(
            ["image", "inspect", "--format", "{{index .RepoDigests 0}}", imageRef],
            ct, TimeSpan.FromMinutes(1));
        if (repoDigest.ExitCode == 0 && !string.IsNullOrWhiteSpace(repoDigest.Output))
            return repoDigest.Output.Trim();
        var id = Docker(["image", "inspect", "--format", "{{.Id}}", imageRef],
            ct, TimeSpan.FromMinutes(1));
        return id.ExitCode == 0 && !string.IsNullOrWhiteSpace(id.Output) ? id.Output.Trim() : null;
    }

    /// <summary>The structured report, when the tester wrote one; null (never a guess) when it did
    /// not or when it is unparseable — the verifier reports that as NotVerifiable.</summary>
    private GateRunReport? ReadReport(string reportFile)
    {
        if (!File.Exists(reportFile))
            return null;
        try
        {
            return JsonSerializer.Deserialize<GateRunReport>(
                File.ReadAllText(reportFile), InstanceComboAssembler.Json);
        }
        catch (JsonException ex)
        {
            output.WriteLine($"⚠ the gate report at '{reportFile}' is unparseable: {ex.Message}");
            return null;
        }
    }

    private DockerResult Docker(IReadOnlyList<string> args, CancellationToken ct, TimeSpan budget)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var combined = new StringBuilder();
        var sync = new object();

        try
        {
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => Append(e.Data);
            p.ErrorDataReceived += (_, e) => Append(e.Data);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            // Cancellation kills the docker client (which tears down the container via --rm) so a
            // pool slot is never leaked on unsubscribe.
            using var reg = ct.Register(() =>
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            });
            if (!p.WaitForExit((int)budget.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
                p.WaitForExit();
                return new DockerResult(-1, Combined(), TimedOut: true);
            }
            // Flush the async readers.
            p.WaitForExit();
            return new DockerResult(p.ExitCode, Combined(), TimedOut: false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // docker itself is not runnable here — a speaking orchestration error, not a crash.
            return new DockerResult(-1,
                $"could not start 'docker {string.Join(' ', args)}': {ex.Message}", TimedOut: false);
        }

        void Append(string? line)
        {
            if (line is null)
                return;
            lock (sync)
            {
                combined.AppendLine(line);
                output.WriteLine($"    {line}");
            }
        }

        string Combined()
        {
            lock (sync)
                return combined.ToString();
        }
    }

    private static string Tail(string text) =>
        text.Length <= LogTailLength ? text : text[^LogTailLength..];

    private sealed record DockerResult(int ExitCode, string Output, bool TimedOut);
}
