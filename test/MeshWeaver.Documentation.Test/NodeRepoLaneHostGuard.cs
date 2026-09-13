#pragma warning disable CS1591

using System;
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

    [Fact]
    public void ThePlatformsPluginsBake_PassesThePortalOfThePromotedSet()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), MainCd));
        // The job block: its 4-space-indented lines (plus blank lines and 2-space comments), up to
        // the next top-level job key.
        var job = Regex.Match(text, @"\n  plugins-bake:\n(?<body>(?:(?:    .*|  #.*)\n|\n)+?)(?=  [a-z][a-z-]*:\n)");
        Assert.True(job.Success, $"{MainCd} must have a `plugins-bake` job");
        var body = ExecutableLinesOf(job.Groups["body"].Value);
        Assert.Contains("platform-image: meshweaver.azurecr.io/memex-portal-ai:", body, StringComparison.Ordinal);
        Assert.Contains("platform-image-digest: ${{ needs.plugins-bake-image.outputs.platform_digest }}", body, StringComparison.Ordinal);
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
