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
/// </summary>
public class PlatformBakeLaneGuard
{
    private const string Workflow = ".github/workflows/main-cd.yml";
    private const string JobName = "publish-bake";

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
    /// </summary>
    [Fact]
    public void PlatformBake_ResolvesTheReleaseVersionOnce_AndEveryConsumerReadsIt()
    {
        var job = ExecutableLinesOf(ReadJobBlock());
        var lines = job.Split('\n');

        var rawRefs = lines.Where(l => l.Contains("needs.portal-image.outputs.version", StringComparison.Ordinal)).ToList();
        Assert.True(rawRefs.Count == 1,
            $"'{JobName}' in {Workflow} must read needs.portal-image.outputs.version in exactly ONE place — the "
            + "`release` step that resolves the version for the whole job (this run's, else the promoted image's "
            + $"tag). Found {rawRefs.Count}: on a bake-only reconcile that output is EMPTY, so any other consumer "
            + "publishes under no version and the availability assert dies on `${1:?usage}`.");

        var releaseStep = Array.FindIndex(lines, l => l.Trim() == "id: release");
        Assert.True(releaseStep >= 0, $"'{JobName}' must carry a step with `id: release` that resolves the release version.");
        var rawAt = Array.FindIndex(lines, l => l.Contains("needs.portal-image.outputs.version", StringComparison.Ordinal));
        Assert.True(Math.Abs(rawAt - releaseStep) <= 6,
            "the single needs.portal-image.outputs.version read must be the `release` step's own input "
            + $"(line {rawAt} vs `id: release` at line {releaseStep}).");

        var release = lines.Skip(releaseStep).TakeWhile((l, i) => i == 0 || !l.TrimStart().StartsWith("- name:", StringComparison.Ordinal));
        var releaseText = string.Join('\n', release);
        Assert.True(releaseText.Contains("az acr manifest list-metadata", StringComparison.Ordinal)
                    && releaseText.Contains("memex-portal-ai", StringComparison.Ordinal),
            "on a bake-only run the `release` step must recover the version from the PROMOTED image's tags "
            + "(az acr manifest list-metadata … memex-portal-ai) — the record promote's Phase C wrote and "
            + "SelfUpdateHostedService rolls from — never recompute or invent one.");
        Assert.True(releaseText.Contains("exit 1", StringComparison.Ordinal),
            "a sha with no version tag was never armed as a release; the step must STOP (exit 1), not fall back.");

        var consumers = lines.Count(l => l.Contains("RELEASE_VERSION: ${{ steps.release.outputs.version }}", StringComparison.Ordinal));
        Assert.True(consumers >= 2,
            $"both the publish step and the availability assert must read steps.release.outputs.version (found {consumers}).");
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
    /// 🚨 The platform's main build BUILDS EVERYTHING and publishes it atomically: core, every
    /// plugin, the portal host, and the bake — one run, one framework identity. Maintainer decision
    /// 2026-08-26 ("we want to run full plugin build in main memex build"; "plugins should not have
    /// a lane in this sense"). That supersedes the earlier model in which plugins were ADOPTED from
    /// a bundle their own lane published, and it removes the #1814 bake-identity class by
    /// construction: nothing can be built against a framework other than the one shipping.
    ///
    /// <para>What this guard still refuses: the samples trees (no deployment embeds them), and any
    /// checkout that is not <c>Systemorph/MeshWeaver.Plugins</c>. The plugins checkout is the ONE
    /// deliberate cross-repo input — a second one would be a new decision, not an extension of
    /// this one.</para>
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

        // The main build checks out exactly ONE other repository — the plugins it builds and ships.
        // Asserted POSITIVELY (present, and the only one) rather than as an absence: the portal
        // host lives there now, so a main-cd without this checkout cannot build the deployment
        // image at all (measured 2026-08-26: every push run failed at the version step and nothing
        // published until this was restored). And a third `repository:` would be a new decision.
        var text = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), Workflow)));
        var repos = Regex.Matches(text, @"repository:\s*(\S+)")
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct()
            .ToList();
        Assert.Equal(new[] { "Systemorph/MeshWeaver.Plugins" }, repos);
    }

    /// <summary>The block's lines that can actually DO something: YAML and shell comment lines
    /// (first non-blank character <c>#</c>) dropped, everything else kept verbatim.</summary>
    private static string ExecutableLinesOf(string block) =>
        string.Join("\n", block.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    [Fact]
    public void PlatformBake_NeverAdoptsAnotherBuildsBundles()
    {
        var workflows = Path.Combine(FindRepoRoot(), ".github", "workflows");
        var offenders = Directory
            .EnumerateFiles(workflows, "*.yml", SearchOption.TopDirectoryOnly)
            // Comments stripped for the same reason as above, in the other direction: a workflow is
            // judged on what it RUNS, so prose explaining why the artifact hop was removed must
            // never read as the hop itself.
            .Select(f => (file: Path.GetFileName(f), text: ExecutableLinesOf(File.ReadAllText(f))))
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
    /// 🚨 A <c>PreWarm__*</c> key set in a values file but NOT templated in the configmap is
    /// SILENTLY DROPPED — the deployment runs as if it were never configured, with no warning from
    /// helm, kubectl, or the portal.
    ///
    /// <para>This is not hypothetical. <c>deploy/aks/values.aks.yaml</c> set
    /// <c>PreWarm__PrebuiltBundleRoot: "/data/prebuilt-bundles"</c> from the day the CI-published
    /// bake lane shipped (#1660 WS3), while <c>config.yaml</c> never rendered the key — so every
    /// chart-deployed portal ran with the consuming half of that lane inert and Roslyn-compiled the
    /// content CI had already baked for it. The producer worked, the value was right, and the one
    /// line that would have carried it across did not exist.</para>
    ///
    /// <para>The configmap enumerates keys explicitly (it does not iterate <c>.Values.config</c>),
    /// which is a deliberate choice — it keeps the deployed surface reviewable — but it means
    /// adding a key to a values file is only ever HALF the change. This guard is the other half,
    /// asserted rather than remembered.</para>
    /// </summary>
    [Fact]
    public void EveryPreWarmKeyInValues_IsTemplatedInTheConfigMap()
    {
        var root = FindRepoRoot();
        var configMap = File.ReadAllText(
            Path.Combine(root, "deploy", "helm", "templates", "memex-portal", "config.yaml"));

        // The values files the chart is actually deployed with. A key is "set" when it appears as a
        // mapping key; comments are excluded so the prose describing a key never counts as using it.
        var valuesFiles = new[]
        {
            Path.Combine(root, "deploy", "helm", "values.yaml"),
            Path.Combine(root, "deploy", "aks", "values.aks.yaml"),
            Path.Combine(root, "deploy", "homebrew", "share", "values.local.defaults.yaml"),
        };

        var missing = valuesFiles
            .Where(File.Exists)
            .SelectMany(f => File.ReadAllLines(f)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith('#') && l.StartsWith("PreWarm__", StringComparison.Ordinal))
                .Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)].Trim())
                .Select(key => (file: Path.GetFileName(f), key)))
            .Distinct()
            // 🚨 Templated means the key is BOUND, not merely mentioned. Matching "<key>:" alone
            // would be satisfied by the explanatory Helm comment blocks that precede each entry —
            // they are `{{- /* ... */}}`, which no '#'-stripping can remove, and several of them
            // open with the very key they describe. A check a comment can satisfy is not a check.
            // Requiring the value binding as well is a shape prose cannot accidentally have.
            .Where(x => !IsBoundInConfigMap(configMap, x.key))
            .ToList();

        Assert.True(missing.Count == 0,
            "These PreWarm keys are set in a values file but never rendered by "
            + "deploy/helm/templates/memex-portal/config.yaml, so the deployment silently ignores "
            + "them: "
            + string.Join(", ", missing.Select(x => $"{x.key} (set in {x.file})"))
            + ". Add each one to the configmap template — the values file alone does nothing.");
    }

    /// <summary>
    /// Keys a values file sets that reach the portal by a route OTHER than this configmap, and are
    /// therefore legitimately unbound here. Named ONE BY ONE with the route — never a prefix, never
    /// a whole-file exemption: an unexplained carve-out is how a guard stops being one.
    ///
    /// <para><c>Logging__LogLevel__Default</c> — memex-local applies verbose logging as a
    /// DEPLOYMENT-config override after the helm release ("Applying verbose logging … as a
    /// deployment-config override"), so it arrives as container env directly.</para>
    /// </summary>
    private static readonly HashSet<string> DeliveredOutOfBand =
        new(StringComparer.Ordinal) { "Logging__LogLevel__Default" };

    /// <summary>
    /// 🚨 The GENERAL form of the check above: <b>ANY</b> <c>config.memex_portal</c> key set in a
    /// values file this repo ships must be BOUND in the configmap template.
    ///
    /// <para><b>Why the PreWarm-only version was not enough.</b> That check has existed for a
    /// while and is correct — it is simply scoped to keys starting with <c>PreWarm__</c>, so the
    /// same defect went on shipping under every other prefix. Three instances reached a running
    /// deployment before this test existed:</para>
    /// <list type="number">
    ///   <item><c>Modules__Root</c> (#1924) — set in three AKS values files, rendered by nothing,
    ///     so no module could ever be store-activated and <c>/mcp</c> answered 404.</item>
    ///   <item><c>SelfUpdate__MinRollInterval</c>, <c>WebhookInbox__Targets__0</c>,
    ///     <c>AzureFoundry__Models__3</c> (#1925/#1999) — the restart floor sat inert, and the
    ///     typed one later crashed a portal outright on <c>'' is not a valid TimeSpan</c>.</item>
    ///   <item><c>Modules__Required__N</c> — the AKS overlays blank five entries the image
    ///     requires; un-templated, the override never arrived. On a laptop, where the modules
    ///     cannot be landed instead, the health check held <c>/health</c> at 503 and every
    ///     rollout timed out with the portal never serving.</item>
    /// </list>
    ///
    /// <para>Two things make this test honest rather than noisy, and both were found by running
    /// it:</para>
    /// <list type="bullet">
    ///   <item><b>Read the YAML, never the lines.</b> A line scan sweeps in the <c>secrets:</c>
    ///     section, whose keys are legitimately bound in <c>secrets.yaml</c> under
    ///     <c>.Values.secrets.memex_portal.*</c> — 19 false positives.</item>
    ///   <item><b>Accept the intermediate-variable shape.</b> <c>Portal__BaseUrl</c> is emitted
    ///     via <c>{{- $portalBaseUrl := … }}</c> so it can fall back to the ingress host. Demanding
    ///     the literal one-line binding flags it wrongly; requiring the key to be EMITTED and its
    ///     values path REFERENCED accepts both shapes and still cannot be satisfied by prose.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void EveryConfigKeySetInAValuesFile_IsBoundInTheConfigMap()
    {
        var root = FindRepoRoot();
        var configMap = File.ReadAllText(Path.Combine(
            root, "deploy", "helm", "templates", "memex-portal", "config.yaml"));

        var missing = ValuesFiles(root)
            .Where(File.Exists)
            .SelectMany(f => PortalConfigKeysOf(File.ReadAllLines(f))
                .Select(key => (file: Path.GetFileName(f), key)))
            .Distinct()
            .Where(x => !DeliveredOutOfBand.Contains(x.key))
            .Where(x => !IsWiredInConfigMap(configMap, x.key))
            .ToList();

        Assert.True(missing.Count == 0,
            "These config.memex_portal keys are set in a values file but never rendered by "
            + "deploy/helm/templates/memex-portal/config.yaml. The configmap names every key "
            + "explicitly and the Deployment's only env path is envFrom on it, so the setting "
            + "reaches NO container — silently, with helm reporting success: "
            + string.Join(", ", missing.Select(x => $"{x.key} (set in {x.file})"))
            + ". Template each one — and give a TYPED key a real default, because '' is not a "
            + "valid TimeSpan/int and crashes the portal at startup rather than reading as unset.");
    }

    /// <summary>
    /// EVERY tracked file that becomes a values overlay — not only the three the chart is rendered
    /// with directly. <c>values.deploy.example.yaml</c> is copied to the overlay
    /// <c>deploy/aks/scripts/deploy.sh</c> passes, and <c>values.local.yaml</c> to the one
    /// memex-local generates for a new install; both carry <c>config.memex_portal</c> mappings, so
    /// an untemplated key added to either reaches no container in exactly the same way (Copilot
    /// review, #2104).
    /// </summary>
    private static string[] ValuesFiles(string root) =>
    [
        Path.Combine(root, "deploy", "helm", "values.yaml"),
        Path.Combine(root, "deploy", "aks", "values.aks.yaml"),
        Path.Combine(root, "deploy", "aks", "scripts", "values.deploy.example.yaml"),
        Path.Combine(root, "deploy", "homebrew", "share", "values.local.defaults.yaml"),
        Path.Combine(root, "deploy", "homebrew", "share", "values.local.yaml"),
    ];

    /// <summary>The key family whose rendered range this pair of guards holds.</summary>
    private const string RequiredModulePrefix = "Modules__Required__";

    /// <summary>
    /// 🚨 The <c>Modules__Required__N</c> block must render a CONTIGUOUS range from 0, because a
    /// gap is invisible in exactly the way the whole key family already proved it can be.
    ///
    /// <para>The block is written index by index on purpose — a Helm <c>range</c> renders
    /// correctly and is invisible to <see cref="EveryConfigKeySetInAValuesFile_IsBoundInTheConfigMap"/>,
    /// which looks for the literal key. That contract is what makes the list checkable, and it is
    /// also what gives it a CEILING: a hand-written list stops somewhere. Both failure modes of a
    /// hand-written list are silent at deploy time, so both are asserted here:</para>
    /// <list type="number">
    ///   <item><b>A gap.</b> Delete the index-3 block and an overlay's
    ///     <c>Modules__Required__3</c> reaches no container while 4 and 5 still do — the
    ///     deployment then requires a DIFFERENT set than the one it declared, with helm
    ///     reporting success.</item>
    ///   <item><b>The ceiling drifting out of the comment.</b> The comment states the rendered
    ///     range so the next person adding a module reads the number instead of counting blocks;
    ///     this asserts the sentence still matches the template.</item>
    /// </list>
    ///
    /// <para>Both came from Memex#131: the block stopped at 4 while memex-cloud declared index 5
    /// (<c>MeshWeaver.Mcp.dll</c>), so <c>/mcp</c> went on answering 404 with <c>Mcp__BaseUrl</c>
    /// configured a few lines below it — the fifth recurrence of the named-key omission class
    /// (#1925/#1778/#1780/#2203/#2104).</para>
    /// </summary>
    [Fact]
    public void ModulesRequired_IsRenderedAsAContiguousRangeFromZero()
    {
        var configMap = ReadPortalConfigMap();
        var rendered = RenderedRequiredModuleIndices(configMap);

        Assert.True(rendered.Length > 0,
            $"deploy/helm/templates/memex-portal/config.yaml renders NO {RequiredModulePrefix}N "
            + "key at all. The overlays override the image's required-module list by index "
            + "through this block; with it gone every override reaches no container and the "
            + "image's own list silently wins (Memex#131).");

        Assert.True(rendered[0] == 0,
            $"the {RequiredModulePrefix}N block starts at index {rendered[0]}, not 0. "
            + "Configuration binds the array by index, so a list that does not start at 0 cannot "
            + "override the image's first entry.");

        var ceiling = rendered[^1];
        var gaps = Enumerable.Range(0, ceiling + 1).Except(rendered).ToArray();
        Assert.True(gaps.Length == 0,
            $"the {RequiredModulePrefix}N block renders "
            + string.Join(", ", rendered)
            + $" — index {string.Join(", ", gaps)} missing. A gap is silent: an overlay that sets "
            + "the missing index reaches NO container while its neighbours do, so the deployment "
            + "requires a different module set than the one it declares and helm still reports "
            + "success. Render every index from 0 to the ceiling.");

        // The comment must still name the range it renders. This is the only thing standing
        // between "the ceiling is written down where the next person reads it" and a sentence
        // that quietly describes the previous ceiling — which is how Memex#128 came to declare
        // index 5 against a comment that said the chart renders 0..4.
        Assert.True(configMap.Contains($"renders 0..{ceiling}", StringComparison.Ordinal),
            $"the {RequiredModulePrefix}N block renders 0..{ceiling}, but no comment in "
            + "deploy/helm/templates/memex-portal/config.yaml says so. The ceiling is only useful "
            + "written down: the overlay that hits it lives in another repository, and its author "
            + "sees this file, not this test. Say `renders 0..N` in the block's comment.");
    }

    /// <summary>
    /// 🚨 No values file may declare a <c>Modules__Required__N</c> ABOVE the rendered ceiling —
    /// and the failure says what the ceiling is.
    ///
    /// <para><see cref="EveryConfigKeySetInAValuesFile_IsBoundInTheConfigMap"/> already fails on
    /// such a key, but it fails as one of a generic list of "template each one", which is the
    /// wrong instruction for this family: the fix is not a new key somewhere, it is one more
    /// <c>hasKey</c> block on an ordered list whose ceiling the author has to know. Memex#131 is
    /// the whole argument — the person adding index 5 could see the key was refused but not what
    /// the limit was, and the chart is in a different repository from the overlay.</para>
    /// </summary>
    [Fact]
    public void NoValuesFile_DeclaresAModulesRequiredIndexAboveTheCeiling()
    {
        var root = FindRepoRoot();
        var configMap = ReadPortalConfigMap();
        var rendered = RenderedRequiredModuleIndices(configMap);
        var ceiling = rendered.Length == 0 ? -1 : rendered[^1];

        var over = ValuesFiles(root)
            .Where(File.Exists)
            .SelectMany(f => PortalConfigKeysOf(File.ReadAllLines(f))
                .Where(k => k.StartsWith(RequiredModulePrefix, StringComparison.Ordinal))
                .Select(k => (file: Path.GetFileName(f), key: k, index: IndexOfKey(k))))
            .Where(x => x.index > ceiling)
            .Distinct()
            .ToList();

        Assert.True(over.Count == 0,
            "These values entries declare a required-module index the chart does not render, so "
            + "they reach NO container and the module is not required at all — silently, with "
            + "helm reporting success: "
            + string.Join(", ", over.Select(x => $"{x.key} (set in {x.file})"))
            + $". deploy/helm/templates/memex-portal/config.yaml renders {RequiredModulePrefix}"
            + $"0..{ceiling} — RAISE THE CEILING by adding one more `hasKey` block per index (a "
            + "Helm range would render correctly and be invisible to the key-literal guards), and "
            + "update the comment that states the rendered range.");
    }

    private static string ReadPortalConfigMap() =>
        File.ReadAllText(Path.Combine(
            FindRepoRoot(), "deploy", "helm", "templates", "memex-portal", "config.yaml"));

    /// <summary>
    /// The <c>Modules__Required__N</c> indices the configmap actually BINDS, ascending. Candidates
    /// come from executable template text only and each is confirmed through
    /// <see cref="IsWiredInConfigMap"/>, so a key named in a Helm comment — this file has nine
    /// such blocks, and the one above this very family names indices in prose — can never count as
    /// rendered.
    /// </summary>
    private static int[] RenderedRequiredModuleIndices(string configMap) =>
        Regex.Matches(
                ExecutableTemplateOf(configMap),
                $@"^\s{{2}}{Regex.Escape(RequiredModulePrefix)}(\d+):\s",
                RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Where(i => IsWiredInConfigMap(configMap, $"{RequiredModulePrefix}{i}"))
            .Distinct()
            .Order()
            .ToArray();

    /// <summary>The numeric suffix of a <c>Modules__Required__N</c> key; -1 when it is not one.
    /// </summary>
    private static int IndexOfKey(string key) =>
        int.TryParse(key[RequiredModulePrefix.Length..], NumberStyles.None,
            CultureInfo.InvariantCulture, out var index)
            ? index
            : -1;

    /// <summary>
    /// The keys under <c>config.memex_portal</c> of a values file — section-scoped, so the
    /// <c>secrets:</c> block (bound in secrets.yaml, not here) never counts. Comments and nested
    /// values are dropped; only mapping keys at the section's own indent are returned.
    /// </summary>
    private static IEnumerable<string> PortalConfigKeysOf(IEnumerable<string> lines)
    {
        var inConfig = false;
        var inPortal = false;
        foreach (var raw in lines)
        {
            if (raw.TrimStart().StartsWith('#') || raw.Trim().Length == 0)
                continue;
            var indent = raw.Length - raw.TrimStart().Length;
            var line = raw.Trim();

            if (indent == 0)
            {
                // A new top-level block ends whichever one we were in — this is what keeps
                // `secrets:` out, and it is the whole reason the scan is section-aware.
                inConfig = line.StartsWith("config:", StringComparison.Ordinal);
                inPortal = false;
                continue;
            }
            if (!inConfig)
                continue;
            if (indent == 2)
            {
                inPortal = line.StartsWith("memex_portal:", StringComparison.Ordinal);
                continue;
            }
            if (!inPortal || indent != 4)
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon].Trim();
            if (key.Length > 0)
                yield return key;
        }
    }

    /// <summary>
    /// Whether the configmap actually WIRES <paramref name="key"/>: it emits the key as a
    /// configmap entry AND references its values path somewhere in the template. Both halves are
    /// needed — emitting alone would accept a hard-coded value that ignores the deployment, and
    /// referencing alone would accept a Helm variable computed and never written out. Together
    /// they also accept the intermediate-variable shape, which the stricter one-line form rejects.
    /// </summary>
    private static bool IsWiredInConfigMap(string configMap, string key)
    {
        // 🚨 Judge EXECUTABLE template text only. Nine {{- /* … */}} blocks in that file name keys
        // and their .Values paths in prose; one that writes its example line at the entries' own
        // two-space indent satisfies BOTH halves below while templating nothing — verified by
        // seeding exactly that shape, which this helper catches and the un-stripped form passes.
        // "A check a comment can satisfy is not a check" (Copilot review, #2104).
        var code = ExecutableTemplateOf(configMap);
        return Regex.IsMatch(code, $@"^\s{{2}}{Regex.Escape(key)}:\s", RegexOptions.Multiline)
            && code.Contains($".Values.config.memex_portal.{key}", StringComparison.Ordinal);
    }

    /// <summary>The template with its comments removed: Helm's own <c>{{/* … */}}</c> blocks —
    /// which no '#'-stripping can reach — and YAML <c>#</c> lines.</summary>
    private static string ExecutableTemplateOf(string template)
    {
        var withoutHelmComments = Regex.Replace(
            template, @"\{\{-?\s*/\*.*?\*/\s*-?\}\}", "", RegexOptions.Singleline);
        return string.Join("\n", withoutHelmComments
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith('#')));
    }

    /// <summary>
    /// 🚨 <b>An armed readiness gate with no sweep behind it is a SILENT LIE.</b>
    ///
    /// <para>The bake gate reads state that only the compiling sweep writes, and the health check
    /// deliberately reports Healthy while the bake is <c>NotStarted</c> so a configuration mistake
    /// can never black-hole a pod. The consequence is that <c>GateReadiness=true</c> with
    /// <c>DynamicTypes=false</c> yields a gate that is registered, permanently green, and protects
    /// NOTHING — which is precisely the failure the gate exists to prevent.</para>
    ///
    /// <para>The portal shouts that combination at Critical on startup, but a log line is a
    /// runtime discovery; this is the compile-time one. It matters most right now, because the
    /// fleet has just switched the sweep OFF: the next person who re-arms a gate "for safety"
    /// without also restoring the sweep gets false confidence, not safety.</para>
    /// </summary>
    [Fact]
    public void NoValuesFileArmsTheBakeGateWithoutTheSweepBehindIt()
    {
        var root = FindRepoRoot();
        var offenders = new[]
            {
                Path.Combine(root, "deploy", "helm", "values.yaml"),
                Path.Combine(root, "deploy", "aks", "values.aks.yaml"),
                Path.Combine(root, "deploy", "homebrew", "share", "values.local.defaults.yaml"),
            }
            .Where(File.Exists)
            .Select(f => (file: Path.GetFileName(f), lines: File.ReadAllLines(f)
                .Select(l => l.Trim())
                .Where(l => !l.StartsWith('#'))
                .ToList()))
            .Where(x => Setting(x.lines, "PreWarm__GateReadiness") == "true"
                        && Setting(x.lines, "PreWarm__DynamicTypes") != "true")
            .Select(x => x.file)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These values files arm PreWarm__GateReadiness while PreWarm__DynamicTypes is not "
            + "true. The gate can only reach a verdict from the compiling sweep, so with the sweep "
            + "off it reports healthy on every rollout and protects nothing — worse than no gate, "
            + "because the deployment believes it is protected. Either restore the sweep or turn "
            + "the gate off. Offending file(s): " + string.Join(", ", offenders));
    }

    /// <summary>The value of a <c>KEY: "value"</c> line, unquoted, or null when unset.</summary>
    private static string? Setting(IEnumerable<string> lines, string key) =>
        lines.FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal))
            ?.Split(':', 2)[1].Trim().Trim('"');

    /// <summary>
    /// The local instance must name a bundle root — adopt-only with nothing to adopt from reports
    /// every dynamic type as uncovered forever, which is honest but useless.
    /// </summary>
    [Fact]
    public void LocalDefaults_NameABundleRootToAdoptFrom()
    {
        var values = File.ReadAllLines(Path.Combine(
                FindRepoRoot(), "deploy", "homebrew", "share", "values.local.defaults.yaml"))
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .ToList();

        Assert.True(
            values.Any(l => l.StartsWith("PreWarm__PrebuiltBundleRoot:", StringComparison.Ordinal)
                            && l.Contains('/', StringComparison.Ordinal)),
            "The local instance must name a bundle root, otherwise adopt-only has no bundles to "
            + "adopt from and reports every dynamic type as uncovered forever.");
    }

    /// <summary>
    /// Whether the configmap actually BINDS <paramref name="key"/> to its values entry, i.e. emits
    /// <c>&lt;key&gt;: "{{ .Values.config.memex_portal.&lt;key&gt; …}}"</c>. The value binding is the
    /// discriminator on purpose: every key in that file is preceded by a Helm comment block
    /// (<c>{{- /* … */}}</c>) that frequently opens with the key name, so a looser "the key appears
    /// somewhere" test would be satisfied by the prose explaining a key nobody templated — the
    /// exact failure mode this guard exists to catch. The trailing character must be a space or the
    /// closing brace so that one key cannot be matched by another that merely starts with it.
    /// </summary>
    private static bool IsBoundInConfigMap(string configMap, string key)
    {
        var binding = $"{key}: \"{{{{ .Values.config.memex_portal.{key}";
        var at = configMap.IndexOf(binding, StringComparison.Ordinal);
        if (at < 0)
            return false;
        var next = at + binding.Length;
        return next < configMap.Length && (configMap[next] == ' ' || configMap[next] == '}');
    }

    /// <summary>The <c>publish-bake</c> job's own YAML block: from its two-space-indented key to
    /// the next job key at the same indentation. The terminator is matched as a bare
    /// <c>  &lt;name&gt;:</c> line rather than "indented and ends with a colon", so a two-space
    /// comment between jobs cannot truncate the block and turn this guard into a false failure.
    /// </summary>
    private static string ReadJobBlock()
    {
        var path = Path.Combine(FindRepoRoot(), Workflow);
        Assert.True(File.Exists(path), $"expected {Workflow} at the repo root");
        var lines = File.ReadAllLines(path);

        var start = Array.FindIndex(lines, l => l.Equals($"  {JobName}:", StringComparison.Ordinal));
        Assert.True(start >= 0, $"no '{JobName}:' job in {Workflow} — the platform's own content "
            + "bake is what publishes the shipped NodeType bundles the portals adopt; deleting it "
            + "returns every pod to compiling every shipped type at boot (#1347, #1725).");

        var end = Array.FindIndex(lines, start + 1, IsJobKey);
        if (end < 0)
            end = lines.Length;
        return string.Join("\n", lines[start..end]);
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
