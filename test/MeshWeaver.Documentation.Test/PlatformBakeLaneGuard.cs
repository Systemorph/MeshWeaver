using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the ONE invariant of the platform's prebuilt-NodeType delivery lane
/// (issue #1725): <b>a bake must be produced by the binaries the consumer runs.</b>
///
/// <para>The framework build identity (<c>FrameworkBuildIdentity</c>) is derived from the exact
/// assemblies a host ships — implementation MVIDs for the toolchain closure, reference-assembly
/// hashes for the rest — so it is a property of the BINARIES, not of the source they were built
/// from. Two compilations of one commit resolve DIFFERENT identities. Measured on <c>babb3bc</c>,
/// same sources and same runner path, between the Build-and-Test job's build output and the image
/// that same commit shipped: every implementation assembly's MVID differed (controls included),
/// and 4 of the 37 canonical reference assemblies differed too. The two hosts resolved
/// <c>sd0d0daa…</c> and <c>s377941f…</c>.</para>
///
/// <para>🚨 What that cost, and what this guard prevents from recurring: <c>main-cd</c> used to
/// publish the Build-and-Test run's <c>baked-assemblies-*</c> artifact — a different compilation —
/// so <c>prebuilt-bundles/&lt;runtime-identity&gt;/</c> never contained the platform's own content
/// and every pod Roslyn-compiled ~80 shipped NodeTypes on EVERY boot (64.8 s warm-up,
/// <c>compiled=80 alreadyBaked=4</c>). Nothing went red: the artifact existed, the publish
/// succeeded, and the identity nobody compared was the whole defect. A cross-build hop is
/// therefore not a thing to review case by case — it is banned outright, and the fix (bake inside
/// the shipped image, exactly as every satellite content repo does) is asserted positively so
/// "the lane quietly stopped baking" cannot pass either.</para>
///
/// <para>This is a text guard over the workflow because the invariant lives in CI, not in the
/// product: no C# unit test can observe which build produced a published bundle. See
/// Doc/Architecture/CiContentBake.</para>
///
/// <para>The values-file ↔ ConfigMap facts (<c>EveryPreWarmKeyInValues_*</c>,
/// <c>EveryConfigKeySetInAValuesFile_*</c>, <c>ModulesRequired_*</c>,
/// <c>NoValuesFile_DeclaresAModulesRequiredIndexAboveTheCeiling</c>,
/// <c>NoValuesFileArmsTheBakeGateWithoutTheSweepBehindIt</c>) read the chart and moved to
/// MeshWeaver.Deploy (test/MeshWeaver.Deploy.ChartGuards/), with the deployment chart it reads — the platform repository carries no deployment knowledge (as <c>ChartValuesBindingGuard</c>).</para>
/// </summary>
public class PlatformBakeLaneGuard
{
    private const string Workflow = ".github/workflows/main-cd.yml";
    private const string JobName = "publish-bake";

    [Fact]
    public void PlatformBake_PublishesThePortalSurface_NotTheSmallerTesterClosure()
    {
        var job = ExecutableLinesOf(ReadJobBlock());
        var measure = job.IndexOf("platform-surface /portal/app --shared-frameworks /portal/shared", StringComparison.Ordinal);
        Assert.True(measure >= 0, "The release gate needs the promoted portal's surface, not the tester's.");
        Assert.Contains("docker cp \"$cid:/usr/share/dotnet/shared\" \"$STAGE/shared\"", job, StringComparison.Ordinal);
        Assert.Contains("-v \"$STAGE:/portal:ro\" -v \"$BAKE_DIR:/bake\"", job, StringComparison.Ordinal);
        Assert.Contains("--output /bake/platform-surface.json", job, StringComparison.Ordinal);
        var verify = job.IndexOf("framework-identity /portal --expect \"$BAKED\"", StringComparison.Ordinal);
        var publish = job.IndexOf(".github/scripts/publish-bake-bundles.sh \"$BAKE_DIR\"", StringComparison.Ordinal);
        Assert.True(verify >= 0 && verify < measure && measure < publish,
            "Verify the portal's identity, measure its surface, then publish that measurement.");
    }

