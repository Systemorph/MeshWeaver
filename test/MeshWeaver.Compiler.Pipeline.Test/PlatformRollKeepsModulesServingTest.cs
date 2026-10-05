#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Rows B1, B2 and B4 of the module update ladder</b> (<c>Doc/Architecture/ModuleUpdateLadder</c>,
/// policy <c>platform-backwards-compatibility</c>): a platform roll keeps the module bytes an instance
/// already landed. Nothing is rebuilt or re-sealed, and a module built on an older platform of the
/// same key loads on a newer one.
///
/// <para>A platform roll is a new process booting on the SAME module volume. These tests therefore land
/// real bundles through <see cref="ModuleLandingService"/> once, then boot the same volume repeatedly
/// through the production boot computation
/// (<see cref="ModuleActivationBoot.ComputeEffectiveModuleEntriesAgainstImage"/>). Each boot uses a
/// different running platform and production's own floor wording
/// (<see cref="ModulePlatformFloor.DeclineReason(string?, string?)"/>).</para>
///
/// <para><b>What makes these able to fail.</b> Each test records every skip and advisory the boot
/// reports, so "keeps serving" means the generation is unchanged AND nothing was said against it. The
/// in-suite negative control (<see cref="AModuleWhoseBytesAreGone_IsSkippedByName_TheChannelIsLive"/>)
/// proves the skip channel reports a module that really cannot be served, so an empty skip list is a
/// measurement, not an unwired callback.</para>
/// </summary>
public class PlatformRollKeepsModulesServingTest : IDisposable
{
    private const string Module = "Acme.Ladder";
    private const string PackagePath = "Plugins/AcmeLadder";

    // Three builds of ONE compatibility key, ordered by their run ordinal.
    private const string P1 = "3.0.0-ci.9400";
    private const string P2 = "3.0.0-ci.9410";
    private const string P3 = "3.0.0-ci.9420";

    private const string SurfaceIdentity = "s4b2836629f0c58ad4ed0aa683190981a";
    private const string PlatformCommit = "gafde4eabe0740ff658f3dae8a3073c33a2b3ea02";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-ladder-roll-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleLandingService landing;

    public PlatformRollKeepsModulesServingTest()
    {
        Directory.CreateDirectory(root);
        landing = new ModuleLandingService(baseDirectory: root);
    }

    public void Dispose()
    {
        landing.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is harmless */ }
        catch (UnauthorizedAccessException) { /* likewise */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// B1 — P1 → P2 → P3 with the module unchanged: every boot serves the SAME landed generation and
    /// says nothing against it, so no rebuild, re-seal or re-land is needed.
    /// </summary>
    [Fact]
    public async Task ALandedModule_KeepsServingAcrossTwoPlatformRolls()
    {
        await Land("1.0.0", floor: P1);

        var onP1 = Boot(P1);
        onP1.Served.Should().NotBeNull("the premise: the module serves on the platform that produced it");
        var generation = onP1.Served!.Directory;

        foreach (var running in new[] { P2, P3 })
        {
            var boot = Boot(running);
            boot.Skips.Should().BeEmpty($"a roll to {running} keeps the landed module");
            boot.Advisories.Should().BeEmpty($"its floor {P1} is below {running}");
            boot.Served.Should().NotBeNull();
            boot.Served!.Version.Should().Be("1.0.0");
            boot.Served.Directory.Should().Be(generation, "the SAME bytes keep serving — nothing re-landed");
        }
    }

    /// <summary>
    /// B2 — a module built on an OLDER platform of the same key, landed after the instance moved on,
    /// loads on the newer platform with no advisory.
    /// </summary>
    [Fact]
    public async Task AModuleBuiltOnAnOlderPlatform_LoadsOnANewerOne_WithNoAdvisory()
    {
        await Land("1.2.0", floor: P1);

        var boot = Boot(P3);
        boot.Skips.Should().BeEmpty();
        boot.Advisories.Should().BeEmpty($"floor {P1} ≤ running {P3}");
        boot.Served!.Version.Should().Be("1.2.0");
        ModulePlatformFloor.DeclineReason(P1, P3).Should().BeNull("the one floor rule agrees");
    }

    /// <summary>
    /// B4 — a landed module whose floor is ABOVE the booting platform (for example an older replica
    /// in a mixed roll) is NOT skipped at boot (#3648). The floor is an advisory that names both
    /// versions, and the link probe decides. The holds that keep such a bundle off an instance happen
    /// at ADOPTION, before anything lands (row B3).
    /// </summary>
    [Fact]
    public async Task AFloorAboveTheBootingPlatform_IsAnAdvisory_NeverASkip()
    {
        await Land("1.3.0", floor: P3);

        var boot = Boot(P2);
        boot.Skips.Should().BeEmpty("the boot never skips on the floor string (#3648)");
        boot.Served!.Version.Should().Be("1.3.0");
        var (module, sentence) = Assert.Single(boot.Advisories);
        module.Should().Be(Module);
        sentence.Should().Contain(P3).And.Contain(P2);

        // The control for this row: on the platform the floor names, the advisory is gone.
        Boot(P3).Advisories.Should().BeEmpty();
    }

    /// <summary>
    /// The negative control: a module whose landed bytes are gone is SKIPPED, by name. This proves the
    /// skip channel the rows above read as empty is wired and does report.
    /// </summary>
    [Fact]
    public async Task AModuleWhoseBytesAreGone_IsSkippedByName_TheChannelIsLive()
    {
        await Land("1.0.0", floor: P1);
        var served = Boot(P1).Served!;
        Directory.Delete(Path.Combine(root, "modules", served.Directory ?? Module), recursive: true);

        var boot = Boot(P2);
        boot.Served.Should().BeNull();
        Assert.Single(boot.Skips).Module.Should().Be(Module);
    }

    // ───────────────────────────────────────────────────────────────────────── harness

    private sealed record BootReading(
        ModuleActivationEntry? Served,
        ImmutableList<(string Module, string Reason)> Skips,
        ImmutableList<(string Module, string Sentence)> Advisories);

    /// <summary>One process booting the shared module volume on <paramref name="running"/>.</summary>
    private BootReading Boot(string running)
    {
        var skips = ImmutableList<(string, string)>.Empty;
        var advisories = ImmutableList<(string, string)>.Empty;
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntriesAgainstImage(
            baselineEntries: [],
            ModuleActivationSidecar.Read(root),
            floor => ModulePlatformFloor.DeclineReason(floor, running),
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry),
            (m, r) => skips = skips.Add((m, r)),
            (m, s) => advisories = advisories.Add((m, s)),
            [SurfaceIdentity, PlatformCommit],
            imageSeedOf: null);
        ModuleActivationEntry? served = null;
        foreach (var module in effective)
            if (module.Landed is { } landed && landed.Name == Module)
                served = landed;
        return new BootReading(served, skips, advisories);
    }

    private async Task Land(string version, string floor) =>
        await landing.LandModule(
                Module, [(Module + ".dll", RealAssemblyBytes)],
                frameworkMvid: PlatformCommit, packagePath: PackagePath, version: version,
                minMeshVersion: floor)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);
}
