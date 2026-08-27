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
/// <para>🚨 EVERY docker call is PINNED to <see cref="Platform"/>, and that is not tidiness. A
/// candidate reference is a multi-arch manifest list whose amd64 and arm64 variants carry
/// genuinely different bytes (they resolve different framework build identities — see the bake
/// lane in <c>main-cd.yml</c>), while docker reports the same manifest LIST digest for both. Left
/// unpinned, docker silently picks the HOST's architecture: an operator running the gate on an
/// arm64 laptop gets a Green naming a digest that covers the amd64 bytes the fleet actually runs
/// and that the run never touched — a FALSE PASS that no output distinguishes from a real one.
/// The platform is therefore explicit here and recorded on the verdict
/// (<see cref="ComboVerification.VerifiedPlatform"/>).</para>
///
/// <para>Expected failures — docker absent, pull denied, the run exceeding its budget, a host that
/// cannot emulate the requested platform — come back as <see cref="CandidateGateRun.Error"/>,
/// never as a fault: the verifier folds them into a NotVerifiable verdict. That is the right way
/// to fail: an un-runnable platform reads as "we could not find out", never as "all clear".</para>
///
/// <para>Each docker invocation is a blocking <see cref="Process"/> leaf on the
/// <see cref="IoPoolNames.Process"/> pool, the sanctioned boundary for sync-blocking work; the
/// public surface is <see cref="IObservable{T}"/>.</para>
/// </summary>
public sealed class DockerImageGate(
    IoPoolRegistry pools, TimeSpan runBudget, TextWriter output, string platform)
{
    /// <summary>How many trailing characters of the combined docker output the run keeps for
    /// diagnostics.</summary>
    private const int LogTailLength = 8 * 1024;

    /// <summary>The platform every install in the fleet runs. AKS node pools are x86_64 (the CD
    /// bake's arm64 leg is opt-in behind <c>BAKE_ARM64</c>), so a verdict that does not say
    /// otherwise must be about THIS.</summary>
    public const string FleetPlatform = "linux/amd64";

    /// <summary>The docker platform this gate pins every call to.</summary>
    public string Platform { get; } = platform;

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

        // Ensure the image is present FOR THE REQUESTED PLATFORM (docker run would pull too, but a
        // separate pull keeps its output out of the gate log and makes an auth failure a SPEAKING
        // error).
        //
        // 🚨 The presence question is per-PLATFORM, not per-reference. `docker image inspect`
        // answers about whichever variant the local store holds, so asking "is it present?" would
        // report the arm64 copy an earlier run pulled and SKIP the pull of the amd64 bytes we are
        // about to be asked to run. Asking for the architecture instead makes the check answer the
        // question the run actually depends on.
        var present = Docker(
            ["image", "inspect", "--format", "{{.Os}}/{{.Architecture}}", imageRef], ct,
            TimeSpan.FromMinutes(1));
        var localPlatform = present.ExitCode == 0 ? present.Output.Trim() : null;
        if (!string.Equals(localPlatform, Platform, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine(localPlatform is null
                ? $"pulling {imageRef} ({Platform}) …"
                : $"pulling {imageRef} ({Platform}) — the local copy is {localPlatform} …");
            var pull = Docker(PullArgs(imageRef, Platform), ct, runBudget);
            if (pull.TimedOut)
                return new CandidateGateRun
                {
                    Platform = Platform,
                    Error = $"docker pull '{imageRef}' ({Platform}) exceeded the {runBudget} budget.",
                    LogTail = Tail(pull.Output),
                };
            // 🚨 A pull that fails is NOT recoverable by running whatever happens to be local: that
            // is precisely how a run for one architecture gets served another. Report it.
            if (pull.ExitCode != 0)
                return new CandidateGateRun
                {
                    Platform = Platform,
                    Error = $"docker pull '{imageRef}' for {Platform} failed (exit "
                            + $"{pull.ExitCode}) — the candidate's {Platform} bytes are not "
                            + "available here, so nothing about them was verified.",
                    LogTail = Tail(pull.Output),
                };
        }

        var digest = ResolveDigest(imageRef, ct);

        output.WriteLine(
            $"running mw-plugin-test inside {imageRef} ({Platform}) over '{fullRoot}' …");
        var run = Docker(RunArgs(imageRef, fullRoot, Platform), ct, runBudget);
        if (run.TimedOut)
            return new CandidateGateRun
            {
                ImageDigest = digest,
                Platform = Platform,
                Error = $"the gate run exceeded its {runBudget} budget and was killed — "
                        + "something inside the candidate did not complete.",
                LogTail = Tail(run.Output),
            };

        return new CandidateGateRun
        {
            ExitCode = run.ExitCode,
            ImageDigest = digest,
            Platform = Platform,
            Report = ReadReport(reportFile),
            LogTail = Tail(run.Output),
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The docker command line — pure, so the contract is pinned by a test
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The <c>docker pull</c> argv, pinned to <paramref name="platform"/>.</summary>
    internal static string[] PullArgs(string imageRef, string platform) =>
        ["pull", "--platform", platform, imageRef];

    /// <summary>
    /// The <c>docker run</c> argv — the same contract as the plugins repo's <c>test-repos</c> job
    /// and <c>main-cd.yml</c>'s bake: <c>--rm --init --platform … -v root:/work --entrypoint
    /// /app/mw-plugin-test IMAGE /work --report /work/…</c>.
    ///
    /// <para>🚨 ORDER IS THE CONTRACT, not a style choice. Everything before
    /// <paramref name="imageRef"/> is docker's; everything after it is the TESTER's. Move
    /// <c>--platform</c> (or any flag) past the image and docker stops seeing it — the tester
    /// receives it as an unknown argument instead, and the run is no longer pinned to anything.
    /// Pinned by <c>DockerImageGateArgsTest</c>.</para>
    /// </summary>
    internal static string[] RunArgs(string imageRef, string fullRoot, string platform) =>
    [
        "run", "--rm",
        // --init reaps the tester's children; every other invocation of this image in the repo
        // passes it, and a gate that runs the image differently from CI is not measuring CI.
        "--init",
        "--platform", platform,
        "-v", $"{fullRoot}:/work",
        "--entrypoint", "/app/mw-plugin-test",
        imageRef,
        "/work",
        "--report", $"/work/{GateRunReport.FileName}",
    ];

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