    /// <summary>
    /// <b>A bake-only reconcile must still know WHICH release it is making available.</b>
    ///
    /// <para><c>portal-image</c> mints the version per run and is SKIPPED on a bake-only run, so
    /// its output is empty there. The first reconcile that ever fired (2026-08-27, run
    /// 33063843072 — the cron was starved until #2491) published the bake and then died in the
    /// availability assert on an empty argument, having also written no release marker: the one
    /// thing a reconcile exists to heal was the one thing it could not do. The version is not
    /// recomputable (it carries the original run's number) but it IS recorded — promote's Phase C
    /// tags the promoted image with it — so a single <c>release</c> step resolves it (this run's
    /// output, else the promoted image's tag set) and EVERY consumer reads that step.</para>
    ///
    /// <para>Pinned structurally: inside the job, the raw <c>needs.portal-image.outputs.version</c>
    /// may appear exactly once — as the input of the step whose <c>id</c> is <c>release</c> — and
    /// the publish and the assert must read <c>steps.release.outputs.version</c>. A second raw
    /// consumer is the regression: correct on every push, empty on every reconcile.</para>
    ///
    /// <para>🚨 <b>The RECOVERY half moved to <c>gate</c> (MeshWeaver#4539), and this guard moved
    /// with its subject rather than being relaxed.</b> A second consumer of the recovered version
    /// appeared — the seal probe, which needs the release version to name this set's framework
    /// identity — so the ACR read now happens ONCE, in <c>gate</c>'s <c>bake_version</c> step, and
    /// arrives here as <c>needs.gate.outputs.release_version</c>. The invariant is unchanged and
    /// the assertion is STRICTER than before: the recovery must exist, must still stop rather than
    /// fall back, and must appear in exactly ONE job of the whole workflow — so nobody re-adds a
    /// local "safety net" read, which is the two-resolutions-of-one-fact shape the file forbids.</para>
    /// </summary>
    [Fact]
    public void PlatformBake_ResolvesTheReleaseVersionOnce_AndEveryConsumerReadsIt()
    {
        var job = ExecutableLinesOf(ReadJobBlock());
        var lines = job.Split('\n');

        var rawRefs = lines.Where(l => l.Contains("needs.portal-image.outputs.version", StringComparison.Ordinal)).ToList();
        Assert.True(rawRefs.Count == 1,
            $"'{JobName}' in {Workflow} must read needs.portal-image.outputs.version in exactly ONE place — the "
            + "`release` step that resolves the version for the whole job (this run's, else the version `gate` "
            + $"recovered off the promoted image). Found {rawRefs.Count}: on a bake-only reconcile that output is "
            + "EMPTY, so any other consumer publishes under no version and the availability assert dies on `${1:?usage}`.");

        var releaseStep = Array.FindIndex(lines, l => l.Trim() == "id: release");
        Assert.True(releaseStep >= 0, $"'{JobName}' must carry a step with `id: release` that resolves the release version.");
        var rawAt = Array.FindIndex(lines, l => l.Contains("needs.portal-image.outputs.version", StringComparison.Ordinal));
        Assert.True(Math.Abs(rawAt - releaseStep) <= 6,
            "the single needs.portal-image.outputs.version read must be the `release` step's own input "
            + $"(line {rawAt} vs `id: release` at line {releaseStep}).");

        var release = lines.Skip(releaseStep).TakeWhile((l, i) => i == 0 || !l.TrimStart().StartsWith("- name:", StringComparison.Ordinal));
        var releaseText = string.Join('\n', release);
        Assert.True(releaseText.Contains("needs.gate.outputs.release_version", StringComparison.Ordinal),
            "on a bake-only run the `release` step must take the version `gate` recovered from the PROMOTED "
            + "image's tags (needs.gate.outputs.release_version) — the record promote's Phase C wrote and "
            + "SelfUpdateHostedService rolls from — never recompute or invent one.");
        Assert.True(releaseText.Contains("exit 1", StringComparison.Ordinal),
            "a sha with no version tag was never armed as a release; the step must STOP (exit 1), not fall back.");

        var consumers = lines.Count(l => l.Contains("RELEASE_VERSION: ${{ steps.release.outputs.version }}", StringComparison.Ordinal));
        Assert.True(consumers >= 2,
            $"both the publish step and the availability assert must read steps.release.outputs.version (found {consumers}).");
    }

