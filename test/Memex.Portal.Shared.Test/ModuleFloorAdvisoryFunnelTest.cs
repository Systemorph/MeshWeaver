using System;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PackagingManifest = MeshWeaver.Plugin.Packaging.PluginManifest;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The consumer's land half — <c>PluginBundleClient.LandFromBundle</c> — no longer DECLINES
/// a bundle whose declared floor exceeds this platform (#3648).</b>
///
/// <para>This was the second of the three places the floor refused on the module lane, and the
/// one whose log line named the 2026-09-07 outage: "Module bundle for {Plugin} DECLINED: the
/// module requires platform 3.0.0-rc8 or newer but this deployment runs 3.0.0-ci.8055 — nothing
/// landed". The archive's manifest still carries the floor; the client now LOGS it (Information,
/// naming both versions) and hands the bytes to the landing, whose link probe decides. The
/// name-drift refusal — a bundle declaring a different module than the package — is untouched.</para>
///
/// <para>Driven through a REAL monolith mesh because the client resolves its landing service, I/O
/// pool and logger off the hub, and the pin is about what reaches the disk: the Plugins-repo
/// counterpart (<c>ModuleFunnelTest.ABundleWhoseFloorExceedsThisPlatform_IsRefused_NothingReachesDisk</c>)
/// asserted the opposite and flips with the platform pin.</para>
/// </summary>
public class ModuleFloorAdvisoryFunnelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Module = "MeshWeaver.Social";
    private const string Plugin = "SocialMedia";

    private readonly string landingRoot =
        Path.Combine(Path.GetTempPath(), "mw-flooradvisory-funnel-" + Guid.NewGuid().ToString("N"));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(landingRoot);
        return ConfigureMeshBase(builder)
            .AddPluginCatalog()
            // Land into a per-test temp tree, never the test host's own bin folder — the sidecar
            // is a persistent file, and writing it beside the testhost would bleed across tests.
            .ConfigureServices(services =>
                services.AddSingleton(new ModuleLandingService(baseDirectory: landingRoot)));
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
            // Best-effort: a cleanup hiccup must never turn a green test red.
        }
    }

    /// <summary>
    /// A bundle whose manifest declares a floor no build of this line satisfies LANDS: one file on
    /// disk, the entry recorded with the floor (for the index and the status row to SAY), the
    /// restart raised. Before #3648 this returned 0 and wrote nothing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABundleWhoseFloorExceedsThisPlatform_IsLanded_WhenItLinks()
    {
        // The precondition that makes this able to fail: the floor ranks above the platform this
        // test process runs, so the old client would have declined it.
        const string floor = "999.0.0";
        Assert.NotNull(ModulePlatformFloor.DeclineReason(floor));

        var client = new PluginBundleClient(Mesh, "http://registry.invalid");

        var landed = await client
            .LandFromBundle(Plugin, Module, "Plugins/" + Plugin, "1.2.0",
                ModuleBundle(minMeshVersion: floor), advertisedFrameworkMvid: "s-advertised")
            .FirstAsync().Await();

        Assert.Equal(1, landed);
        var list = ModuleActivationSidecar.Read(landingRoot);
        var entry = Assert.Single(list.Entries);
        Assert.Equal(Module, entry.Name);
        Assert.Equal("1.2.0", entry.Version);
        Assert.Equal(floor, entry.MinMeshVersion);
        Assert.Equal("s-advertised", entry.FrameworkMvid);
        Assert.True(entry.Enabled);
        Assert.True(list.PendingRestart, "restart-as-activation — nothing loads into the running process");
        Assert.True(File.Exists(Path.Combine(
            ModuleLandingService.ModuleDirectoryFor(landingRoot, Module, entry), Module + ".dll")));
    }

    /// <summary>The refusal that stays: a bundle declaring a module other than the one the
    /// package declares lands nothing — the floor change removed one gate, not this one.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ABundleDeclaringAnotherModule_IsStillRefused()
    {
        var client = new PluginBundleClient(Mesh, "http://registry.invalid");

        var landed = await client
            .LandFromBundle(Plugin, "MeshWeaver.SomethingElse", "Plugins/" + Plugin, "1.2.0",
                ModuleBundle(minMeshVersion: null), advertisedFrameworkMvid: "s-advertised")
            .FirstAsync().Await();

        Assert.Equal(0, landed);
        Assert.Empty(ModuleActivationSidecar.Read(landingRoot).Entries);
    }

    /// <summary>A packed module bundle the client can read, declaring the given floor, carrying
    /// REAL assembly bytes — the landing measures them (#3538), so a byte stand-in would be refused
    /// as unreadable and the test would be about that gate instead of the floor.</summary>
    private static byte[] ModuleBundle(string? minMeshVersion)
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = Plugin,
            version = "1.2.0",
            frameworkMvid = "s-requested-lane",
            module = new
            {
                assemblyName = Module,
                assemblies = new[] { Module + ".dll" },
                minMeshVersion,
            },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(
            buffer,
            new PackagingManifest(Plugin, "MeshWeaver.Plugin." + Plugin, "1.2.0", Plugin, null, []),
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
