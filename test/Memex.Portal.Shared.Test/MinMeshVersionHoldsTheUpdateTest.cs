#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PackagingManifest = MeshWeaver.Plugin.Packaging.PluginManifest;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>Policy <c>package-min-mesh-version</c> on the COMPILED-MODULE lane — the pure decision.</b>
/// A bundle whose declared floor is comparable with the running platform and strictly above it is
/// NOT landed (<see cref="ModuleUpdateAction.SkipPlatformBelowFloor"/>); the landed generation keeps
/// running. Positive and negative controls side by side, so a decision that held everything, or
/// nothing, fails here.
/// </summary>
public class ModuleFloorHoldDecisionTest
{
    private const string Running = "3.0.0-ci.9412";

    private static string? Gate(string? floor) => ModulePlatformFloor.DeclineReason(floor, Running);

    private static string? Hold(string? floor) => PlatformFloor.HoldReason(floor, Running);

    private static ModuleActivationEntry Landed(string version) => new()
    {
        Name = "MeshWeaver.Store",
        PackagePath = "Plugins/Store",
        Version = version,
        FrameworkMvid = "s-old",
        Enabled = true,
    };

    private static ModuleUpdateVerdict Decide(string version, string? floor, ModuleActivationEntry? landed) =>
        ModuleUpdateDecision.Decide(version, floor, Gate, landed, policyDecline: null, _ => true, "s-new",
            floorHold: Hold);

    /// <summary>The 2026-09-27 shape: Store 1.16 built on a newer platform than the instance runs.</summary>
    [Theory]
    [InlineData("3.0.0-ci.9494")]
    [InlineData("3.0.0-ci.9413")]
    [InlineData("3.1.0")]
    public void AFloorAboveTheRunningPlatform_IsHeld_AndTheLandedVersionIsKept(string floor)
    {
        var upgrade = Decide("1.16.0", floor, Landed("1.15.0"));
        var fresh = Decide("1.16.0", floor, landed: null);

        Assert.Equal(ModuleUpdateAction.SkipPlatformBelowFloor, upgrade.Action);
        Assert.Contains(floor, upgrade.Reason);
        Assert.Contains(Running, upgrade.Reason);
        Assert.Contains("keeping 1.15.0", upgrade.Reason);
        Assert.Equal(ModuleUpdateAction.SkipPlatformBelowFloor, fresh.Action);
    }

    /// <summary>Floors at or below the running platform, and every floor that cannot be ordered
    /// against it, LAND — the negative controls, including the 2026-09-07 pair.</summary>
    [Theory]
    [InlineData("3.0.0-ci.9412")]
    [InlineData("3.0.0-ci.7845")]
    [InlineData("1.0.0")]
    [InlineData("3.0.0")]
    [InlineData("3.0.0-rc8")]
    [InlineData("not-a-version")]
    [InlineData(null)]
    public void AFloorAtOrBelow_OrUnorderable_Lands(string? floor)
        => Assert.Equal(ModuleUpdateAction.Land, Decide("1.16.0", floor, Landed("1.15.0")).Action);

    /// <summary>A LOCAL source build (<c>-ci.0</c>) is never held, whatever the floor.</summary>
    [Fact]
    public void ALocalSourceBuild_IsNeverHeld()
        => Assert.Equal(ModuleUpdateAction.Land, ModuleUpdateDecision.Decide(
            "1.16.0", "3.0.0-ci.9494", Gate, Landed("1.15.0"), null, _ => true, "s-new",
            floorHold: f => PlatformFloor.HoldReason(f, "3.0.0-ci.0")).Action);

