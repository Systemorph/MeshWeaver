using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using MeshWeaver.ComboVerifier;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// Pins the combo gate's SPLIT shape (MeshWeaver#4413): <see cref="DockerImageGate"/> bakes the
/// combo mesh-free with <c>mw-plugin-test compile</c>, then runs the mesh gate CONSUMING that bake
/// with <c>--seed</c>, inside ONE budget.
///
/// <para><b>The defect.</b> The combo ran the FUSED shape: one <c>mw-plugin-test /work</c> with no
/// seed. The gate host then registered no prebuilt-assembly consumer and the mesh compiled every
/// NodeType itself. On the slots carrying <c>Hosting</c> (63 types over one shared source network)
/// the in-mesh drain took 15 of the 25 minutes on a GREEN run, and on the red runs a type the drain
/// reached more than 300 s after the gate started waiting on it read as
/// <c>compile-no-verdict</c>; two of those stacked and the run was killed at its budget (14 killed
/// and 10 no-verdict slots over 2026-11-08..10). The producing repository's own lane bakes first and
/// seeds the gate; the combo now does the same.</para>
///
/// <para>The docker CLI is replaced by <see cref="DockerImageGate.DockerOverride"/>, so the ORDER,
/// the seed and the budget arithmetic are asserted without a daemon. Each test fails on the fused
/// shape: it makes one docker run, never a <c>compile</c>, and never passes <c>--seed</c>.</para>
/// </summary>
public sealed class ComboGateBakesThenSeedsTest : IDisposable
{
    private const string Image = "meshweaver.azurecr.io/memex-portal-ai:3.1.12943";
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(25);

    private readonly string scratch = Directory.CreateTempSubdirectory("combo-split-").FullName;
    private readonly IoPoolRegistry pools = new();

    private string WorkRoot => Path.Combine(scratch, "slot-0");
    private string HostDir => Path.Combine(scratch, "host");

    /// <summary>Creates an empty work root and gate host for one case.</summary>
    public ComboGateBakesThenSeedsTest()
    {
        Directory.CreateDirectory(WorkRoot);
        Directory.CreateDirectory(HostDir);
    }

    /// <summary>Releases the pools and deletes the case's scratch tree.</summary>
    public void Dispose()
    {
        pools.Dispose();
        Directory.Delete(scratch, recursive: true);
    }

