#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the ONE shape of the node-repo bake and gate lanes since #3022:
/// <b>the tester EXECUTES, the platform (portal) image SUPPLIES the reference set, the framework
/// identity and the runtime</b> — both verbs run from the composed gate host (the portal's
/// <c>/app</c> + the tester CLI), and the compile takes that host's <c>/app</c> AND its
/// implementation shared frameworks as the reference set.
///
/// <para>Why a text guard: the property lives in CI, not in the product. A lane that quietly went
/// back to <c>--entrypoint /app/mw-plugin-test "$IMAGE"</c> would compile every NodeType against
/// the tester image's <c>/app</c> again — a strict subset of the portal's (88 vs 219 assemblies on
/// 3.0.0-rc9.ci.7534) — and the regression would surface as CONTENT errors on the next satellite
/// pin bump, exactly the misdiagnosis that held the release wave on 2026-09-02. Nothing in a
/// green run distinguishes the two shapes; this does.</para>
///
/// <para>🚨 MeshWeaver#4113 gave the GATE lane a second way to obtain those bytes — off the runner
/// node's <c>/opt/platform</c> volume, running the tester as a PROCESS instead of in a container —
/// so the invariants are asserted per MODE rather than relaxed. Both modes must start the composed
/// host; both must name the PORTAL's <c>/app</c> as <c>--app</c>; the compile must name the same
/// host's shared frameworks. The publish-bake lane is container-only and keeps the literal
/// pre-#4113 assertions.</para>
/// </summary>
public class NodeRepoLaneHostGuard
{
    private const string PublishBake = ".github/workflows/node-repo-publish-bake.yml";
    private const string Gate = ".github/workflows/node-repo-gate.yml";
    private const string MainCd = ".github/workflows/main-cd.yml";