    /// <summary>The floor holds a LAND only — every more specific skip keeps its own name.</summary>
    [Fact]
    public void TheFloor_NeverMasksAMoreSpecificSkip()
    {
        const string above = "3.0.0-ci.9494";
        Assert.Equal(ModuleUpdateAction.SkipUpToDate, ModuleUpdateDecision.Decide(
            "1.15.0", above, Gate, Landed("1.15.0") with { FrameworkMvid = "s-new" }, null, _ => true, "s-new",
            floorHold: Hold).Action);
        Assert.Equal(ModuleUpdateAction.SkipOlder, Decide("1.14.0", above, Landed("1.15.0")).Action);
        Assert.Equal(ModuleUpdateAction.SkipPolicy, ModuleUpdateDecision.Decide(
            "1.16.0", above, Gate, Landed("1.15.0"), "policy Notify", _ => true, "s-new", floorHold: Hold).Action);
    }
}

/// <summary>
/// 🚨 <b>Policy <c>package-min-mesh-version</c> through the REAL compiled-module path</b> —
/// <see cref="PluginBundleClient.AdoptModule"/>: the registry's index advertises a bundle with a
/// declared floor; above the running platform NOTHING is downloaded and the landed generation stays;
/// satisfied, the bundle lands. The witness is the fake registry's download log and the landing
/// service's own activation sidecar.
/// </summary>
public class ModuleBundleFloorHoldTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RegistryHost = "registry.floor-hold.test";
    private const string RegistryUrl = "https://" + RegistryHost;
    private const string Token = "mwi_floor_hold_consumer";
    private const string Plugin = "FloorHoldModulePkg";
    private const string Module = "MeshWeaver.FloorHoldModule";
    private const string Framework = "s1234567890abcdef1234567890abcdef";

    private readonly string landingRoot =
        Path.Combine(Path.GetTempPath(), "mw-floor-hold-" + Guid.NewGuid().ToString("N"));

    private readonly FloorRegistry registry = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(landingRoot);
        return base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IHttpClientFactory>(new HostRoutingClientFactory(registry))
                .AddSingleton(FloorFixture.Pin)
                .AddSingleton(new ModuleLandingService(baseDirectory: landingRoot)));
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(landingRoot))
                Directory.Delete(landingRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>A fresh client per step: the index is cached per client instance.</summary>
    private PluginBundleClient Client() => new(Mesh, RegistryUrl, Token);

    [Fact(Timeout = 180_000)]
    public async Task ABundleAboveTheFloor_IsNotDownloaded_TheLandedGenerationStays_AndSatisfiedItLands()
    {
        var ct = TestContext.Current.CancellationToken;
        FloorFixture.AssertTheFloorHolds(Mesh);

        // ── 1. v1.1.0, no floor: lands (the installed version that must keep serving).
        registry.Serve("1.1.0", floor: null);
        (await Client().AdoptModule(Plugin, Module, "Plugins/" + Plugin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be(1);
        var v1 = ModuleActivationSidecar.Read(landingRoot).Entries.Single(e => e.Name == Module);
        v1.Version.Should().Be("1.1.0");
        var v1Dll = Path.Combine(ModuleLandingService.ModuleDirectoryFor(landingRoot, Module, v1), Module + ".dll");
        File.Exists(v1Dll).Should().BeTrue();
        registry.Downloads.Should().Equal("1.1.0");

        // ── 2. v1.2.0 declaring a floor above the running platform: HELD.
        registry.Serve("1.2.0", floor: FloorFixture.Above);
        (await Client().AdoptModule(Plugin, Module, "Plugins/" + Plugin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be(0,
            "a bundle whose floor is above the running platform is not landed (policy package-min-mesh-version)");
        registry.Downloads.Should().Equal(["1.1.0"],
            "the held bundle is not even DOWNLOADED — the index's floor decides before a byte travels");
        var kept = ModuleActivationSidecar.Read(landingRoot).Entries.Single(e => e.Name == Module);
        kept.Version.Should().Be("1.1.0", "R1: the landed generation keeps running");
        File.Exists(v1Dll).Should().BeTrue("and its bytes stay where boot will load them");

        // ── 3. the same version with a satisfied floor: LANDS.
        registry.Serve("1.2.0", floor: FloorFixture.Below);
        (await Client().AdoptModule(Plugin, Module, "Plugins/" + Plugin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be(1);
        ModuleActivationSidecar.Read(landingRoot).Entries.Single(e => e.Name == Module)
            .Version.Should().Be("1.2.0");
        registry.Downloads.Should().Equal("1.1.0", "1.2.0");
    }

    /// <summary>The plugin registry: ONE module bundle at a settable version and floor, and a log of
    /// every download.</summary>
    private sealed class FloorRegistry : HttpMessageHandler, IHostHandler
    {
        private ImmutableList<string> downloads = ImmutableList<string>.Empty;
        private (string Version, string? Floor) served = ("1.0.0", null);

        public ImmutableList<string> Downloads => downloads;

        public void Serve(string version, string? floor) => served = (version, floor);

        public bool Serves(string host) => string.Equals(host, RegistryHost, StringComparison.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var (version, floor) = served;
            if (request.Method == HttpMethod.Get && path == $"{PluginBundleClient.RoutePrefix}/index.json")
            {
                var body = JsonSerializer.Serialize(new
                {
                    frameworkMvid = Framework,
                    bundles = new[]
                    {
                        new
                        {
                            plugin = Plugin,
                            version,
                            url = $"{RegistryUrl}{PluginBundleClient.RoutePrefix}/{Plugin}/{version}",
                            module = Module,
                            minMeshVersion = floor,
                            frameworkMvid = Framework,
                        },
                    },
                }, PluginRegistryPayloads.Json);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Get && path.StartsWith($"{PluginBundleClient.RoutePrefix}/", StringComparison.Ordinal))
            {
                ImmutableInterlocked.Update(ref downloads, d => d.Add(version));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Bundle(version, floor)),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        /// <summary>A packed module bundle carrying REAL assembly bytes (the landing measures them).</summary>
        private static byte[] Bundle(string version, string? floor)
        {
            var manifestJson = JsonSerializer.Serialize(new
            {
                plugin = Plugin,
                version,
                frameworkMvid = Framework,
                module = new
                {
                    assemblyName = Module,
                    assemblies = new[] { Module + ".dll" },
                    minMeshVersion = floor,
                },
            });
            var buffer = new MemoryStream();
            NuGetPackageWriter.Write(
                buffer,
                new PackagingManifest(Plugin, "MeshWeaver.Plugin." + Plugin, version, Plugin, null, []),
                "3.0.0",
                [
                    new NuGetPackageWriter.Entry(
                        NuGetPackageWriter.ModuleEntryPathFor(Module + ".dll"),
                        () => new MemoryStream(File.ReadAllBytes(typeof(BundleReader).Assembly.Location))),
                ],
                manifestJson);
            return buffer.ToArray();
        }
    }
}

/// <summary>
/// 🚨 <b>ONE comparator, ONE running-version reader</b> — the guard behind policy
/// <c>package-min-mesh-version</c>. The floor used to be decided in three places with two
/// comparators and two notions of "running": <c>ModuleUpdateDecision</c> (SemVer, advisory),
/// <c>PrebuiltAdoptionPolicy.FloorDecline</c> (a private SemVer copy — the 2026-09-07 trap, live)
/// and GitSync's <c>ModuleSyncDecision</c> (<c>PlatformCompatibility.ProducerIsNewer</c>), reading
/// the running version from MeshWeaver.Graph's assembly stamp in one lane and from
/// <c>PlatformBuildInfo</c> in the other. This guard fails when a source file under <c>src/</c>
/// decides a declared floor with anything but <see cref="PlatformFloor"/>, or reads a running
/// version with anything but <see cref="PlatformBuildInfo.RunningPlatformVersion"/>.
/// </summary>
public class OneFloorComparatorGuard
{
    /// <summary>The files allowed to compare a floor with SemVer, each with its reason.</summary>
    private static readonly ImmutableDictionary<string, string> SemVerFloorAllowed =
        ImmutableDictionary<string, string>.Empty
            // The WORDING of the advisory lines and the pack-time lint's parity oracle
            // (ModulePlatformFloorScriptParityTest) — decides nothing at runtime.
            .Add("src/MeshWeaver.PluginCatalog/ModulePlatformFloor.cs", "advisory wording + pack-lint parity")
            // THE decision itself: the numeric-core comparison inside PlatformFloor.
            .Add("src/MeshWeaver.Plugin.Packaging/PlatformFloor.cs", "the one floor decision")
            .Add("src/MeshWeaver.Plugin.Packaging/PlatformReleaseOrder.cs", "the one release order");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "MeshWeaver.slnx not found above the test's base directory — the guard cannot see src/, so it "
            + "must fail rather than pass having checked nothing");
    }

    private static ImmutableList<(string Path, string Text)> Sources()
    {
        var root = RepoRoot();
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToImmutableList();
    }

    /// <summary>Code lines only — a doc comment that NAMES the old comparator is not a call.</summary>
    private static string Code(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void NoFloorIsDecidedWithSemVer_OutsideTheOneDecision()
    {
        var sources = Sources();
        sources.Should().NotBeEmpty("the guard must see src/");

        // A file that both names a declared floor and calls the SemVer comparer is a second floor
        // decision in waiting — exactly how PrebuiltAdoptionPolicy.FloorDecline kept the 09-07 trap.
        var offenders = sources
            .Where(s => !SemVerFloorAllowed.ContainsKey(s.Path))
            .Select(s => (s.Path, Code: Code(s.Text)))
            .Where(s => Regex.IsMatch(s.Code, @"\bminMeshVersion\b", RegexOptions.IgnoreCase)
                        && Regex.IsMatch(s.Code, @"NuGetVersionComparer\.Instance\.Compare\([^;]*(?:minMeshVersion|floor)",
                            RegexOptions.IgnoreCase))
            .Select(s => s.Path)
            .ToList();

        offenders.Should().BeEmpty(
            "a declared minMeshVersion is decided by PlatformFloor alone (policy package-min-mesh-version); "
            + "SemVer ranks ci < rc < clean and held every portal on 2026-09-07. Offenders: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheModuleSyncDecline_UsesTheOneDecision()
    {
        var sync = Sources().Single(s => s.Path.EndsWith("MeshWeaver.GitSync/ModuleSyncDecision.cs", StringComparison.Ordinal));
        var code = Code(sync.Text);
        code.Should().Contain("PlatformFloor.Evaluate(");
        code.Should().NotContain("ProducerIsNewer(", "the GitSync decline and the catalog's hold are one decision");
    }

    [Fact]
    public void TheRunningVersion_HasOneReader()
    {
        // Every "running platform version" a floor is compared against comes from
        // PlatformBuildInfo.RunningPlatformVersion; the two lane-local readers delegate to it.
        var sources = Sources();
        var floor = sources.Single(s => s.Path.EndsWith("MeshWeaver.PluginCatalog/ModulePlatformFloor.cs", StringComparison.Ordinal));
        Code(floor.Text).Should().Contain("RunningVersion => PlatformBuildInfo.RunningPlatformVersion");
        Code(floor.Text).Should().NotContain("AssemblyInformationalVersionAttribute",
            "the module lane used to read MeshWeaver.Graph's stamp, which carries no run number");
        var prebuilt = sources.Single(s => s.Path.EndsWith("MeshWeaver.Hosting/PrebuiltAdoptionPolicy.cs", StringComparison.Ordinal));
        Code(prebuilt.Text).Should().Contain("RunningPlatformVersion => PlatformBuildInfo.RunningPlatformVersion");

        // …and the floor gates read it through that one property.
        var gate = sources.Single(s => s.Path.EndsWith("MeshWeaver.PluginCatalog/PackagePlatformFloorGate.cs", StringComparison.Ordinal));
        Code(gate.Text).Should().Contain("PlatformFloor.Evaluate(candidate.MinMeshVersion, RunningVersion(hub))");
        Code(gate.Text).Should().Contain(": PlatformBuildInfo.RunningPlatformVersion;",
            "the catalog's reader falls back to the one reader; the override is a test seam");
        var bundle = sources.Single(s => s.Path.EndsWith("MeshWeaver.PluginCatalog/PluginBundleClient.cs", StringComparison.Ordinal));
        Code(bundle.Text).Should().Contain("floorHold: PackagePlatformFloorGate.HoldFor(_hub)",
            "the compiled-module lane decides through the same gate as the content lane");
    }

    /// <summary>Behavioural parity across the matrix: the GitSync decline and the catalog's hold
    /// answer the same for every (floor, running) pair — including the 2026-09-07 pairs.</summary>
    [Theory]
    [InlineData("3.0.0-ci.9494", "3.0.0-ci.9412")]
    [InlineData("3.0.0-ci.9412", "3.0.0-ci.9412")]
    [InlineData("3.0.0-rc8", "3.0.0-ci.8055")]
    [InlineData("3.0.0", "3.0.0-ci.9412")]
    [InlineData("1.0.0", "3.0.0-ci.9412")]
    [InlineData("3.1.0", "3.0.0-ci.9412")]
    [InlineData("garbage", "3.0.0-ci.9412")]
    [InlineData("3.0.0-ci.9494", "3.0.0-ci.0")]
    [InlineData("3.0.0-ci.9494", null)]
    public void GitSyncAndTheCatalog_AgreeOnEveryPair(string floor, string? running)
    {
        var declined = ModuleSyncDecision.Decide(
                [new ModuleReading("M", "", "hash", floor)], null, running, reconcile: false)
            .Single().Outcome == ModuleSyncOutcomeKind.Declined;
        var held = ModuleUpdateDecision.Decide("2.0.0", floor, _ => null, null, null, _ => true, "s",
            floorHold: f => PlatformFloor.HoldReason(f, running)).Action == ModuleUpdateAction.SkipPlatformBelowFloor;

        declined.Should().Be(PlatformFloor.Evaluate(floor, running).IsHeld);
        held.Should().Be(declined);
    }
}

/// <summary>
/// The platform the end-to-end fixtures run on, and the floors either side of it — the 2026-09-27
/// numbers: instances on <c>3.0.0-ci.9412</c>, packages built on <c>3.0.0-ci.9494</c>. Pinned on the
/// mesh through <see cref="RunningPlatformVersionOverride"/>, because a test process is a local
/// <c>-ci.0</c> build, against which no floor is ordered (and correctly so — see
/// <c>PlatformFloorTest.TheSeptember7Trap_NeverHolds</c>).
/// </summary>
internal static class FloorFixture
{
    /// <summary>The running platform every end-to-end fixture pins.</summary>
    public const string Running = "3.0.0-ci.9412";

    /// <summary>A floor above <see cref="Running"/> — the build the 09-27 packages were made on.</summary>
    public const string Above = "3.0.0-ci.9494";

    /// <summary>A floor below <see cref="Running"/>.</summary>
    public const string Below = "3.0.0-ci.7845";

    /// <summary>The seam registration that pins <see cref="Running"/> on a test mesh.</summary>
    public static RunningPlatformVersionOverride Pin => new(Running);

    /// <summary>
    /// 🚨 The precondition every end-to-end hold test rests on, ASSERTED against the mesh's own
    /// reading: the pinned version must be what the catalog reads, and the two floors must fall on
    /// either side of it, or the "held" arm would pass (or fail) for the wrong reason.
    /// </summary>
    public static void AssertTheFloorHolds(MeshWeaver.Messaging.IMessageHub mesh)
    {
        var running = PackagePlatformFloorGate.RunningVersion(mesh);
        running.Should().Be(Running, "the fixture pins the running platform on the mesh");
        PlatformFloor.Evaluate(Above, running).IsHeld.Should().BeTrue();
        PlatformFloor.Evaluate(Below, running).IsHeld.Should().BeFalse();
    }
}