    /// <summary>
    /// <b>The version recovery itself — wherever it lives, it lives EXACTLY once, and it stops
    /// rather than inventing.</b> The half this pins is the one the reconcile depends on: a
    /// bake-only tick has no minted version, so the promoted image's tag set is the only record of
    /// which release it is re-asserting. Since MeshWeaver#4539 that read is <c>gate</c>'s
    /// <c>bake_version</c> step, because the seal probe needs the same answer.
    /// </summary>
    [Fact]
    public void TheReleaseVersionRecovery_LivesInExactlyOneJob_AndRefusesRatherThanInventing()
    {
        var gate = ExecutableLinesOf(ReadJobBlock("gate"));
        var lines = gate.Split('\n');

        var step = Array.FindIndex(lines, l => l.Trim() == "id: bake_version");
        Assert.True(step >= 0,
            $"'gate' in {Workflow} must carry a step with `id: bake_version` that recovers the promoted set's "
            + "release version. Without it a bake-only reconcile publishes under no version, and the seal probe "
            + "has no key for the `_releases/<version>` marker — the only thing outside the image that names this "
            + "set's framework identity (#4539).");

        var body = string.Join('\n', lines.Skip(step)
            .TakeWhile((l, i) => i == 0 || !l.TrimStart().StartsWith("- name:", StringComparison.Ordinal)));
        // 🚨 From memex-MIGRATION's tag set, not the portal's (policy
        // `one-promotion-gate`): the portal's `<version>` is the ARMING write `arm` makes only once the
        // dependent suites pass, so a promoted-but-unarmed set has none — while promote's phase A
        // tags the migration `<version>` + `<short-sha>` on one digest, the same version by
        // construction (CdImageVersionsShareOneSourceGuard).
        Assert.True(body.Contains("az acr manifest list-metadata", StringComparison.Ordinal)
                    && body.Contains("--name memex-migration", StringComparison.Ordinal),
            "`bake_version` must recover the version from the PROMOTED set's tags "
            + "(az acr manifest list-metadata … memex-migration, which phase A tags), never recompute or invent one "
            + "— and never from memex-portal-ai, whose version tag is the arming write and may not exist yet.");
        Assert.True(body.Contains("tags &&", StringComparison.Ordinal),
            "the jmespath query must short-circuit rows whose `tags` is null (`tags && contains(...)`). "
            + "contains() THROWS on null and the throw aborts the WHOLE query, so ONE of the registry's untagged "
            + "orphan manifests makes the step find no tag at all — the defect that killed nine consecutive "
            + "reconciles (2026-08-28 22:12Z → 08-29 05:26Z) while accusing promote (#2642).");
        Assert.DoesNotContain("2>/dev/null", body, StringComparison.Ordinal);
        Assert.True(body.Contains("exit 1", StringComparison.Ordinal),
            "a sha with no version tag was never armed as a release; the step must STOP (exit 1), not fall back.");

        // 🚨 EXACTLY ONE job may make this read. A second one is not a safety net: two resolutions
        // of one fact can disagree, and then the bake publishes a release marker for one version
        // while the seal probe asks about another — an availability answer about the wrong identity,
        // which is the one thing a seal check must never get wrong.
        var all = File.ReadAllLines(Path.Combine(FindRepoRoot(), Workflow));
        var jobs = Enumerable.Range(0, all.Length)
            .Where(i => IsJobKey(all[i]))
            .ToList();
        var jobsWithRecovery = jobs
            .Select((startLine, idx) =>
            {
                var endLine = idx + 1 < jobs.Count ? jobs[idx + 1] : all.Length;
                var text = ExecutableLinesOf(string.Join("\n", all[startLine..endLine]));
                return (name: all[startLine].Trim().TrimEnd(':'), hasRead: text.Contains("az acr manifest list-metadata", StringComparison.Ordinal)
                    && text.Contains("--name memex-migration", StringComparison.Ordinal));
            })
            .Where(x => x.hasRead)
            .Select(x => x.name)
            .ToList();
        Assert.True(jobsWithRecovery.Count == 1 && jobsWithRecovery[0] == "gate",
            $"the promoted set's version must be recovered in exactly ONE job — `gate` — and is recovered in: "
            + $"{string.Join(", ", jobsWithRecovery)}. Two readers of one registry fact can disagree, and then the "
            + "platform bake's release marker and the seal probe's availability question name different releases.");
    }

