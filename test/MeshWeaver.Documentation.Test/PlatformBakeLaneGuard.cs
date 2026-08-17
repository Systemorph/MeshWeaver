using System;
using System.IO;
using System.Linq;
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
                    && job.Contains("--bake-output /bake", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must bake the platform's content by running mw-plugin-test "
            + "INSIDE the image this run built (docker run … --entrypoint /app/mw-plugin-test … "
            + "--bake-output /bake). The framework identity is a property of the shipped binaries, "
            + "so only the image the pods run can produce a bake those pods will adopt.");

        Assert.True(job.Contains("--platform linux/amd64", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must pin the bake platform explicitly. Architecture is part "
            + "of the identity — the amd64 and arm64 variants of one multi-arch image resolve "
            + "different identities — and every AKS node is amd64, so inheriting whatever "
            + "architecture the runner happens to be would silently key the bake to an identity no "
            + "pod resolves.");

        Assert.True(job.Contains("publish-bake-bundles.sh", StringComparison.Ordinal),
            $"'{JobName}' in {Workflow} must publish through .github/scripts/publish-bake-bundles.sh "
            + "— the one script whose '_complete' sentinel matches "
            + "ShippedPrebuiltBundles.CompletionSentinelFileName.");
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
    /// 🚨 CD bakes ONLY the content the image itself embeds. Everything a deployment receives
    /// already built — node-repo content (Plugins, Education, Reinsurance, SocialMedia), Store
    /// packages, and the samples trees — is ADOPTED from a bundle its own lane published under the
    /// same framework identity. Re-compiling it here would redo that work, burn CD wall-clock, and
    /// re-derive assemblies that were meant to be authoritative.
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

        // The strongest form of "CD cannot compile someone else's module": it never checks out
        // someone else's repository. One `repository:` input would make it possible.
        var text = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), Workflow)));
        Assert.False(text.Contains("repository:", StringComparison.Ordinal),
            $"{Workflow} must check out no repository but this one. Node-repo and Store content "
            + "arrives already compiled and is adopted; a checkout of another repo here would let "
            + "CD re-derive assemblies whose authoritative build belongs to that repo's own "
            + "publish-bake lane.");
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
            .Where(x => x.text.Contains("gh run download", StringComparison.Ordinal)
                        || x.text.Contains("download-artifact", StringComparison.Ordinal))
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
