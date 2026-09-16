using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 #4026, Copilot's review of #4427: an UNINSTALL and a LANDING of one module on two replicas at
/// once. Both end by writing the per-module file (<c>activation.d/&lt;Name&gt;.json</c>) that images
/// predating the landing records read — and a write of that file is a projection DERIVED earlier,
/// so either one can land after the other and state a decision the other already overtook.
/// Re-reading before writing is not a compare-and-swap, so the ORDER between the two has to live
/// somewhere nothing replaces: a landing record and an uninstall tombstone, each immutable, each
/// ordered by its own arrival.
///
/// <para>Deterministic, like <see cref="ConcurrentModuleLandingTest"/>: two
/// <see cref="ModuleLandingService"/> replicas over one root, and one replica's whole operation runs
/// inside the other's projection window — after it decided, before it wrote the per-module file.</para>
/// </summary>
public class ConcurrentUninstallTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.ConcurrentUninstall";
    private const string MarkerAsset = "wwwroot/build.txt";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-concurrent-uninstall-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the landing root both replicas share.</summary>
    public ConcurrentUninstallTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private static (IReadOnlyList<(string, byte[])> Assemblies, IReadOnlyList<(string, byte[])> Assets) Bundle(string content) =>
        ([(Module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
         [(MarkerAsset, Encoding.UTF8.GetBytes(content))]);

    private static async Task Shelve(ModuleLandingService replica, string version)
    {
        var (assemblies, assets) = Bundle(version);
        await replica.ShelveModule(
                Module, assemblies, frameworkMvid: "test-build", packagePath: "Plugins/ConcurrentUninstall",
                version: version, staticAssets: assets)
            .Timeout(TestTimeouts.Convergence).Await();
    }

    private static void ShelveNow(ModuleLandingService replica, string version)
    {
        var (assemblies, assets) = Bundle(version);
        replica.LandCore(
            Module, assemblies, "test-build", "Plugins/ConcurrentUninstall", version, minMeshVersion: null,
            assets, sourceCommit: null, nativeAssets: null, holdUnloadable: true, keepNewerHead: true);
    }

    /// <summary>What an image that predates the landing records reads: the per-module file, and
    /// nothing else.</summary>
    private ModuleActivationEntry OlderImageView() =>
        JsonSerializer.Deserialize<ModuleActivationEntry>(
            File.ReadAllText(ModuleActivationSidecar.EntryPath(root, Module)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>
    /// 🚨 Copilot's interleaving, verbatim: replica A lands and DERIVES its projection; replica B
    /// uninstalls — its decision and its disabled per-module file both complete; THEN A writes the
    /// projection it derived before the uninstall. The uninstall came after the landing, so the
    /// module is uninstalled — and a current image must say so whatever the per-module file says.
    ///
    /// <para>The per-module file itself ends as A's stale projection, because it is last-writer-wins
    /// and nothing can make it otherwise without a compare-and-swap the store does not have. That is
    /// what an IMAGE THAT PREDATES THE RECORDS reads, and it is asserted here so the residue is a
    /// measured statement rather than a hope: such an image sees the module enabled until the next
    /// current-image write of this module's file — exactly what every image saw on main for the
    /// same interleaving.</para>
    /// </summary>
    [Fact]
    public async Task AnUninstallInsideALandingsProjectionWindow_IsNotUndoneByTheStaleProjection()
    {
        var uninstalled = false;
        using var replicaB = new ModuleLandingService(null, root, beforeRecording: null);
        using var replicaA = new ModuleLandingService(null, root, beforeRecording: null, beforeProjecting: _ =>
        {
            if (!uninstalled)
            {
                uninstalled = true;
                replicaB.RemoveCore(Module);
            }
        });

        await Shelve(replicaA, "1.7.0");
        Assert.True(uninstalled);

        var entry = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.False(entry.Enabled, "the uninstall came after the landing; a stale projection must not undo it");
        Assert.Null(entry.Directory);
        var seenByB = Assert.Single((await replicaB.GetActivation().Timeout(TestTimeouts.Convergence).Await()).Entries);
        Assert.False(seenByB.Enabled);

        // The residue, stated: an older image reads only the per-module file, which A wrote last.
        Assert.True(OlderImageView().Enabled,
            "the per-module file is last-writer-wins — an image that predates the records reads A's "
            + "stale projection until the next current-image write of it, as it did on main");

        // …and the NEXT landing, which comes after the uninstall, is what brings the module back.
        await Shelve(replicaB, "1.6.0");
        var back = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.True(back.Enabled);
        Assert.Equal("1.6.0", back.Version);
        Assert.True(OlderImageView().Enabled);
        Assert.Equal("1.6.0", OlderImageView().Version);
    }

    /// <summary>
    /// The mirror image, and the control that stops "uninstall always wins" from passing the test
    /// above: replica B decides an uninstall; replica A lands a new build inside B's projection
    /// window; THEN B writes its disabled per-module file. The landing came after the uninstall, so
    /// the module is installed, at the new build.
    ///
    /// <para>Here the per-module file ends DISABLED (B wrote it last), which is what an image that
    /// predates the records reads until the next current-image write of it — the other direction
    /// of the same last-writer-wins residue.</para>
    /// </summary>
    [Fact]
    public async Task ALandingInsideAnUninstallsProjectionWindow_IsNotUndoneByTheStaleDisabledEntry()
    {
        using var replicaA = new ModuleLandingService(null, root, beforeRecording: null);
        var landed = false;
        using var replicaB = new ModuleLandingService(null, root, beforeRecording: null, beforeProjecting: _ =>
        {
            if (!landed)
            {
                landed = true;
                ShelveNow(replicaA, "1.7.1");
            }
        });
        await Shelve(replicaA, "1.7.0");

        replicaB.RemoveCore(Module);
        Assert.True(landed);

        var entry = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.True(entry.Enabled, "the landing came after the uninstall; a stale disabled entry must not undo it");
        Assert.Equal("1.7.1", entry.Version);
        Assert.False(OlderImageView().Enabled,
            "the per-module file is last-writer-wins — an image that predates the records reads B's "
            + "stale disabled entry until the next current-image write of it");
    }
}