    /// <summary>🚨 THE REGRESSION GUARD: a compile runs first, writes into the run's own bake
    /// directory, and the gate that follows mounts that directory and is told to consume it.</summary>
    [Fact]
    public void TheGate_ConsumesAMeshFreeBakeOfTheSameTree()
    {
        var docker = new FakeDocker(onCompile: bake => SealBake(bake, withBundle: true));
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Null(run.Error);
        Assert.Equal(2, docker.Containers.Count);
        var (compile, gate) = (docker.Containers[0].Args, docker.Containers[1].Args);

        var bakeRoot = DockerImageGate.BakeRootFor(WorkRoot);
        Assert.Equal(
            new[]
            {
                "/host/mw-plugin-test.dll", "compile", "/work", "--output", "/bake",
                "--app", "/app", "--shared-frameworks", "/usr/share/dotnet/shared",
            },
            AfterImage(compile));
        Assert.Contains($"{bakeRoot}:/bake", Mounts(compile));

        Assert.Contains($"{bakeRoot}:/seed:ro", Mounts(gate));
        var tester = AfterImage(gate);
        Assert.Equal("--seed", tester[^2]);
        Assert.Equal("/seed", tester[^1]);
        // The bake is beside the work root, never a tree inside it the gate would also discover.
        Assert.False(bakeRoot.StartsWith(Path.GetFullPath(WorkRoot) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
    }

    /// <summary>The split shares the run's ONE budget: the gate gets what the bake left, so
    /// splitting the run cannot extend the time a slot may take.</summary>
    [Fact]
    public void TheGate_GetsWhatTheBakeLeftOfTheBudget()
    {
        var clock = new SteppedClock();
        var docker = new FakeDocker(onCompile: bake =>
        {
            SealBake(bake, withBundle: true);
            clock.Advance(TimeSpan.FromMinutes(10));
        });
        Gate(docker, clock).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Equal(Budget, docker.Containers[0].Budget);
        Assert.Equal(TimeSpan.FromMinutes(15), docker.Containers[1].Budget);
    }

    /// <summary>A bake that used the whole budget leaves no gate to run, and says so.</summary>
    [Fact]
    public void ABakeThatSpentTheBudget_StartsNoGate()
    {
        var clock = new SteppedClock();
        var docker = new FakeDocker(onCompile: bake =>
        {
            SealBake(bake, withBundle: true);
            clock.Advance(Budget);
        });
        var run = Gate(docker, clock).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Single(docker.Containers);
        Assert.Null(run.ExitCode);
        Assert.Contains("the gate never started", run.Error);
    }

    /// <summary>🚨 A bake that died is NotVerifiable — never answered by falling back to the fused
    /// shape, which would bring back the in-mesh compile of everything.</summary>
    [Fact]
    public void ABakeThatSealedNothing_IsAnErrorAndStartsNoGate()
    {
        var docker = new FakeDocker(onCompile: _ => { }, compileExit: 70);
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Single(docker.Containers);
        Assert.Null(run.ExitCode);
        Assert.Contains("sealed no framework identity", run.Error);
        Assert.Contains("exit 70", run.Error);
    }

    /// <summary>A killed bake is reported as the bake, and no gate runs after it.</summary>
    [Fact]
    public void ABakeKilledOnItsBudget_StartsNoGate()
    {
        var docker = new FakeDocker(onCompile: _ => { }, compileTimesOut: true);
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Single(docker.Containers);
        Assert.Contains("the mesh-free bake exceeded", run.Error);
    }

    /// <summary>A sealed bake with no bundle compiled no NodeType: <c>--seed</c> refuses an empty
    /// bake, so the gate runs without one, and there is nothing it could have adopted.</summary>
    [Fact]
    public void ASealedBakeWithNoBundle_GatesWithoutASeed()
    {
        var docker = new FakeDocker(onCompile: bake => SealBake(bake, withBundle: false));
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Null(run.Error);
        Assert.Equal(2, docker.Containers.Count);
        Assert.DoesNotContain("--seed", docker.Containers[1].Args);
        Assert.DoesNotContain(Mounts(docker.Containers[1].Args), m => m.EndsWith(":/seed:ro"));
    }

    /// <summary>A bake left behind by an earlier run is never this run's seed.</summary>
    [Fact]
    public void AStaleBake_IsClearedBeforeTheRun()
    {
        var bakeRoot = DockerImageGate.BakeRootFor(WorkRoot);
        Directory.CreateDirectory(bakeRoot);
        SealBake(bakeRoot, withBundle: true);

        var docker = new FakeDocker(onCompile: _ => { }, compileExit: 70);
        var run = Gate(docker).Execute(Image, WorkRoot, CancellationToken.None);

        Assert.Contains("sealed no framework identity", run.Error);
        Assert.Single(docker.Containers);
    }

    /// <summary>The three bake readings: no identity (died), identity without a bundle (nothing
    /// compiled), identity with a bundle (a seed).</summary>
    [Fact]
    public void ReadBake_DistinguishesDiedEmptyAndSeeded()
    {
        var dir = Path.Combine(scratch, "read-bake");
        Directory.CreateDirectory(dir);
        Assert.Equal(DockerImageGate.BakeReading.Unsealed, DockerImageGate.ReadBake(dir));
        File.WriteAllText(Path.Combine(dir, DockerImageGate.FrameworkMvidFile), "  \n");
        Assert.Equal(DockerImageGate.BakeReading.Unsealed, DockerImageGate.ReadBake(dir));
        SealBake(dir, withBundle: false);
        Assert.Equal(DockerImageGate.BakeReading.NoBundles, DockerImageGate.ReadBake(dir));
        SealBake(dir, withBundle: true);
        Assert.Equal(DockerImageGate.BakeReading.Bundles, DockerImageGate.ReadBake(dir));
    }

    /// <summary>A bake root the run never created is neither deleted (no throw) nor named as kept,
    /// on both settle paths.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Settle_IgnoresABakeRootTheRunNeverCreated(int exitCode)
    {
        var bakeRoot = DockerImageGate.BakeRootFor(WorkRoot);
        Assert.False(Directory.Exists(bakeRoot));
        var output = new StringWriter();

        ComboWorkRoots.Settle(exitCode, WorkRoot, null, output, bakeRoot);

        Assert.DoesNotContain(bakeRoot, output.ToString());
        Assert.Equal(exitCode != 0, Directory.Exists(WorkRoot));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private DockerImageGate Gate(FakeDocker docker, TimeProvider? clock = null) =>
        new(pools, Budget, TextWriter.Null, DockerImageGate.FleetPlatform, HostDir)
        {
            DockerOverride = docker.Call,
            Clock = clock ?? TimeProvider.System,
        };

    private static void SealBake(string bake, bool withBundle)
    {
        File.WriteAllText(Path.Combine(bake, DockerImageGate.FrameworkMvidFile), "c003e001\n");
        if (withBundle)
            File.WriteAllText(Path.Combine(bake, "Hosting.zip"), "bundle");
    }

    private static string[] AfterImage(IReadOnlyList<string> args) =>
        [.. args.SkipWhile(a => a != Image).Skip(1)];

    private static string[] Mounts(IReadOnlyList<string> args) =>
        [.. Enumerable.Range(0, args.Count - 1).Where(i => args[i] == "-v").Select(i => args[i + 1])];

    /// <summary>Answers the inspect calls as "present for the fleet platform" and records every
    /// <c>docker run</c>; the <c>compile</c> container runs <paramref name="onCompile"/> against the
    /// host side of its <c>/bake</c> mount, as the real container writes there.</summary>
    private sealed class FakeDocker(
        Action<string> onCompile, int compileExit = 0, bool compileTimesOut = false)
    {
        public List<(ImmutableArray<string> Args, TimeSpan Budget)> Containers { get; } = [];

        public DockerImageGate.DockerResult Call(
            IReadOnlyList<string> args, CancellationToken ct, TimeSpan budget)
        {
            if (args[0] == "image")
                return new DockerImageGate.DockerResult(0,
                    args.Contains("{{.Os}}/{{.Architecture}}") ? DockerImageGate.FleetPlatform : "sha256:x",
                    TimedOut: false);
            if (args[0] != "run")
                throw new InvalidOperationException($"unexpected docker call: {string.Join(' ', args)}");

            Containers.Add(([.. args], budget));
            if (!args.Contains("compile"))
                return new DockerImageGate.DockerResult(0, "gate ran", TimedOut: false);
            if (compileTimesOut)
                return new DockerImageGate.DockerResult(-1, "", TimedOut: true);
            onCompile(Mounts(args).Single(m => m.EndsWith(":/bake", StringComparison.Ordinal))[..^":/bake".Length]);
            return new DockerImageGate.DockerResult(compileExit, "baked", TimedOut: false);
        }
    }

    /// <summary>A clock that moves only when told.</summary>
    private sealed class SteppedClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }
}
