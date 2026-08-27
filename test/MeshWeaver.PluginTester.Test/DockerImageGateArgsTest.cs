#pragma warning disable CS1591

using System;
using System.Linq;
using MeshWeaver.ComboVerifier;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// Pins the docker command line <see cref="DockerImageGate"/> builds — the combo gate's ONLY
/// production seam, and the one part of the Candidate Release Protocol that no test and no CI job
/// had ever executed (#2274).
///
/// <para><b>Why an argv test rather than a docker one.</b> The defect this exists to catch is not
/// a crash; it is a run that succeeds against the WRONG BYTES. A candidate reference is a
/// multi-arch manifest list, docker reports the same list digest for every variant, and an
/// unpinned <c>docker run</c> silently selects the HOST's architecture — so a gate run on an
/// operator's arm64 laptop returned Green about arm64 while naming a digest that also covers the
/// amd64 the fleet serves. Nothing in the output distinguished that from a real pass. Measured
/// 2026-08-27 against <c>mw-plugin-test:main</c>: list digest <c>sha256:4a63eda…</c>, amd64
/// manifest <c>sha256:ab6efc31…</c>, arm64 manifest <c>sha256:e353c397…</c> — one verdict, one
/// digest, two different sets of bytes.</para>
///
/// <para>The argv is where that is decided, it is pure, and it needs no daemon — so it is asserted
/// here rather than in a docker-shaped integration test CI would have to skip, and a skipped gate
/// renders the same green tick as a passing one.</para>
/// </summary>
public class DockerImageGateArgsTest
{
    private const string Image = "meshweaver.azurecr.io/mw-plugin-test:3.0.0-rc8.ci.6015";
    private const string Root = "/tmp/combo-work";

    /// <summary>The fleet's architecture is what a verdict is about when nothing says otherwise:
    /// AKS node pools are x86_64, and CD's arm64 bake leg is opt-in behind BAKE_ARM64.</summary>
    [Fact]
    public void FleetPlatform_IsAmd64() =>
        Assert.Equal("linux/amd64", DockerImageGate.FleetPlatform);

    /// <summary>🚨 THE REGRESSION GUARD. Drop <c>--platform</c> from either call and the gate
    /// verifies whichever architecture the host happens to be, while reporting a verdict about the
    /// image as a whole.</summary>
    [Theory]
    [InlineData("linux/amd64")]
    [InlineData("linux/arm64")]
    public void EveryDockerCall_PinsThePlatform(string platform)
    {
        AssertAdjacent(DockerImageGate.PullArgs(Image, platform), "--platform", platform);
        AssertAdjacent(DockerImageGate.RunArgs(Image, Root, platform), "--platform", platform);
    }

    /// <summary>
    /// 🚨 Everything before the image reference is DOCKER's; everything after it is the TESTER's.
    /// A flag that drifts past the image is not a flag any more — docker never sees it and
    /// mw-plugin-test receives it as an unknown argument, so the run silently stops being pinned
    /// while still looking correct. This asserts the boundary, not merely the presence.
    /// </summary>
    [Fact]
    public void RunArgs_PutEveryDockerFlagBeforeTheImage_AndTheTestersAfterIt()
    {
        var args = DockerImageGate.RunArgs(Image, Root, DockerImageGate.FleetPlatform);
        var image = Array.IndexOf(args, Image);
        Assert.True(image > 0, "the image reference must appear in the argv");

        var docker = args[..image];
        Assert.Contains("--rm", docker);
        // --init reaps the tester's children; every other invocation of this image in the repo
        // passes it, and a gate that runs the image differently from CI is not measuring CI.
        Assert.Contains("--init", docker);
        AssertAdjacent(docker, "--platform", DockerImageGate.FleetPlatform);
        AssertAdjacent(docker, "-v", $"{Root}:/work");
        AssertAdjacent(docker, "--entrypoint", "/app/mw-plugin-test");

        // The tester's own arguments — the repo root it gates, and where to write the structured
        // report the verifier folds. A report path outside the mount lands inside the container and
        // is lost, which reads as "the tester produced no report" → NotVerifiable forever, with no
        // error anywhere to say why.
        Assert.Equal(
            new[] { "/work", "--report", $"/work/{GateRunReport.FileName}" },
            args[(image + 1)..]);
    }

    /// <summary>The pull names the image and nothing else — a pull carrying the tester's arguments
    /// would fail as a registry error four steps from its cause.</summary>
    [Fact]
    public void PullArgs_AreJustPullPlatformImage() =>
        Assert.Equal(
            new[] { "pull", "--platform", DockerImageGate.FleetPlatform, Image },
            DockerImageGate.PullArgs(Image, DockerImageGate.FleetPlatform));

    /// <summary><paramref name="value"/> must FOLLOW <paramref name="flag"/> — a flag whose value
    /// is merely present somewhere else in the argv is a different command line.</summary>
    private static void AssertAdjacent(string[] args, string flag, string value)
    {
        var at = Array.IndexOf(args, flag);
        Assert.True(at >= 0, $"'{flag}' is missing from: {string.Join(' ', args)}");
        Assert.True(at + 1 < args.Length, $"'{flag}' has no value in: {string.Join(' ', args)}");
        Assert.Equal(value, args[at + 1]);
    }
}
