using System.Diagnostics;
using System.Text;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The CI gate and the runtime must answer the SAME question the same way</b>
/// (Systemorph/MeshWeaver#3554).
///
/// <para><c>ModulePlatformFloor.DeclineReason</c> decides at RUNTIME whether a module may land on
/// the platform it finds itself on. <c>.github/scripts/check-module-platform-floor.py</c> decides at
/// BUILD time whether the floor a package declares is satisfiable by the platform the bundle is being
/// compiled against — the check whose absence let 42 packages declare rc-line floors that no
/// <c>3.0.0-ci.&lt;n&gt;</c> image can ever meet (SemVer §11.4 ranks pre-release identifiers as text,
/// so <c>"ci" &lt; "rc"</c>), holding every self-update candidate on both AKS portals with a message
/// naming two versions that look like they are in the right order.</para>
///
/// <para><b>Why a parity test and not two careful implementations.</b> Two call sites computing the
/// same fold differently either never converge or never fire, and both are silent — the gate would
/// pass a floor the runtime then refuses, or red a floor that is actually fine, and nothing would
/// say which. So the ordering is written once in
/// <c>src/MeshWeaver.Plugin.Packaging/NuGetVersionComparer.cs</c>, mirrored in the script, and pinned
/// here: for every case, the script's EXIT CODE must equal "the runtime found no reason to decline".
/// The table deliberately includes all four floor shapes measured on the live portals on 2026-09-07
/// (<c>rc8</c>, <c>rc9</c>, the never-published <c>rc14</c>, and a clean <c>3.0.0</c>), the fix
/// MeshWeaver.Plugins#1447 moved them to, and the numeric-vs-text trap
/// (<c>ci.900</c> vs <c>ci.3758</c>) the comparer exists for.</para>
///
/// <para>🚨 The fixture asserts its own inputs: the script must exist and <c>python3</c> must run it.
/// A parity test that silently skips when its subject is missing is the skip-trapdoor shape
/// AGENTS.md bans — it would pass having compared nothing, exactly while the gate it pins was
/// unreachable.</para>
/// </summary>
public class ModulePlatformFloorScriptParityTest
{
    private static string ScriptPath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            var script = Path.Combine(dir!.FullName, ".github", "scripts", "check-module-platform-floor.py");
            Assert.True(File.Exists(script),
                $"{script} is missing — this test would compare the runtime against nothing while "
                + "the module-pack lane's floor gate is unreachable. Follow the script if it moved.");
            return script;
        }
    }

    /// <summary>The script's verdict: true = the floor is satisfied (exit 0).</summary>
    private static bool ScriptSaysSatisfied(string? floor, string? platform)
    {
        var psi = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ScriptPath);
        psi.ArgumentList.Add("--package");
        psi.ArgumentList.Add("ParityFixture");
        psi.ArgumentList.Add("--floor");
        psi.ArgumentList.Add(floor ?? string.Empty);
        psi.ArgumentList.Add("--platform-version");
        psi.ArgumentList.Add(platform ?? string.Empty);
        psi.ArgumentList.Add("--quiet");

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        // 🚨 DRAIN BOTH PIPES CONCURRENTLY, THROUGH THE EVENT-BASED READER (#4734) — never
        // `StandardOutput.ReadToEnd()` then `StandardError.ReadToEnd()`. There is a deadlock on
        // EITHER side of the bounded wait, and only a concurrent drain misses both:
        //   • Reading BEFORE the wait — what this did — cannot honour the bound it advertises.
        //     `ReadToEnd()` returns at EOF, and the child produces EOF by EXITING. So on the one
        //     failure the 30 s exists to catch, a wedged check-module-platform-floor.py, the read
        //     blocks for ever and `WaitForExit` is never reached: the cap was reachable only when
        //     it was not needed, and the assertion beneath it could never report the hang it
        //     names. A cap that cannot fire is not a cap.
        //   • Reading AFTER the wait deadlocks the other way: a redirected stream nobody reads
        //     fills its OS buffer and blocks the child in `write`, so the wait times out on a
        //     process that had already finished its work.
        // So reordering is not the fix — draining both pipes while the wait runs is. The same
        // analysis and the same remedy sit in MsBuildPropertyProbe and StartupFailureProcessTest;
        // MemexLocalDiscoversNodeRepoSiblingsGuard takes the other sanctioned route, redirecting
        // to FILES so there is no pipe at all. Event-based reading needs no TaskCompletionSource
        // and no async bridge, so it stays inside the house rules for `test/`.
        var outBuffer = new StringBuilder();
        var errBuffer = new StringBuilder();
        process!.OutputDataReceived += (_, e) => { if (e.Data is not null) outBuffer.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) errBuffer.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Close stdin immediately. The script reads none, so a regression that started waiting on
        // it (an `input()`, an argparse prompt) would otherwise park until the bound; with the
        // pipe closed it gets EOF at once and fails as a named exit code instead of as a timeout.
        process.StandardInput.Close();

        if (!process.WaitForExit(30_000))
        {
            // Kill the TREE: python3 is the launcher, and a guard that leaks a spinning child on
            // every timeout degrades the machine it is measuring on. The race where the process
            // exits between the wait returning false and this call is the NON-event — catching it
            // keeps the named failure below, rather than replacing it with an opaque
            // InvalidOperationException. Nothing is suppressed: Assert.Fail runs either way.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            Assert.Fail(
                "python3 did not answer within 30s — the gate's own logic is a pure comparison, so a "
                + "hang here is the environment, not the rule. Do not raise the bound.\n"
                + $"stdout so far: {outBuffer} stderr so far: {errBuffer}");
        }
        // 🚨 The TIMED overload waits for the PROCESS only; the parameterless one is what joins the
        // async readers started above. Reading the buffers straight after `WaitForExit(ms)` can
        // therefore catch a TRUNCATED stream, which would make this test fail on a half-read
        // message instead of on its subject. The process has already exited here, so this returns
        // as soon as the readers drain.
        process.WaitForExit();

        var stdout = outBuffer.ToString();
        var stderr = errBuffer.ToString();
        // A python that could not START (argparse error, syntax error) exits 2 with text on stderr.
        // That must never read as "the floor is not satisfied": it is a broken fixture, named.
        Assert.True(process.ExitCode is 0 or 1,
            $"the script exited {process.ExitCode} rather than 0/1 — it did not run. "
            + $"stdout: {stdout} stderr: {stderr}");
        return process.ExitCode == 0;
    }

    [Theory]
    // The four shapes measured on memex + memex-cloud, 2026-09-07, while both ran 3.0.0-ci.7989.
    [InlineData("3.0.0-rc8", "3.0.0-ci.7989")]
    [InlineData("3.0.0-rc9", "3.0.0-ci.7989")]
    [InlineData("3.0.0-rc14", "3.0.0-ci.7989")]     // names a platform that never existed
    [InlineData("3.0.0", "3.0.0-ci.7989")]          // a release outranks every pre-release
    [InlineData("3.0.0-rc4", "3.0.0-ci.1")]
    [InlineData("3.0.0-rc4", "3.0.0-ci.999999999")] // no build number rescues an rc floor
    // The floor MeshWeaver.Plugins#1447 moved all 42 packages to, against every platform line it
    // has to survive.
    [InlineData("3.0.0-ci.7845", "3.0.0-ci.7845")]
    [InlineData("3.0.0-ci.7845", "3.0.0-ci.7989")]
    [InlineData("3.0.0-ci.7845", "3.0.0-rc9.ci.7534")]
    [InlineData("3.0.0-ci.7845", "3.0.0")]
    [InlineData("3.0.0-ci.7845", "3.1.0-ci.1")]
    // The trap NuGetVersionComparer exists for: string order puts ci.3758 below ci.900.
    [InlineData("3.0.0-ci.900", "3.0.0-ci.3758")]
    [InlineData("3.0.0-ci.3758", "3.0.0-ci.900")]
    // 🚨 Int32 OVERFLOW, on both sides and in both positions (Copilot review). `int.TryParse`
    // REFUSES past 2147483647, so the runtime reads such a segment as zero in the core and as
    // ALPHANUMERIC in the pre-release — the one place an unbounded Python `int()` was free to
    // disagree. 2147483647 is the last value both accept; 2147483648 is the first neither does.
    [InlineData("3.0.0-ci.2147483647", "3.0.0-ci.2147483647")]
    [InlineData("3.0.0-ci.2147483647", "3.0.0-ci.2147483648")]
    [InlineData("3.0.0-ci.2147483648", "3.0.0-ci.2147483647")]
    [InlineData("3.0.0-ci.2147483648", "3.0.0-ci.9999999999999999999999")]
    [InlineData("3.0.0-ci.7845", "3.0.0-ci.99999999999999999999")]
    [InlineData("99999999999999999999.0.0", "3.0.0")]
    [InlineData("3.0.0", "99999999999999999999.0.0")]
    // Non-ASCII digits: str.isdigit() accepts them, NumberStyles.None does not.
    [InlineData("3.0.0-ci.٣", "3.0.0-ci.3")]
    [InlineData("3.0.0-ci.3", "3.0.0-ci.٣")]
    // Boundaries: no declared floor is no constraint; an unknown platform is refused, not waved.
    [InlineData("", "3.0.0-ci.7989")]
    [InlineData(null, "3.0.0-ci.7989")]
    [InlineData("3.0.0-ci.7845", "")]
    [InlineData("3.0.0-ci.7845", null)]
    public void TheGateAgreesWithTheRuntimeGate(string? floor, string? platform)
    {
        var runtimeAllows = ModulePlatformFloor.DeclineReason(floor, platform) is null;
        var scriptAllows = ScriptSaysSatisfied(floor, platform);

        Assert.True(runtimeAllows == scriptAllows,
            $"floor '{floor ?? "(null)"}' against platform '{platform ?? "(null)"}': the runtime "
            + $"{(runtimeAllows ? "ALLOWS" : "DECLINES")} it and check-module-platform-floor.py "
            + $"{(scriptAllows ? "ALLOWS" : "DECLINES")} it. The two must be one rule — a gate that "
            + "disagrees with the runtime either passes a floor that will hold for ever or reds one "
            + "that is fine, and says nothing about which (#3554).");
    }

    /// <summary>
    /// The defect itself, stated as a claim rather than inferred from the table: on the ci line, an
    /// rc floor is unsatisfiable no matter how large the build number gets. This is what makes the
    /// build-time check necessary at all — the runtime cannot distinguish "not yet" from "never".
    /// </summary>
    [Fact]
    public void AnRcFloorIsUnreachableFromTheCiLine_ForEveryBuildNumber()
    {
        foreach (var build in new[] { "1", "7845", "7989", "999999999" })
        {
            var running = $"3.0.0-ci.{build}";
            Assert.NotNull(ModulePlatformFloor.DeclineReason("3.0.0-rc8", running));
            Assert.False(ScriptSaysSatisfied("3.0.0-rc8", running));
        }
    }
}