    [Fact]
    public void PlatformBake_RunsInsideTheShippedImage()
    {
        // 🚨 Comment lines are stripped first, and the probes below name details that appear ONLY on
        // a real command line (the entrypoint, the bake mount). Otherwise this guard could be
        // satisfied by the prose explaining the lane while the step doing it was gone — a check
        // that cannot fail is not a check.
        var job = ExecutableLinesOf(ReadJobBlock());

        Assert.True(job.Contains("docker run", StringComparison.Ordinal)
                    && job.Contains("--entrypoint /app/mw-plugin-test", StringComparison.Ordinal)
                    && job.Contains("--output /bake", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must bake the platform's content by running mw-plugin-test "
            + "INSIDE the image this run built (docker run … --entrypoint /app/mw-plugin-test "
            + "compile … --output /bake). The framework identity is a property of the shipped "
            + "binaries, so only the image the pods run can produce a bake those pods will adopt.");

        // 🚨 THE PLATFORM PIN IS STRUCTURAL, NOT A LITERAL — and the invariant is "explicit", not
        // "amd64". Architecture is part of the identity (the amd64 and arm64 variants of ONE
        // multi-arch image resolve different identities), so a container that inherits whatever
        // architecture the runner happens to be would silently key its bake to an identity no
        // consumer resolves. The lane is now one leg per architecture, each pinning `--platform` to
        // its OWN matrix value, so asserting the old `--platform linux/amd64` literal would assert
        // the opposite of the invariant: it would demand that the arm64 leg pull amd64 bytes.
        //
        // Both halves are checked, because either alone is satisfiable while the invariant is gone:
        //   (a) the LANES must name their platforms as LITERALS — `${{ matrix.docker_platform }}`
        //       with nothing behind it, or a value fed from an expression, is an inherited pin
        //       wearing an explicit pin's clothes. The lane list is composed in the `gate` job
        //       (publish-bake's matrix is `fromJSON(needs.gate.outputs.bake_arches)`, because
        //       `matrix` is not an available context in `jobs.<id>.if` and gating a leg there is a
        //       startup failure, not a false condition), so this half reads the whole WORKFLOW —
        //       and it is exactly what fails if that step is ever deleted and the matrix goes
        //       empty. Both YAML (`docker_platform: linux/amd64`) and JSON
        //       (`"docker_platform":"linux/amd64"`) spellings count, so moving the list between
        //       the two forms is not a regression;
        //   (b) EVERY container command that materialises image bytes (run / pull / create) must
        //       carry that pin — one unpinned `docker run` is enough to bake against the runner's
        //       own architecture, which is exactly the silent form of this defect.
        var workflow = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), Workflow)));
        var declaredPlatforms = Regex.Matches(workflow, @"docker_platform""?\s*:\s*""?([^""\s,}]+)")
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.True(declaredPlatforms.SequenceEqual(new[] { "linux/amd64", "linux/arm64" }),
            $"{Workflow} must name the bake lanes' platforms as literals (docker_platform "
            + "linux/amd64 and linux/arm64), one lane per architecture. Found: ["
            + string.Join(", ", declaredPlatforms) + "]. A bake is an ABI claim about BYTES, so each "
            + "leg must be taken ON the architecture it describes; publishing one architecture's "
            + "bytes under the other's identity is the adopt-what-you-did-not-resolve defect "
            + "CiContentBake.md forbids.");

        var unpinned = job.Split('\n')
            .Where(l => Regex.IsMatch(l, @"docker\s+(run|pull|create)\b"))
            .Where(l => !l.Contains("--platform ${{ matrix.docker_platform }}", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .ToArray();
        Assert.True(unpinned.Length == 0,
            $"'{JobName}' in {Workflow} runs a container without pinning it to this leg's "
            + "architecture (--platform ${{ matrix.docker_platform }}). Every docker run/pull/create "
            + "in this job materialises image bytes whose identity the bake then claims, so an "
            + "unpinned one inherits the runner's architecture and keys the bake to an identity no "
            + "pod resolves — silently, because the publish still succeeds. Offending line(s):\n  "
            + string.Join("\n  ", unpinned));

        Assert.True(job.Contains("publish-bake-bundles.sh", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must publish through .github/scripts/publish-bake-bundles.sh "
            + "— the one script whose '_complete' sentinel matches "
            + "ShippedPrebuiltBundles.CompletionSentinelFileName.");
    }

    /// <summary>
    /// 🚨 THE SPLIT (#1763): the lane must PRODUCE with the compiler and JUDGE with a mesh, in that
    /// order, as two separate runs.
    ///
    /// <para>Both halves are asserted, because either one alone is a silent regression of a
    /// different kind. A lane that goes back to the fused
    /// <c>mw-plugin-test &lt;root&gt; --bake-output</c> is a mesh compiling the content again —
    /// the finding #1763 opens with, and invisible from the outside because the artifacts are
    /// identical. A lane that keeps <c>compile</c> but drops the <c>--seed</c> gate has published
    /// bundles nothing ever rendered or tested: the release gate is gone and every check still
    /// passes.</para>
    ///
    /// <para><c>--seed</c> is also what makes the gate judge the BYTES THAT SHIP rather than a
    /// private recompile of the same sources, so its absence quietly weakens the strongest claim
    /// this lane makes.</para>
    /// </summary>
    [Fact]
    public void PlatformBake_IsCompilerDriven_AndTheGateThenConsumesIt()
    {
        var job = ExecutableLinesOf(ReadJobBlock());

        Assert.Contains("\"$IMAGE\" compile /repo/doc", job, StringComparison.Ordinal);

        Assert.False(job.Contains("--bake-output", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must not bake through the GATE verb (--bake-output): that "
            + "stands up an in-process mesh, imports the content and lets the MESH compile it — "
            + "the mesh-driven bake #1763 exists to remove. Producing an assembly is a build step; "
            + "use `compile <root> --output <dir>`, which has no MeshBuilder, no AddGraph() and no "
            + "hub anywhere in its path (pinned by MeshFreeBakePathTest, and proved equivalent to "
            + "the mesh's own resolution by BakeEquivalenceTest).");

        Assert.True(job.Contains("--seed /bake", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must GATE the bake it just produced (--seed /bake). "
            + "Splitting the bake out must not lose the gate: the platform's own shipped content "
            + "failing to render or execute its Tests areas against the image that ships it is a "
            + "release defect. With --seed the mesh CONSUMES the bake, so what it renders and "
            + "tests is the assembly that will actually ship — publishing bundles no mesh ever "
            + "exercised would be a strictly weaker release gate than the one this replaced.");

        // Order matters: the gate consumes what the bake produced, so the bake command must appear
        // first. A gate step accidentally moved above the bake would run against an empty /bake and
        // refuse (BakeSeed.Read: "holds no *.zip bundles"), but as a red job rather than as the
        // clear statement below.
        Assert.True(
            job.IndexOf("compile /repo/doc", StringComparison.Ordinal)
            < job.IndexOf("--seed /bake", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must BAKE before it GATES — the gate consumes the bake.");
    }

    /// <summary>
    /// 🚨 The release marker is what makes a release's framework identity knowable OUTSIDE its own
    /// image, and the release availability gates (#1754/#1755) HOLD when it is absent. So two
    /// things must stay true, and neither is visible from the other side:
    /// the platform bake must PASS the release version to the publisher, and the directory name the
    /// publisher writes must be the one <see cref="PublishedBundleCatalogue.ReleaseMarkerDirectoryName"/>
    /// reads. Drift in either direction freezes every environment on a release that is perfectly
    /// fine, silently.
    /// </summary>
    [Fact]
    public void PlatformBake_RecordsTheReleaseMarker_AndTheDirectoryNameMatchesTheReader()
    {
        var job = ExecutableLinesOf(ReadJobBlock());
        Assert.True(job.Contains("publish-bake-bundles.sh", StringComparison.Ordinal)
                    && job.Contains("RELEASE_VERSION", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must pass the promoted platform version to "
            + "publish-bake-bundles.sh as its release-version argument. Without it no "
            + "_releases/<version> marker is written, the release's framework identity stays "
            + "unknowable, and every environment holds on a release that is in fact fine.");

        var script = File.ReadAllText(
            Path.Combine(FindRepoRoot(), ".github", "scripts", "publish-bake-bundles.sh"));
        Assert.Contains(
            $"RELEASES_DIR=\"{PublishedBundleCatalogue.ReleaseMarkerDirectoryName}\"", script,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 The EXACT-RELEASE surface (policy <c>exact-release-surface</c>) has three hands and none
    /// sees the others: the publisher writes <c>_releases/_surface/&lt;version&gt;</c>, the gate reads
    /// <see cref="PublishedBundleCatalogue.ReleaseSurfaceDirectoryName"/>, and <c>release.yml</c>
    /// carries the document from the continuous version to the clean one. A rename on any one of
    /// them makes every release UNMEASURED — the link check silently becomes an advisory — with
    /// nothing red, so the pairing is pinned here.
    /// </summary>
    [Fact]
    public void TheExactReleaseSurface_IsWrittenCarriedAndReadUnderOneDirectoryName()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, ".github", "scripts", "publish-bake-bundles.sh"));
        Assert.Contains(
            $"RELEASE_SURFACE_DIR=\"{PublishedBundleCatalogue.ReleaseSurfaceDirectoryName}\"", script,
            StringComparison.Ordinal);
        var release = ExecutableLinesOf(File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml")));
        Assert.True(
            release.Contains($"$dir/{PublishedBundleCatalogue.ReleaseSurfaceDirectoryName}/$CI_VERSION", StringComparison.Ordinal)
            && release.Contains($"--path \"$dir/{PublishedBundleCatalogue.ReleaseSurfaceDirectoryName}\"", StringComparison.Ordinal),
            "release.yml must carry _releases/_surface/<ci-version> to the clean version beside the marker — "
            + "a Stable install gates the CLEAN version, and without its own surface every link check is unmeasured.");
    }

    /// <summary>
    /// 🚨 The platform's main build BUILDS EVERYTHING and publishes it atomically: core, every
    /// plugin, the portal host, and the bake — one run, one framework identity. Maintainer decision
    /// 2026-08-26 ("we want to run full plugin build in main memex build"; "plugins should not have
    /// a lane in this sense"). That supersedes the earlier model in which plugins were ADOPTED from
    /// a bundle their own lane published, and it removes the #1814 bake-identity class by
    /// construction: nothing can be built against a framework other than the one shipping.
    ///
    /// <para>What this guard still refuses: the samples trees (no deployment embeds them), and any
    /// checkout that is not <c>Systemorph/MeshWeaver.Plugins</c> — with ONE further, enumerated
    /// exception, and the original rule's reason is quoted here so the exception can be judged
    /// against it rather than against its wording.</para>
    ///
    /// <para><b>The original (commit 410538457, 2026-08-26, PR #2445):</b> <i>"the set of
    /// checked-out repositories in main-cd is exactly ["Systemorph/MeshWeaver.Plugins"]. Present,
    /// and the only one. A third would be a new decision rather than an extension of this one."</i>
    /// The HAZARD it closed is named in the same message: the ADOPT model — <i>"plugins arriving
    /// pre-built from a bundle their own lane published"</i> — i.e. the #1814 bake-identity class,
    /// <i>"nothing can be built against a framework other than the one shipping"</i>. A checkout is
    /// dangerous there because what it feeds BECOMES the set: bytes from a tree the identity does not
    /// name, published under an identity they were not built for.</para>
    ///
    /// <para><b>The exception (2026-09-12, maintainer: the fleet rebuilds once a day, so every core
    /// build must still MEASURE compatibility):</b> <c>satellite-compat</c> points the reusable
    /// compile gate at each satellite's content. It is OUTSIDE the hazard on every axis the original
    /// names: the checkout is read-only content at <c>content-ref: main</c>, resolved to a sha that
    /// the leg records in its verdict artifact; it is compiled into a throwaway <c>check.csproj</c>
    /// against the set this run ALREADY promoted; nothing from it enters an image, a bundle or a
    /// seal; no version is derived from it; it runs after <c>promote</c> and nothing waits for it;
    /// and nothing downstream consumes the verdict. It cannot put bytes under the wrong identity
    /// because it publishes no bytes. What it CAN do is make a core CD run red — and, since the
    /// baseline control (the same content compiled against the set the fleet is on, recorded by
    /// <c>gate</c> pre-promote), only when a type compiled there and does not against this set:
    /// core's side by measurement, which is the decision, not a side effect.</para>
    ///
    /// <para>So the register is asserted POSITIVELY below rather than the rule relaxed: exactly the
    /// five satellites, only inside that job, and OUTSIDE it the only repository is still Plugins.
    /// A sixth repository, a satellite named by another job, or a matrix that lost one is a new
    /// decision and fails here by name.</para>
    /// </summary>
    [Fact]
    public void PlatformBake_CompilesOnlyWhatTheImageEmbeds()
    {
        var job = ExecutableLinesOf(ReadJobBlock());

        Assert.True(job.Contains("stage-doc-gate.sh", StringComparison.Ordinal),
            $"'{JobName}' must bake the Doc tree — it is the content every portal embeds "
            + "(Memex.Portal.Shared references MeshWeaver.Documentation), so it is the one tree "
            + "no other lane can publish.");

        Assert.False(job.Contains("stage-samples-gate.sh", StringComparison.Ordinal),
            $"'{JobName}' must NOT bake samples/Graph/Data. No deployment embeds those trees, and "
            + "memex receives them over the GitHub link into the `MeshWeaver` partition — where the "
            + "node paths read `MeshWeaver/samples/Graph/Data/ACME/…` while the bundles are keyed "
            + "`ACME/…`, so the seeder (which matches by node PATH) can never adopt them. Measured: "
            + "7 packages / 24 assemblies compiled per CD run for bundles nothing can use. They "
            + "still compile-GATE on every PR in dotnet-test.yml's doc-gate — that proves the "
            + "content, which is the part worth paying for.");

        // The main build checks out exactly ONE other repository AS A BUILD INPUT — the plugins it
        // builds and ships. Asserted POSITIVELY (present, and the only one) rather than as an
        // absence: the portal host lives there now, so a main-cd without this checkout cannot build
        // the deployment image at all (measured 2026-08-26: every push run failed at the version
        // step and nothing published until this was restored).
        //
        // The `satellite-compat` job (2026-09-12) is the enumerated exception: it names the five
        // satellites in its matrix and hands them to node-repo-compile-check.yml as
        // `content-repository: ${{ matrix.repository }}`. Both halves are asserted — the matrix
        // lists exactly the five, and OUTSIDE that job the only repository named is Plugins — so a
        // sixth satellite, a satellite named by another job, or a matrix that silently lost one all
        // fail here by name. (`${{` is the matrix expression itself, seen through the same regex.)
        var lines = File.ReadAllLines(Path.Combine(FindRepoRoot(), Workflow));
        var (compatStart, compatEnd) = JobRange(lines, "satellite-compat");
        var compat = ExecutableLinesOf(string.Join("\n", lines[compatStart..compatEnd]));
        var satellites = Regex.Matches(compat, @"repository:\s*(\S+)")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(v => !v.StartsWith("${{", StringComparison.Ordinal))
            .Distinct()
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[]
            {
                "Systemorph/MeshWeaver.Crm",
                "Systemorph/MeshWeaver.Education",
                "Systemorph/MeshWeaver.Manufacturing",
                "Systemorph/MeshWeaver.Reinsurance",
                "Systemorph/MeshWeaver.SocialMedia",
            },
            satellites);
        Assert.Contains("content-repository: ${{ matrix.repository }}", compat, StringComparison.Ordinal);

        var outside = ExecutableLinesOf(string.Join("\n", lines[..compatStart].Concat(lines[compatEnd..])));
        var repos = Regex.Matches(outside, @"repository:\s*(\S+)")
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct()
            .ToList();
        Assert.Equal(new[] { "Systemorph/MeshWeaver.Plugins" }, repos);
    }

    /// <summary>The block's lines that can actually DO something: YAML and shell comment lines
    /// (first non-blank character <c>#</c>) dropped, everything else kept verbatim.</summary>
    private static string ExecutableLinesOf(string block) =>
        string.Join("\n", block.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    /// <summary>
    /// The ONE cross-run download this file tolerates, and the block it must stay inside. The
    /// original invariant (#1725, below) is about ADOPTION: a bundle from another run is a
    /// different compilation of the same source, resolves a different framework identity, and so
    /// must never be PUBLISHED as this run's. <c>satellite-compat-image</c> (2026-09-12) downloads
    /// the previous run's module bundles for the opposite purpose — as the COMPILE REFERENCE of the
    /// baseline reading (the set the fleet is on, so a satellite's "does not compile" can be
    /// attributed to core or to the satellite). Those bytes are re-uploaded under a name only the
    /// compat legs read, unpacked into <c>refs-baseline/</c>, and never enter a bake directory, a
    /// seal or a publication. The exemption is the BLOCK, asserted positively by
    /// <see cref="SatelliteCompatBaselineDownload_IsAReferenceNeverAPublication"/>; a
    /// <c>gh run download</c> anywhere else in a publishing workflow is still the #1725 defect.
    /// </summary>
    private const string BaselineDownloadJob = "satellite-compat-image";

    [Fact]
    public void PlatformBake_NeverAdoptsAnotherBuildsBundles()
    {
        var workflows = Path.Combine(FindRepoRoot(), ".github", "workflows");
        var offenders = Directory
            .EnumerateFiles(workflows, "*.yml", SearchOption.TopDirectoryOnly)
            // Comments stripped for the same reason as above, in the other direction: a workflow is
            // judged on what it RUNS, so prose explaining why the artifact hop was removed must
            // never read as the hop itself.
            .Select(f => (file: Path.GetFileName(f), text: ExecutableLinesOf(WithoutJob(File.ReadAllLines(f), BaselineDownloadJob))))
            .Where(x => x.text.Contains("publish-bake-bundles.sh", StringComparison.Ordinal))
            // The invariant is CROSS-RUN adoption (#1725): a bundle from another run is a different
            // compilation resolving a different framework identity. `gh run download` is cross-run
            // by construction and stays forbidden outright. `actions/download-artifact` can only
            // reach ANOTHER run when it names one (`run-id:` + a token) — without `run-id:` it sees
            // exclusively the CURRENT run's artifacts, which is the property this guard exists to
            // protect. That same-run form is how the bake composes this wave's MODULE bundles into
            // the compile surface (#2628, `module-artifacts` → `--module`): the module lane is
            // FLOOR-gated, not identity-gated (PluginBundleClient.AdoptModule), and the downloaded
            // bytes are mounted at /ext for the compiler — they never enter the $BAKE directory
            // that publish-bake-bundles.sh publishes.
            .Where(x => x.text.Contains("gh run download", StringComparison.Ordinal)
                        || (x.text.Contains("download-artifact", StringComparison.Ordinal)
                            && x.text.Contains("run-id:", StringComparison.Ordinal)))
            .Select(x => x.file)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A workflow that publishes prebuilt bundles must never take them from a cross-run "
            + "artifact — that is a DIFFERENT compilation of the same source and resolves a "
            + "different framework identity, so no pod can adopt what it publishes (#1725). "
            + "Bake inside the image being shipped instead. Offending workflow(s): "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// <b>The baseline download is a compile reference, never a publication.</b> Both directions:
    /// the exempted block must still CONTAIN the cross-run download (or the exemption above excuses
    /// nothing and this guard has gone blind), and it must carry none of the forms by which bytes
    /// reach a bake, a seal or the portals' storage. The lane that consumes the re-upload is held
    /// to the same: the baseline bundles unpack into <c>refs-baseline/</c> only.
    /// </summary>
    [Fact]
    public void SatelliteCompatBaselineDownload_IsAReferenceNeverAPublication()
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepoRoot(), Workflow));
        var start = Array.FindIndex(lines, l => l.Equals($"  {BaselineDownloadJob}:", StringComparison.Ordinal));
        Assert.True(start >= 0, $"no '{BaselineDownloadJob}:' job in {Workflow} — the exemption in "
            + "PlatformBake_NeverAdoptsAnotherBuildsBundles names a block that does not exist, so it excuses nothing "
            + "and this guard measures nothing. Delete the exemption with the job, in the same change.");
        var end = Array.FindIndex(lines, start + 1, IsJobKey);
        var block = ExecutableLinesOf(string.Join("\n", lines[start..(end < 0 ? lines.Length : end)]));

        Assert.Contains("gh run download", block, StringComparison.Ordinal);
        Assert.Contains("name: satellite-compat-baseline-bundles", block, StringComparison.Ordinal);
        foreach (var publishing in new[] { "publish-bake-bundles.sh", "push-bundle-publication.sh", "--bake-output", "--seed ", "az storage", "BAKE_PUBLISH_TARGETS" })
            Assert.DoesNotContain(publishing, block, StringComparison.Ordinal);

        // The consumer side: the reusable lane unpacks the baseline bundles into the BASELINE
        // reference directory and nowhere else, and the lane publishes nothing.
        var lane = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "node-repo-compile-check.yml")));
        Assert.Contains("baseline-module-artifacts", lane, StringComparison.Ordinal);
        Assert.Contains("refs-baseline/", lane, StringComparison.Ordinal);
        foreach (var publishing in new[] { "publish-bake-bundles.sh", "push-bundle-publication.sh", "--bake-output", "az storage" })
            Assert.DoesNotContain(publishing, lane, StringComparison.Ordinal);
    }

    /// <summary>The workflow's lines with ONE job's block removed — for a detector whose rule the
    /// block is exempt from by a positive assertion elsewhere.</summary>
    private static string WithoutJob(string[] lines, string jobName)
    {
        var start = Array.FindIndex(lines, l => l.Equals($"  {jobName}:", StringComparison.Ordinal));
        if (start < 0) return string.Join("\n", lines);
        var end = Array.FindIndex(lines, start + 1, IsJobKey);
        if (end < 0) end = lines.Length;
        return string.Join("\n", lines[..start].Concat(lines[end..]));
    }

    /// <summary>The <c>publish-bake</c> job's own YAML block: from its two-space-indented key to
    /// the next job key at the same indentation. The terminator is matched as a bare
    /// <c>  &lt;name&gt;:</c> line rather than "indented and ends with a colon", so a two-space
    /// comment between jobs cannot truncate the block and turn this guard into a false failure.
    /// </summary>
    private static string ReadJobBlock() => ReadJobBlock(JobName);

    private static string ReadJobBlock(string jobName)
    {
        var path = Path.Combine(FindRepoRoot(), Workflow);
        Assert.True(File.Exists(path), $"expected {Workflow} at the repo root");
        var lines = File.ReadAllLines(path);

        var start = Array.FindIndex(lines, l => l.Equals($"  {jobName}:", StringComparison.Ordinal));
        Assert.True(start >= 0, $"no '{jobName}:' job in {Workflow} — the platform's own content "
            + "bake is what publishes the shipped NodeType bundles the portals adopt; deleting it "
            + "returns every pod to compiling every shipped type at boot (#1347, #1725).");

        var end = Array.FindIndex(lines, start + 1, IsJobKey);
        if (end < 0)
            end = lines.Length;
        return string.Join("\n", lines[start..end]);
    }

    /// <summary>The [start, end) line range of one job's block — the job key line through the line
    /// before the next job key. Asserted present: a job this guard pins by name that is gone would
    /// otherwise turn its assertions into a pass over an empty string.</summary>
    private static (int Start, int End) JobRange(string[] lines, string jobName)
    {
        var start = Array.FindIndex(lines, l => l.Equals($"  {jobName}:", StringComparison.Ordinal));
        Assert.True(start >= 0, $"no '{jobName}:' job in {Workflow} — the per-build satellite compatibility "
            + "measurement (2026-09-12) is the counterweight to the fleet's once-a-day rebuild; without it a "
            + "core merge that breaks a satellite is found at 03:00 or on a portal at self-update.");
        var end = Array.FindIndex(lines, start + 1, IsJobKey);
        return (start, end < 0 ? lines.Length : end);
    }

    private static bool IsJobKey(string line) =>
        line.Length > 3
        && line.StartsWith("  ", StringComparison.Ordinal)
        && line[2] != ' '
        && line[^1] == ':'
        && line[2..^1].All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
