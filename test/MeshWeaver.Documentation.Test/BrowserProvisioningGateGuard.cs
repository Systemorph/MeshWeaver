using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the ONE invariant every workflow that provisions the headless browser must
/// hold: <b>apt is not the gate — the browser working is.</b>
///
/// <para><c>playwright install --with-deps</c> answers "did apt complete?", which is a PROXY for the
/// question CI actually asks: "can this runner drive Chromium?". On <c>ubuntu-24.04</c> every shared
/// library Chromium links against is already in the image, so what the flag adds is essentially nine
/// FONT packages — and the real answer is measured a few lines later by launching the browser
/// (dotnet-test.yml's smoke print; clients.yml's <c>npm run e2e</c>). Failing on the proxy while the
/// evidence sits further down means an Ubuntu mirror having a bad morning reads as "the clients
/// broke".</para>
///
/// <para>🚨 Why this is a guard and not a comment: the invariant has now drifted between the two
/// copies THREE times, each time fixed in one file and not the other. #1855 extracted
/// <c>wait-for-apt.sh</c> into one shared script precisely to stop this; #1858 fixed the <c>flock</c>
/// no-op in dotnet-test.yml while the identical one stayed in clients.yml; #1878 then moved the gate
/// off apt in dotnet-test.yml only. On 2026-08-19 the consequence was that a mirror stall killed
/// <c>rn-web-e2e</c> while every other Clients job passed — and because <c>clients-gate</c> feeds
/// <c>Consolidate test results</c>, the repo's ONE required check, it blocked every merge in the
/// repository. A prose note saying "keep these in step" is what failed three times; this is the
/// version that fails the build instead.</para>
///
/// <para>The workflows are DISCOVERED, never listed, so a third copy inherits the invariant on the
/// day it is written rather than on the day it breaks.</para>
/// </summary>
public class BrowserProvisioningGateGuard
{
    /// <summary>
    /// 🚨 The download must be BANKED before any apt runs. Under <c>install --with-deps</c> the
    /// apt-get runs FIRST and the download second, so an apt stall costs the browser as well as the
    /// fonts — which is why "just make the failure non-fatal" is not the fix on its own: there would
    /// be no browser left to fall back on. A separate <c>install</c> without the flag is the phase
    /// that cannot be taken away by a package mirror.
    /// </summary>
    [Fact]
    public void EveryBrowserProvisioner_BanksTheDownloadBeforeApt()
    {
        // Every workflow here has at least one install invocation — that is what
        // ProvisioningWorkflows() selects on — so the only question left is whether ALL of them
        // carry the flag, i.e. whether the download was ever banked on its own.
        var offenders = ProvisioningWorkflows()
            .Where(w => w.lines.Where(IsInstallInvocation).All(UsesWithDeps))
            .Select(w => w.file)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These workflows fuse the browser download to the apt phase: every `playwright install` "
            + "they run carries `--with-deps`. Under that flag apt runs FIRST, so a stalled Ubuntu "
            + "mirror leaves the job with no browser at all. Download it in its own invocation "
            + "first (no `--with-deps`), then run the flagged one purely for its apt half. "
            + "Offending workflow(s): " + string.Join(", ", offenders));
    }

    /// <summary>
    /// 🚨 And the apt half must not be able to fail the step. Asserted in BOTH directions on
    /// purpose — the same shape <see cref="PlatformBakeLaneGuard"/> uses — because a one-sided
    /// check is satisfied by deleting the handling altogether: forbidding the <c>::error::</c>
    /// alone would pass a workflow that reports nothing, and requiring the <c>::warning::</c> alone
    /// would pass one that emits both and still exits non-zero.
    /// </summary>
    [Fact]
    public void EveryBrowserProvisioner_TreatsADependencyFailureAsAWarning()
    {
        // 🚨 No `Assert.NotEmpty` here any more. Since the test shards stopped running apt (the font
        // set is staged once and restored from a cache, see TheTestShardsNeverReachAPackageMirror),
        // a repo can legitimately have NO `--with-deps` provisioner, and that is the stronger state.
        // Non-vacuity lives in ProvisioningWorkflows(), which still requires at least one
        // browser provisioner to exist.
        var provisioners = ProvisioningWorkflows()
            .Where(w => w.lines.Any(UsesWithDeps))
            .ToList();

        var fatal = provisioners
            .Where(w => w.lines.Any(l => Mentions(l, "::error::") && Mentions(l, "with-deps")))
            .Select(w => w.file)
            .ToList();

        Assert.True(fatal.Count == 0,
            "These workflows report a failed `--with-deps` install as an ::error::, which fails the "
            + "step on the proxy rather than on the capability. The browser launching later is the "
            + "direct measurement; report the apt failure as a ::warning:: and let that decide. "
            + "Offending workflow(s): " + string.Join(", ", fatal));

        var silent = provisioners
            .Where(w => !w.lines.Any(l => Mentions(l, "::warning::") && Mentions(l, "with-deps")))
            .Select(w => w.file)
            .ToList();

        Assert.True(silent.Count == 0,
            "These workflows run `--with-deps` but say nothing when it fails. A skipped dependency "
            + "install is the most likely explanation for a browser that cannot start, so the "
            + "warning is the context the later failure needs — without it the run reports a "
            + "rendering fault and names no cause. Emit a ::warning:: mentioning `--with-deps`. "
            + "Offending workflow(s): " + string.Join(", ", silent));
    }

    /// <summary>
    /// 🚨 The test shards provision the browser with NO apt at all. `--with-deps` added only font
    /// packages there, and a stalled Ubuntu mirror then spent the step's whole 480 s budget on
    /// them inside a shard's 20-minute cap. On 2026-10-23 that cut four shards whose tests were
    /// still running (PR runs 40011143666 and 40011313475, main run 40027189653). The fonts are now
    /// staged once by the `browser-fonts` job and restored from the `chromium-fonts` cache, so a
    /// shard never reaches a mirror. This keeps it that way: an executable `--with-deps`,
    /// `install-deps` or `apt-get` line in the test job puts the mirror back on the shard's
    /// critical path.
    /// </summary>
    [Fact]
    public void TheTestShardsNeverReachAPackageMirror()
    {
        var dir = Path.Combine(FindRepoRoot(), ".github", "workflows");
        var lines = File.ReadAllLines(Path.Combine(dir, "dotnet-test.yml"));
        var start = Array.FindIndex(lines, l => l == "  test:");
        Assert.True(start >= 0, "could not find the `test:` job in dotnet-test.yml — the guard measured nothing");
        var end = Array.FindIndex(lines, start + 1, l => l.Length > 2 && l.StartsWith("  ", StringComparison.Ordinal)
            && !l.StartsWith("   ", StringComparison.Ordinal) && !l.TrimStart().StartsWith('#'));
        var job = lines[start..(end < 0 ? lines.Length : end)]
            .Where(l => !l.TrimStart().StartsWith('#'))
            .ToArray();

        Assert.Contains(job, l => Mentions(l, "stage-chromium-fonts.sh install"));   // the replacement is there

        var apt = job.Where(l => UsesWithDeps(l) || Mentions(l, "install-deps") || Mentions(l, "apt-get"))
            .ToList();
        Assert.True(apt.Count == 0,
            "The test job reaches a package mirror again. Every shard then waits on an Ubuntu mirror "
            + "inside its 20-minute cap, which is what cut four shards on 2026-10-23. Stage what you "
            + "need in the browser-fonts job (.github/scripts/stage-chromium-fonts.sh) and restore it "
            + "from the cache. Offending line(s): " + string.Join(" | ", apt.Select(l => l.Trim())));
    }

    /// <summary>A <c>playwright install</c> command line — the real invocation, not prose about it.</summary>
    private static bool IsInstallInvocation(string line) =>
        Mentions(line, "playwright") && Mentions(line, "install") && !Mentions(line, "wait-for-apt");

    private static bool UsesWithDeps(string line) => Mentions(line, "--with-deps");

    private static bool Mentions(string line, string token) =>
        line.Contains(token, StringComparison.Ordinal);

    /// <summary>
    /// Every workflow that provisions the browser, as its EXECUTABLE lines: comment lines (first
    /// non-blank character <c>#</c>) dropped first, for the same reason PlatformBakeLaneGuard drops
    /// them — this file's own explanatory prose names `--with-deps` and `::error::` repeatedly, and
    /// a guard a comment can satisfy (or trip) is not a guard.
    /// </summary>
    private static List<(string file, string[] lines)> ProvisioningWorkflows()
    {
        var dir = Path.Combine(RepoPaths.Root, ".github", "workflows");
        var found = Directory
            .EnumerateFiles(dir, "*.yml", SearchOption.TopDirectoryOnly)
            .Select(f => (
                file: Path.GetFileName(f),
                lines: File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith('#')).ToArray()))
            .Where(w => w.lines.Any(IsInstallInvocation))
            .ToList();

        Assert.NotEmpty(found);   // no provisioner found ⇒ the discovery broke, not the repo
        return found;
    }
}
