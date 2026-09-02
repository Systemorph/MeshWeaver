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
/// <c>/app</c> + the tester CLI) started by the portal image, and the compile takes
/// <c>--app /app --shared-frameworks /usr/share/dotnet/shared</c>.
///
/// <para>Why a text guard: the property lives in CI, not in the product. A lane that quietly went
/// back to <c>--entrypoint /app/mw-plugin-test "$IMAGE"</c> would compile every NodeType against
/// the tester image's <c>/app</c> again — a strict subset of the portal's (88 vs 219 assemblies on
/// 3.0.0-rc9.ci.7534) — and the regression would surface as CONTENT errors on the next satellite
/// pin bump, exactly the misdiagnosis that held the release wave on 2026-09-02. Nothing in a
/// green run distinguishes the two shapes; this does.</para>
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

    [Theory]
    [InlineData(PublishBake)]
    [InlineData(Gate)]
    public void TheLane_RunsCompileAndGateFromTheComposedHost_InsideThePlatformImage(string workflow)
    {
        var lines = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), workflow)));

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
            $"{workflow} must run exactly one compile and one gate over /repo (found {contentRuns.Length}):\n  "
            + string.Join("\n  ", contentRuns));
        foreach (var run in contentRuns)
        {
            Assert.True(run.Contains("--entrypoint dotnet \"$PLATFORM_REF\" /host/mw-plugin-test.dll", StringComparison.Ordinal),
                $"{workflow}: a content run must start the PLATFORM image's dotnet on the composed host, "
                + $"not the tester image's own entrypoint against its own /app (#3022). Offending line:\n  {run}");
        }
        var compile = contentRuns.Single(r => r.Contains(" compile /repo", StringComparison.Ordinal));
        Assert.True(
            lines.Contains("--app /app --shared-frameworks /usr/share/dotnet/shared", StringComparison.Ordinal),
            $"{workflow}: the compile must take the portal's /app AND its implementation shared frameworks "
            + $"as the reference set (--app /app --shared-frameworks /usr/share/dotnet/shared). Compile line:\n  {compile}");
        var gate = contentRuns.Single(r => !r.Contains(" compile /repo", StringComparison.Ordinal));
        Assert.True(gate.Contains("/repo --app /app", StringComparison.Ordinal),
            $"{workflow}: the gate must assert it RUNS AS the platform host (--app /app). Gate line:\n  {gate}");

        // The two images must be proven ONE build before the composition is trusted.
        Assert.Contains("framework-identity /portal --expect", lines, StringComparison.Ordinal);

        // No content run against the tester image's own /app survives anywhere in the lane.
        Assert.DoesNotContain("--entrypoint /app/mw-plugin-test \"$IMAGE\" compile", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("--entrypoint /app/mw-plugin-test \"${{ steps.image.outputs.ref }}\" compile", lines, StringComparison.Ordinal);
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
