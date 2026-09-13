#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the module-pack lane's probe of the runner's PLATFORM VOLUME — the
/// Azure Files share every ARC runner pod mounts read-only at <c>/opt/platform</c>
/// (Memex#316, layer 2), from which <c>pack</c> takes the pinned portal's <c>/app</c> instead of
/// downloading the run artifact.
///
/// <para>🚨 THE DEFECT THIS HOLDS SHUT (measured on MeshWeaver.Plugins run 34711540604, 2026-09-12):
/// the probe was written from a DESCRIPTION of the volume, not from the script that writes it.
/// It tested <c>/opt/platform/$DIGEST</c> — the PORTAL digest, in its <c>sha256:</c> colon form,
/// with the assemblies expected at that directory's top level. The refresh
/// (Systemorph/Memex <c>deployments/aks/ci-runners/ci-platform-refresh.py</c>; the README's
/// "Platform kept between jobs") lays a set out as <c>/opt/platform/sha256-&lt;hex&gt;/</c> — keyed
/// by the TESTER digest, with the colon turned into a dash because Azure Files forbids <c>:</c>
/// in a name — holding the portal's <c>/app</c> under <c>platform-refs/</c>, the pairing in
/// <c>platform.json</c> (<c>portal-image-digest</c>) and the tester digest, colon form, in
/// <c>.complete</c>. Three mismatches, each sufficient on its own: the mount could never hit,
/// every aks-silos leg quietly took the artifact, and the volume did nothing for this lane while
/// its log read as a normal fallback. Nothing in a green run distinguished "the volume is not
/// there" from "the volume is there and unreadable to us".</para>
///
/// <para>Why a guard that RUNS the probe: a text assertion alone would pass a rewrite that spells
/// <c>sha256-</c> and still looks in the wrong place. So the probe's own <c>run:</c> body is
/// executed under bash against a synthetic volume laid out exactly as the refresh writes it, and
/// must report <c>mounted=true</c> with the set's <c>platform-refs/</c> as the directory — and
/// against the volume laid out as the OLD probe imagined it, must report <c>mounted=false</c>.
/// Swap today's probe for the pre-fix one and both halves go red. The gate lane's reader of the
/// same volume, <c>resolve-gate-platform.sh</c> (#4115), resolves <c>${digest/:/-}</c> the same
/// way; this is the module-pack lane's equivalent, minus that script's refusals (here a miss falls
/// through to the run artifact by design).</para>
/// </summary>
public class PlatformMountLayoutGuard
{
    private const string Lane = ".github/workflows/node-repo-module-pack.yml";

