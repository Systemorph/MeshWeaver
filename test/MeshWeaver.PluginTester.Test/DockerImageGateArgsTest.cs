#pragma warning disable CS1591

using System;
using System.IO;
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
    // The candidate PORTAL image: the gate runs as the portal host since #3022, and the combo gate
    // since #3848 — the portal image ships no tester, and the tester image is a subset of it.
    private const string Image = "meshweaver.azurecr.io/memex-portal-ai:3.1.10873";
    private const string Root = "/tmp/combo-work";
    private const string Host = "/tmp/combo-host";

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
        AssertAdjacent(DockerImageGate.RunArgs(Image, Root, Host, platform), "--platform", platform);
    }

    /// <summary>
    /// 🚨 Everything before the image reference is DOCKER's; everything after it is what the
    /// portal's <c>dotnet</c> starts and the TESTER's arguments. A flag that drifts past the image is
    /// not a flag any more — docker never sees it, so the run silently stops being pinned while
    /// still looking correct. This asserts the boundary, not merely the presence.
    /// </summary>
    [Fact]
    public void RunArgs_PutEveryDockerFlagBeforeTheImage_AndTheTestersAfterIt()
    {
        var args = DockerImageGate.RunArgs(Image, Root, Host, DockerImageGate.FleetPlatform);
        var image = Array.IndexOf(args, Image);
        Assert.True(image > 0, "the image reference must appear in the argv");

        var docker = args[..image];
        Assert.Contains("--rm", docker);
        // --init reaps the tester's children and lets a crashed runtime's SIGABRT end the process;
        // every lane passes it, and a gate that runs the image differently from CI is not measuring CI.
        Assert.Contains("--init", docker);
        AssertAdjacent(docker, "--platform", DockerImageGate.FleetPlatform);
        // Both mounts, each as a `-v <value>` pair: the composed host read-only, the work root writable.
        var mounts = Enumerable.Range(0, docker.Length - 1)
            .Where(i => docker[i] == "-v").Select(i => docker[i + 1]).ToArray();
        Assert.Equal(new[] { $"{Host}:/host:ro", $"{Root}:/work" }, mounts);

        // The portal image's OWN dotnet starts the composed host's tester CLI, as the portal (--app
        // /app); then the repo root it gates and where to write the structured report the verifier
        // folds. A report path outside the mount lands inside the container and is lost, which reads
        // as "the tester produced no report" → NotVerifiable forever, with no error to say why.
        AssertAdjacent(docker, "--entrypoint", "dotnet");
        Assert.Equal(
            new[]
            {
                "/host/mw-plugin-test.dll", "/work", "--app", "/app",
                "--report", $"/work/{GateRunReport.FileName}",
            },
            args[(image + 1)..]);
    }

    /// <summary>
    /// 🚨 #3848, measured on run 38688576426: <c>--entrypoint /app/mw-plugin-test</c> inside the
    /// candidate PORTAL image — which ships no tester — exited 127 (<c>[FATAL tini (7)] exec
    /// /app/mw-plugin-test failed: No such file or directory</c>) on every slot, and each run was
    /// landed as a NotVerifiable verdict about the candidate. The gate must never again start a
    /// tester path the image it runs does not carry.
    /// </summary>
    [Fact]
    public void RunArgs_NeverStartTheTesterFromThePortalImagesOwnApp() =>
        Assert.DoesNotContain(
            "/app/mw-plugin-test",
            DockerImageGate.RunArgs(Image, Root, Host, DockerImageGate.FleetPlatform));

    /// <summary>
    /// The instrument probe names EVERY file a composed host lacks, and passes a complete one. Its
    /// negative case is the #3848 shape: the portal's /app on its own, carrying the manifest but no
    /// tester.
    /// </summary>
    [Fact]
    public void MissingHostFiles_NamesEachMissingFile_AndPassesACompleteHost()
    {
        var dir = Directory.CreateTempSubdirectory("combo-host-probe-").FullName;
        try
        {
            Assert.Equal(DockerImageGate.RequiredHostFiles, DockerImageGate.MissingHostFiles(dir));

            // The portal's /app alone: a manifest, no tester.
            File.WriteAllText(Path.Combine(dir, "meshweaver-surface.manifest"), "x");
            Assert.Equal(
                new[] { "mw-plugin-test.dll", "mw-plugin-test.runtimeconfig.json" },
                DockerImageGate.MissingHostFiles(dir));

            File.WriteAllText(Path.Combine(dir, "mw-plugin-test.dll"), "x");
            File.WriteAllText(Path.Combine(dir, "mw-plugin-test.runtimeconfig.json"), "{}");
            Assert.Empty(DockerImageGate.MissingHostFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A missing instrument has its OWN exit code — not Green (0), not Red/NotVerifiable
    /// (1), not usage (2) — because the lander keys on it to refuse landing a verdict.</summary>
    [Fact]
    public void TesterMissing_HasItsOwnExitCode() =>
        Assert.DoesNotContain(DockerImageGate.TesterMissingExitCode, new[] { 0, 1, 2 });

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