    [Theory]
    [InlineData(PublishBake)]
    [InlineData(Gate)]
    public void TheLane_DeclaresThePlatformImageAsARequiredInput(string workflow)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), workflow));
        var input = Regex.Match(text, @"\n      platform-image:\n(?<body>(?:        .*\n)+)");
        Assert.True(input.Success, $"{workflow} must declare a `platform-image` input — the portal the bake compiles against.");
        Assert.Contains("required: true", input.Groups["body"].Value, StringComparison.Ordinal);
        Assert.True(Regex.IsMatch(text, @"\n      platform-image-digest:\n"),
            $"{workflow} must declare a `platform-image-digest` input — the pin resolved as image-digest is.");
    }

    [Fact]
    public void ThePublishBakeLane_RunsCompileAndGateFromTheComposedHost_InsideThePlatformImage()
    {
        var lines = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), PublishBake)));

        // The host is composed by the one script, from the portal's /app and the tester's.
        Assert.Contains("compose-gate-host.sh", lines, StringComparison.Ordinal);

        // Every tester invocation that compiles or gates content starts the PORTAL image's dotnet on
        // the composed host — never the tester image's own entrypoint against its own /app.
        var contentRuns = lines.Split('\n')
            .Where(l => l.Contains("--entrypoint", StringComparison.Ordinal)
                        && (l.Contains(" compile /repo", StringComparison.Ordinal)
                            || l.Contains("mw-plugin-test.dll /repo", StringComparison.Ordinal)
                            || l.Contains("mw-plugin-test\" /repo", StringComparison.Ordinal)))
            .Select(l => l.Trim())
            .ToArray();
        Assert.True(contentRuns.Length == 2,
            $"{PublishBake} must run exactly one compile and one gate over /repo (found {contentRuns.Length}):\n  "
            + string.Join("\n  ", contentRuns));
        foreach (var run in contentRuns)
        {
            Assert.True(run.Contains("--entrypoint dotnet \"$PLATFORM_REF\" /host/mw-plugin-test.dll", StringComparison.Ordinal),
                $"{PublishBake}: a content run must start the PLATFORM image's dotnet on the composed host, "
                + $"not the tester image's own entrypoint against its own /app (#3022). Offending line:\n  {run}");
        }
        var compile = contentRuns.Single(r => r.Contains(" compile /repo", StringComparison.Ordinal));
        Assert.True(
            lines.Contains("--app /app --shared-frameworks /usr/share/dotnet/shared", StringComparison.Ordinal),
            $"{PublishBake}: the compile must take the portal's /app AND its implementation shared frameworks "
            + $"as the reference set (--app /app --shared-frameworks /usr/share/dotnet/shared). Compile line:\n  {compile}");
        var gate = contentRuns.Single(r => !r.Contains(" compile /repo", StringComparison.Ordinal));
        Assert.True(gate.Contains("/repo --app /app", StringComparison.Ordinal),
            $"{PublishBake}: the gate must assert it RUNS AS the platform host (--app /app). Gate line:\n  {gate}");

        AssertTheTwoImagesAreProvenOneBuild(PublishBake, lines);
    }

    /// <summary>
    /// The GATE lane's version of the same property, across BOTH ways it can obtain the platform
    /// (MeshWeaver#4113). The tester is started in exactly one place — the <c>run_tester</c>
    /// function — whose two branches are the two modes; the content runs go through it, so the
    /// assertions are: one compile, one gate, both through that function; the container branch
    /// still starts the PORTAL image's <c>dotnet</c> on the composed host; the volume branch starts
    /// the same composed host with the runner's <c>dotnet</c>; and the reference set is the
    /// PORTAL's <c>/app</c> plus the SAME host's shared frameworks either way.
    /// </summary>
    [Fact]
    public void TheGateLane_RunsCompileAndGateFromTheComposedHost_InBothPlatformModes()
    {
        var lines = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), Gate)));

        Assert.Contains("compose-gate-host.sh", lines, StringComparison.Ordinal);

        // 1. ONE definition of how the tester is started, with exactly two branches.
        Assert.True(lines.Contains("          run_tester() {", StringComparison.Ordinal),
            $"{Gate}: the tester must be started in exactly one place — the `run_tester` function — so a mode "
            + "cannot acquire a second, unasserted way to start it.");
        Assert.True(
            lines.Contains("\"$DOTNET\" \"$HOST_DIR/mw-plugin-test.dll\" \"$@\"", StringComparison.Ordinal),
            $"{Gate}: the VOLUME mode must start the COMPOSED HOST ($HOST_DIR/mw-plugin-test.dll) with the "
            + "runner's dotnet — never the tester's own /app off the share, whose /app is a strict subset of "
            + "the portal's (#3022).");
        Assert.True(
            lines.Contains("--entrypoint dotnet \"$PLATFORM_REF\" /host/mw-plugin-test.dll \"$@\"", StringComparison.Ordinal),
            $"{Gate}: the CONTAINER mode must still start the PLATFORM image's dotnet on the composed host (#3022).");

        // 2. Exactly one compile and one gate over the repo tree, both through that function.
        var contentRuns = lines.Split('\n')
            .Where(l => l.TrimStart().StartsWith("run_tester ", StringComparison.Ordinal)
                        && l.Contains("\"$P_REPO\"", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .ToArray();
        Assert.True(contentRuns.Length == 2,
            $"{Gate} must run exactly one compile and one gate over the repo tree through run_tester "
            + $"(found {contentRuns.Length}):\n  " + string.Join("\n  ", contentRuns));
        var compile = contentRuns.Single(r => r.Contains("run_tester compile", StringComparison.Ordinal));
        var gate = contentRuns.Single(r => !r.Contains("run_tester compile", StringComparison.Ordinal));

        // 3. The reference set, per mode: the PORTAL's /app and the SAME host's shared frameworks.
        Assert.True(lines.Contains("--app \"$P_APP\" --shared-frameworks \"$P_SHARED\"", StringComparison.Ordinal),
            $"{Gate}: the compile must take the portal's /app AND its implementation shared frameworks as the "
            + $"reference set. Compile line:\n  {compile}");
        Assert.True(gate.Contains("--app \"$P_APP\"", StringComparison.Ordinal),
            $"{Gate}: the gate must assert it RUNS AS the platform host (--app). Gate line:\n  {gate}");
        Assert.True(lines.Contains("P_APP=/app", StringComparison.Ordinal)
                    && lines.Contains("P_SHARED=/usr/share/dotnet/shared", StringComparison.Ordinal),
            $"{Gate}: in CONTAINER mode --app/--shared-frameworks must resolve to the portal IMAGE's own "
            + "/app and /usr/share/dotnet/shared, as before #4113.");
        Assert.True(lines.Contains("P_APP=\"$PORTAL_APP\"", StringComparison.Ordinal)
                    && lines.Contains("P_SHARED=\"$RUNNER_SHARED_FRAMEWORKS\"", StringComparison.Ordinal),
            $"{Gate}: in VOLUME mode --app must resolve to the PORTAL's /app as the platform resolver reported "
            + "it, and --shared-frameworks to the runner's dotnet root — the step that installs it asserts the "
            + "framework band against the portal's own runtimeconfig.");

        // 4. The platform on the volume is RESOLVED and PROVEN, and never degrades into a pull.
        Assert.Contains("resolve-gate-platform.sh", lines, StringComparison.Ordinal);
        foreach (var dockerVerb in new[] { "docker pull", "docker create", "docker login" })
        {
            var uses = lines.Split('\n').Where(l => l.Contains(dockerVerb, StringComparison.Ordinal)).ToArray();
            Assert.True(uses.Length > 0,
                $"{Gate}: `{dockerVerb}` must still exist for CONTAINER mode — this guard would otherwise pass "
                + "vacuously the day the container path was deleted rather than kept.");
        }
        Assert.True(Regex.IsMatch(lines, @"if: steps\.platform-source\.outputs\.mode == 'container'"),
            $"{Gate}: every step that pulls or logs in must be gated on the MEASURED container mode, so a "
            + "volume-mode shard can never reach a registry.");

        AssertTheTwoImagesAreProvenOneBuild(Gate, lines);
    }

    /// <summary>
    /// In VOLUME mode the composed host is started with the runner's <c>dotnet</c>, so the portal image's
    /// <c>MESHWEAVER_PLATFORM_VERSION</c> (which <c>docker run</c> inherits in container mode) never reaches the
    /// tester, and <c>PlatformBuildInfo</c> falls back to the run-less assembly version (<c>3.1.0+&lt;sha&gt;</c>):
    /// every package floored on the verified set was refused at install (MeshWeaver.Plugins#3237). The lane must
    /// hand the served SET name over before the first tester call, through a shape guard that is RED on anything
    /// the pipeline does not mint. The guard's own pattern is executed here against valid and invalid values.
    /// </summary>
    [Fact]
    public void TheGateLane_VolumeHost_RunsAsTheSetItWasServed()
    {
        var lines = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), Gate)));

        Assert.True(lines.Contains("VOLUME_SET: ${{ steps.platform-source.outputs.set }}", StringComparison.Ordinal),
            $"{Gate}: the gate step must receive the set the volume served (steps.platform-source.outputs.set).");
        var export = lines.IndexOf("export MESHWEAVER_PLATFORM_VERSION=\"$VOLUME_SET\"", StringComparison.Ordinal);
        Assert.True(export >= 0, $"{Gate}: volume mode must export the served set as MESHWEAVER_PLATFORM_VERSION.");
        var volumeBranch = lines.IndexOf("P_SHARED=\"$RUNNER_SHARED_FRAMEWORKS\"", StringComparison.Ordinal);
        var containerBranch = lines.IndexOf("P_APP=/app", StringComparison.Ordinal);
        Assert.True(volumeBranch >= 0 && volumeBranch < export && export < containerBranch,
            $"{Gate}: the export must sit in the VOLUME branch (container mode inherits the image's own variable).");
        Assert.True(Regex.IsMatch(lines[export..], @"\n\s*run_tester "),
            $"{Gate}: no tester invocation follows the export — it would reach nothing.");
        Assert.True(lines[..export].Split('\n').All(l => !l.TrimStart().StartsWith("run_tester ", StringComparison.Ordinal)),
            $"{Gate}: no tester may be started before the platform version is exported.");

        var guard = Regex.Match(lines, @"\[\[ ""\$\{VOLUME_SET:-\}"" =~ (?<re>\S+) \]\]");
        Assert.True(guard.Success, $"{Gate}: the export must be preceded by the release-version shape guard on VOLUME_SET.");
        Assert.True(guard.Index < export, $"{Gate}: the shape guard must run BEFORE the export.");
        var shape = new Regex(guard.Groups["re"].Value);
        foreach (var ok in new[] { "3.1.10365", "3.0.0-ci.10317", "3.0.0-rc9.ci.12", "3.0.0-edge.5" })
            Assert.True(shape.IsMatch(ok), $"the guard must accept the minted version '{ok}'");
        foreach (var bad in new[] { "", "latest", "3.1", "3.0.0-alpha", "sha256:abc" })
            Assert.False(shape.IsMatch(bad), $"the guard must refuse '{bad}' — the host could not be told which platform it runs");
    }

    private static void AssertTheTwoImagesAreProvenOneBuild(string workflow, string lines)
    {
        // The two images must be proven ONE build before the composition is trusted.
        Assert.Contains("framework-identity", lines, StringComparison.Ordinal);
        Assert.Contains("--expect \"$tester\"", lines, StringComparison.Ordinal);

        // No content run against the tester image's own /app survives anywhere in the lane.
        Assert.DoesNotContain("--entrypoint /app/mw-plugin-test \"$IMAGE\" compile", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("--entrypoint /app/mw-plugin-test \"${{ steps.image.outputs.ref }}\" compile", lines, StringComparison.Ordinal);
        Assert.True(workflow.Length > 0);
    }

    /// <summary>
    /// 🚨 Policy <c>platform-module-deploy-separate</c>: the PLATFORM deploy (<c>main-cd.yml</c>) calls
    /// no module lane — it neither bakes nor seals a MeshWeaver.Plugins publication (that lane's own
    /// caller is MeshWeaver.Plugins' <c>ci.yml</c>), so the two facts that used to pin core's
    /// <c>plugins-bake</c> call (its portal input and its generation layout) have no subject here any
    /// more. The layout every caller gets is the lane's own default, held by the next test.
    /// </summary>
    [Fact]
    public void ThePlatformDeploy_CallsNoModuleLane()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), MainCd));
        Assert.Empty(ModuleLaneCalls(text));
        // Negative control: a module lane called from the platform deploy is caught by name.
        var mutated = text.Replace("\n  published-modules:\n",
            "\n  plugins-bake:\n    uses: ./.github/workflows/node-repo-publish-bake.yml\n\n  published-modules:\n",
            StringComparison.Ordinal);
        Assert.NotEqual(text, mutated);
        Assert.Contains(ModuleLaneCalls(mutated), c => c.Contains("node-repo-publish-bake.yml", StringComparison.Ordinal));
    }

    private static List<string> ModuleLaneCalls(string workflowText) =>
        Regex.Matches(workflowText, @"(?m)^\s*uses:\s*\./\.github/workflows/(?<lane>node-repo-(?:publish-bake|module-pack|module-publish)\.yml)")
            .Select(m => m.Groups["lane"].Value).ToList();

    /// <summary>
    /// 🚨 <b>THE LANE'S OWN DEFAULT IS THE FLEET'S PUBLICATION LAYOUT</b> (MeshWeaver#3461, phase 4),
    /// and reverting it to <c>flat</c> is the one change in this area that nothing else would notice.
    ///
    /// <para>None of the six node repositories passes <c>publication-layout</c> (measured
    /// 2026-09-14 over <c>ci.yml</c> on <c>main</c> of Plugins, Crm, Education, Reinsurance,
    /// SocialMedia and Manufacturing), so this <c>default:</c> is what decides where every
    /// satellite's publication is written. Ask the question that catches this class: <i>if this
    /// default were <c>flat</c> right now, would anything else go red?</i> No — the overlap harness
    /// supplies <c>BAKE_PUBLICATION_LAYOUT</c> to the script directly and never reads a caller, and
    /// core's own explicit input is gone with its call (policy
    /// <c>platform-module-deploy-separate</c>). Every satellite lane would silently return to in-place publication, which is the
    /// republish window #3461 exists to close. Raised by Copilot on the pull request that moved it.</para>
    ///
    /// <para>The second assertion is the other half of the same property: the step must pass the
    /// input THROUGH to <c>BAKE_PUBLICATION_LAYOUT</c>. The script's own fallback is deliberately
    /// <c>flat</c> — it is the direct-invocation default — so a lane that stopped exporting the
    /// variable would reach that fallback and publish flat with this default still reading
    /// <c>generation</c>.</para>
    /// </summary>
    [Fact]
    public void ThePublishBakeLane_DefaultsToTheGenerationLayout()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), PublishBake));
        var input = Regex.Match(text, @"\n      publication-layout:\n(?<body>(?:        .*\n|\n)+?)(?=      [a-z][a-z-]*:\n)");
        Assert.True(input.Success, $"{PublishBake} must declare a `publication-layout` input — the fleet's publication layout.");
        Assert.Contains("default: 'generation'", input.Groups["body"].Value, StringComparison.Ordinal);

        Assert.Contains(
            "BAKE_PUBLICATION_LAYOUT: ${{ inputs.publication-layout }}",
            ExecutableLinesOf(text),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 <b>NO caller in this repository publishes FLAT, and that is the mitigation — not a
    /// coincidence worth measuring once</b> (MeshWeaver#3461).
    ///
    /// <para>The residual the generation layout still has is a FLAT publication on a prefix whose
    /// <c>_current</c> the run could not resolve: it publishes IN PLACE at the prefix, which since
    /// #3461 phase 5 is a location the generation publishers DISPOSE of rather than refresh — so a
    /// flat run re-creates a sealed copy there that only a torn pointer read would ever be served,
    /// and that the prefix's next generation publication removes again. #4249 makes it unreachable
    /// for a run that RESOLVES a live pointer (it publishes a generation instead, loudly), so what
    /// is left needs a writer that takes the flat arm: a caller passing <c>flat</c>, or a run whose
    /// <c>publish-bake-bundles.sh</c> predates #4249 — and the second cannot be helped by any code
    /// added to today's script, because it is not running it.</para>
    ///
    /// <para>So the reachable half is kept at zero HERE. Measured 2026-09-14: no caller in the
    /// fleet passes the input at all. This asserts the repository's own callers never introduce one
    /// on the flat arm; a satellite that wanted <c>flat</c> would have to say so in its own
    /// <c>ci.yml</c>, which is a visible act rather than a default.</para>
    /// </summary>
    [Fact]
    public void NoCallerInThisRepository_PublishesFlat()
    {
        foreach (var workflow in Directory.EnumerateFiles(
                     Path.Combine(FindRepoRoot(), ".github", "workflows"), "*.yml"))
        {
            var lines = ExecutableLinesOf(File.ReadAllText(workflow));
            Assert.DoesNotContain("publication-layout: flat", lines, StringComparison.Ordinal);
            Assert.DoesNotContain("publication-layout: 'flat'", lines, StringComparison.Ordinal);
        }
    }

    private static string ExecutableLinesOf(string yaml) =>
        string.Join('\n', yaml.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (MeshWeaver.slnx) above the test bin");
        return dir!.FullName;
    }
}