    // The digests a set is keyed and paired by — deliberately distinct, so a probe that confused
    // the two (the defect) cannot pass by accident.
    private const string TesterDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string PortalDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void TheProbe_KeysTheSetByTheTesterDigestWithTheColonTurnedIntoADash_NeverByThePortalDigestVerbatim()
    {
        var run = ProbeBody();

        // The directory: `${TESTER_DIGEST/:/-}` — the first `:` becomes `-`, exactly as the refresh's
        // `digest.replace(":", "-", 1)` and the gate lane's `${resolved/:/-}` spell it. The scan
        // (no tester pin) globs the same dash form.
        Assert.Contains("candidates=\"$root/${TESTER_DIGEST/:/-}\"", run, StringComparison.Ordinal);
        Assert.Contains("find \"$root\" -mindepth 1 -maxdepth 1 -type d -name 'sha256-*'", run, StringComparison.Ordinal);

        // 🚨 The colon form is NEVER a path component, and the portal digest is never the key.
        Assert.DoesNotContain("/opt/platform/$DIGEST", run, StringComparison.Ordinal);
        Assert.DoesNotContain("$root/$DIGEST", run, StringComparison.Ordinal);
        Assert.DoesNotContain("$root/$TESTER_DIGEST", run, StringComparison.Ordinal);
        Assert.DoesNotContain("/sha256:", run, StringComparison.Ordinal);

        // The marker holds the ORIGINAL colon-form tester digest (what `write_file(.complete, digest)`
        // writes), so it is compared against that form — reconstructed from the directory name when
        // no tester pin was given.
        Assert.Contains("want=\"${TESTER_DIGEST:-sha256:${name#sha256-}}\"", run, StringComparison.Ordinal);
        Assert.Contains("< \"$set/.complete\")\" != \"$want\"", run, StringComparison.Ordinal);

        // The portal's /app is the set's platform-refs/, and the set must PAIR the pinned portal.
        Assert.Contains("find \"$set/platform-refs\"", run, StringComparison.Ordinal);
        Assert.Contains("\"portal-image-digest\"", run, StringComparison.Ordinal);
        Assert.Contains("\"$set/platform.json\"", run, StringComparison.Ordinal);
        Assert.Contains("echo \"dir=$set/platform-refs\" >> \"$GITHUB_OUTPUT\"", run, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerdictStep_TakesTheDirectoryTheProbeResolved_AndDoesNotRecomputeIt()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Lane));
        var verdict = StepBody(text, "id: platform");
        Assert.Contains("MOUNT_DIR: ${{ steps.platform-mount.outputs.dir }}", verdict, StringComparison.Ordinal);
        Assert.Contains("dir=\"$MOUNT_DIR\"", verdict, StringComparison.Ordinal);
        Assert.DoesNotContain("dir=\"/opt/platform/$DIGEST\"", verdict, StringComparison.Ordinal);
        // The lane's own prose names the real layout, not the imagined one.
        Assert.Contains("/opt/platform/sha256-<tester hex>/platform-refs/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/opt/platform/<platform-image-digest>/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/opt/platform/<digest>/", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbe_Executed_HitsAVolumeLaidOutAsTheRefreshWritesIt()
    {
        using var volume = new SyntheticVolume();
        // As ci-platform-refresh.py writes it: sha256-<tester hex>/{app,platform-refs,platform.json,.complete}.
        var set = volume.Set(TesterDigest.Replace(':', '-'), ".complete", TesterDigest, PortalDigest, dllsUnder: "platform-refs");
        // A refresh's per-run scratch beside it, which must not be mistaken for a set.
        Directory.CreateDirectory(set + ".tmp.ci-platform-refresh-abc12");

        var pinned = volume.Probe(PortalDigest, TesterDigest);
        Assert.Equal("true", pinned.Outputs["mounted"]);
        Assert.Equal(Path.Combine(set, "platform-refs"), pinned.Outputs["dir"]);
        Assert.Contains("the artifact is not needed", pinned.Stdout, StringComparison.Ordinal);

        // Without a tester pin the probe scans for the set that pairs the pinned portal.
        var scanned = volume.Probe(PortalDigest, testerDigest: "");
        Assert.Equal("true", scanned.Outputs["mounted"]);
        Assert.Equal(Path.Combine(set, "platform-refs"), scanned.Outputs["dir"]);

        // The same set does not serve ANOTHER portal, tester pin or not.
        const string otherPortal = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        Assert.Equal("false", volume.Probe(otherPortal, TesterDigest).Outputs["mounted"]);
        Assert.Equal("false", volume.Probe(otherPortal, testerDigest: "").Outputs["mounted"]);
    }

    /// <summary>
    /// 🚨 The "could this fail?" half. The volume laid out as the PRE-FIX probe imagined it —
    /// <c>/opt/platform/sha256:&lt;portal hex&gt;/</c>, <c>.complete</c> holding the portal digest,
    /// the assemblies at the top level — must NOT be taken: it is not what the refresh writes, and
    /// a probe that accepts it is the one that never hit the real share. Restore the old probe and
    /// this passes it, i.e. goes red.
    /// </summary>
    [Fact]
    public void TheProbe_Executed_DoesNotHitTheLayoutTheOldProbeImagined()
    {
        using var volume = new SyntheticVolume();
        volume.Set(PortalDigest, ".complete", PortalDigest, PortalDigest, dllsUnder: "");

        Assert.Equal("false", volume.Probe(PortalDigest, TesterDigest).Outputs["mounted"]);
        Assert.Equal("false", volume.Probe(PortalDigest, testerDigest: "").Outputs["mounted"]);
    }

    [Fact]
    public void TheProbe_Executed_FallsThroughOnAHalfInstall_AndOnAnAbsentMount()
    {
        using var volume = new SyntheticVolume();
        var set = volume.Set(TesterDigest.Replace(':', '-'), ".complete", TesterDigest, PortalDigest, dllsUnder: "platform-refs");
        File.Delete(Path.Combine(set, ".complete"));
        var half = volume.Probe(PortalDigest, TesterDigest);
        Assert.Equal("false", half.Outputs["mounted"]);
        Assert.Contains("a refresh in flight", half.Stdout, StringComparison.Ordinal);

        var absent = volume.Probe(PortalDigest, TesterDigest, root: Path.Combine(volume.Root, "not-mounted"));
        Assert.Equal("false", absent.Outputs["mounted"]);
        Assert.Contains("is not present on", absent.Stdout, StringComparison.Ordinal);
    }

    // ── the probe, lifted from the lane and run as shipped ─────────────────────────────────────

    private static string ProbeBody()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Lane));
        var run = RunBlock(StepBody(text, "id: platform-mount"));
        Assert.True(run.Length > 0, $"{Lane}: the `platform-mount` step must have a `run:` block");
        return run;
    }

    /// <summary>The step (in the pack job) whose body carries the given `id:` line, up to the next step.</summary>
    private static string StepBody(string lane, string idLine)
    {
        var lines = lane.Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == idLine);
        Assert.True(start >= 0, $"{Lane} must have a step with `{idLine}`");
        while (start > 0 && !lines[start].StartsWith("      - name:", StringComparison.Ordinal)) start--;
        var end = start + 1;
        // Up to the next step — or the next JOB, should the step be the job's last.
        while (end < lines.Length && !lines[end].StartsWith("      - name:", StringComparison.Ordinal)
               && !(lines[end].Length > 2 && lines[end][0] == ' ' && lines[end][1] == ' ' && char.IsLetter(lines[end][2]))) end++;
        return string.Join('\n', lines[start..end]);
    }

    /// <summary>The `run: |` scalar of a step, dedented to the script as bash receives it.</summary>
    private static string RunBlock(string step)
    {
        var lines = step.Split('\n');
        var run = Array.FindIndex(lines, l => l.TrimEnd() == "        run: |");
        Assert.True(run >= 0, "the step must have a `run: |` block");
        var body = lines[(run + 1)..]
            .TakeWhile(l => l.Length == 0 || l.StartsWith("          ", StringComparison.Ordinal))
            .Select(l => l.Length >= 10 ? l[10..] : l);
        return string.Join('\n', body);
    }

    private sealed class SyntheticVolume : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mw-platform-volume-" + Guid.NewGuid().ToString("N"));
        private readonly string script;

        public SyntheticVolume()
        {
            Directory.CreateDirectory(Root);
            var body = ProbeBody();
            // The lane hardcodes the mount point; the test redirects it and asserts the redirect
            // actually landed, so a probe that stopped saying `root=/opt/platform` cannot pass by
            // probing the runner's real filesystem.
            const string mount = "root=/opt/platform\n";
            Assert.Contains(mount, body, StringComparison.Ordinal);
            script = Path.Combine(Root, "probe.sh");
            File.WriteAllText(script, body.Replace(mount, "root=\"${PROBE_ROOT:?}\"\n", StringComparison.Ordinal));
        }

        /// <summary>Lays down one set directory named <paramref name="dirName"/> under the root.</summary>
        public string Set(string dirName, string marker, string markerValue, string portalDigest, string dllsUnder)
        {
            var dir = Path.Combine(Root, dirName);
            Directory.CreateDirectory(Path.Combine(dir, "app"));
            var dlls = dllsUnder.Length == 0 ? dir : Path.Combine(dir, dllsUnder);
            Directory.CreateDirectory(dlls);
            for (var i = 0; i < 60; i++) File.WriteAllText(Path.Combine(dlls, $"Assembly{i}.dll"), "");
            File.WriteAllText(Path.Combine(dir, "platform.json"),
                $"{{\n  \"set\": \"3.0.0-ci.1\",\n  \"image-digest\": \"{markerValue}\",\n  \"portal-image-digest\": \"{portalDigest}\"\n}}\n");
            File.WriteAllText(Path.Combine(dir, marker), markerValue + "\n");
            File.WriteAllText(Path.Combine(Root, "current"), markerValue + "\n");
            return dir;
        }

        public ProbeResult Probe(string portalDigest, string testerDigest, string? root = null)
        {
            var id = Guid.NewGuid().ToString("N");
            var outPath = Path.Combine(Root, $"stdout-{id}.txt");
            var errPath = Path.Combine(Root, $"stderr-{id}.txt");
            var ghOutput = Path.Combine(Root, $"github-output-{id}.txt");
            File.WriteAllText(ghOutput, "");

            // Redirected to FILES, never piped: a pipe read before WaitForExit blocks in the read
            // and the bounded wait below could never fire (MemexLocalDiscoversNodeRepoSiblingsGuard).
            var info = new ProcessStartInfo("/bin/bash", ["-c", $"bash '{script}' > '{outPath}' 2> '{errPath}'"]);
            info.Environment["PROBE_ROOT"] = root ?? Root;
            info.Environment["DIGEST"] = portalDigest;
            info.Environment["TESTER_DIGEST"] = testerDigest;
            info.Environment["RUNNER_NAME"] = "synthetic-runner";
            info.Environment["GITHUB_OUTPUT"] = ghOutput;

            using var process = Process.Start(info)!;
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("the platform-mount probe did not finish within 60s against a synthetic volume — it is stuck.");
            }
            var stdout = File.ReadAllText(outPath);
            var stderr = File.ReadAllText(errPath);
            Assert.True(process.ExitCode == 0,
                $"the platform-mount probe must never fail — a miss FALLS THROUGH to the run artifact (exit {process.ExitCode}).\nstdout:\n{stdout}\nstderr:\n{stderr}");

            var outputs = File.ReadAllLines(ghOutput)
                .Where(l => l.Contains('='))
                .Select(l => l.Split('=', 2))
                .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);
            Assert.True(outputs.ContainsKey("mounted"),
                $"the probe must always write `mounted=` to $GITHUB_OUTPUT — the fetch step's `if:` reads it.\nstdout:\n{stdout}");
            return new ProbeResult(stdout, outputs);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private sealed record ProbeResult(string Stdout, IReadOnlyDictionary<string, string> Outputs);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (MeshWeaver.slnx) above the test bin");
        return dir!.FullName;
    }
}
