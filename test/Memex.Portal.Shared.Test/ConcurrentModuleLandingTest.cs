using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// 🚨 #4026 — two REPLICAS landing the same module concurrently. Every portal replica shares one
/// <c>/data</c> volume, and a landing used to read the module's activation entry at its start,
/// decide against what it read, and REPLACE the entry by an unconditional rename seconds later. Two
/// publishes of one module reaching two replicas inside that window were last-writer-wins, so the
/// version rule of #3996 ("an older upload never displaces a newer head whose bytes are present")
/// held per PROCESS only: an older build finishing on replica B after replica A landed the newer one
/// put the head back.
///
/// <para>The interleaving is made DETERMINISTIC, not timed: each "replica" is its own
/// <see cref="ModuleLandingService"/> over one landing root (its own cap-1 pool — the thing that
/// serialises landings within ONE process and never across two), and the interrupted replica's
/// recording window runs the other replica's whole landing on the same thread. No sleep, no gate,
/// no timer — the window is exactly the one the defect lived in: after the bytes are on the volume,
/// before anything is recorded.</para>
/// </summary>
public class ConcurrentModuleLandingTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.ConcurrentShelf";
    private const string MarkerAsset = "wwwroot/build.txt";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-concurrent-landing-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the landing root both replicas share.</summary>
    public ConcurrentModuleLandingTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    /// <summary>A module whose bytes LINK here (a real platform assembly) and whose marker asset
    /// makes each version a distinct content address — without it two versions would resolve to
    /// ONE generation directory and every head claim below would be vacuous.</summary>
    private static (IReadOnlyList<(string, byte[])> Assemblies, IReadOnlyList<(string, byte[])> Assets) Bundle(string content) =>
        ([(Module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
         [(MarkerAsset, Encoding.UTF8.GetBytes(content))]);

    /// <summary>A replica's landing through its public publish-route entry point.</summary>
    private static async Task<ModuleLandingOutcome> Shelve(ModuleLandingService replica, string? version, string content)
    {
        var (assemblies, assets) = Bundle(content);
        return await replica.ShelveModule(
                Module, assemblies, frameworkMvid: "test-build", packagePath: "Plugins/ConcurrentShelf",
                version: version, staticAssets: assets)
            .Timeout(TestTimeouts.Convergence).Await();
    }

    /// <summary>A replica's WHOLE shelf landing, run synchronously — what the other replica's
    /// recording window executes. Another process's landing does not queue on this one's pool.</summary>
    private static ModuleLandingOutcome ShelveNow(ModuleLandingService replica, string version, string content)
    {
        var (assemblies, assets) = Bundle(content);
        return replica.LandCore(
            Module, assemblies, "test-build", "Plugins/ConcurrentShelf", version, minMeshVersion: null,
            assets, sourceCommit: null, nativeAssets: null, holdUnloadable: true, keepNewerHead: true);
    }

    /// <summary>WHICH bytes a generation holds, read off the disk rather than inferred from the
    /// version the entry claims.</summary>
    private string MarkerIn(ModuleActivationEntry entry) =>
        File.ReadAllText(Path.Combine(
            ModuleLandingService.ModuleDirectoryFor(root, Module, entry),
            MarkerAsset.Replace('/', Path.DirectorySeparatorChar)));

    private void CollectGarbage() =>
        ModuleLandingService.CollectGarbage(root, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));

    /// <summary>
    /// 🚨 THE DEFECT, both ways round. Mail 1.7.0 and 1.6.1 reached one registry eleven minutes
    /// apart on 2026-09-11 (#3996); this is the same pair reaching TWO replicas inside one landing's
    /// window. Whichever replica is interrupted, the deployment must converge on the newer head,
    /// with the older build retained as its fallback — and the report of the landing that recorded
    /// last must say so, because a publisher reading "head" off the older build would be reading
    /// the lost update.
    ///
    /// <para>The <c>older-interrupted</c> case is the one that regressed: replica B read an empty
    /// record, decided 1.6.1 was the head, and wrote that decision over the 1.7.0 entry replica A
    /// had written in between. The <c>newer-interrupted</c> case is its positive control — a rule
    /// that simply kept the FIRST landing would pass the other case and fail this one.</para>
    /// </summary>
    [Theory]
    [InlineData("older-interrupted")]
    [InlineData("newer-interrupted")]
    public async Task TwoReplicasLandingDifferentVersionsInOneWindow_ConvergeOnTheNewerHead(string interleaving)
    {
        var olderInterrupted = interleaving == "older-interrupted";
        ModuleLandingOutcome? inside = null;
        using var replicaA = new ModuleLandingService(null, root, beforeRecording: null);
        using var replicaB = new ModuleLandingService(null, root, beforeRecording: _ =>
        {
            // The other replica's landing runs to completion INSIDE this one's window, once.
            if (inside is null)
                inside = olderInterrupted
                    ? ShelveNow(replicaA, "1.7.0", "1.7.0")
                    : ShelveNow(replicaA, "1.6.1", "1.6.1");
        });

        var outer = olderInterrupted
            ? await Shelve(replicaB, "1.6.1", "1.6.1")
            : await Shelve(replicaB, "1.7.0", "1.7.0");
        Assert.NotNull(inside);

        // ── the head did NOT regress, whatever order the two writes landed in ────────────────
        var list = ModuleActivationSidecar.Read(root);
        var head = Assert.Single(list.Entries);
        Assert.Equal("1.7.0", head.Version);
        Assert.Equal("1.7.0", MarkerIn(head));

        // ── and the older build is retained as the fallback, not lost ────────────────────────
        Assert.Equal("1.6.1", head.PreviousVersion);
        var fallback = ModuleActivationBoot.PreviousGeneration(head);
        Assert.NotNull(fallback);
        Assert.Equal("1.6.1", MarkerIn(fallback!));

        // ── the landing that recorded LAST reports the converged head ─────────────────────────
        // (The one inside the window recorded first, alone, and reported what was true then.)
        Assert.Equal("1.7.0", outer.HeadVersion);
        if (olderInterrupted)
        {
            Assert.True(outer.ShelfOnly,
                "the older build must not report itself as the head — that report WAS the lost update");
            Assert.True(outer.RetainedAsFallback);
        }
        else
        {
            Assert.False(outer.ShelfOnly, "the newer build IS the head");
        }

        // ── the stored entry an OLDER image boots from says the same ──────────────────────────
        // The later of the two landings derived over BOTH records, so the projection it wrote for
        // images that predate the records is the converged answer too.
        var stored = JsonSerializer.Deserialize<ModuleActivationEntry>(
            File.ReadAllText(ModuleActivationSidecar.EntryPath(root, Module)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(head with { UnloadableFrameworkMvid = null }, stored);

        // ── and both generations survive the modules GC ───────────────────────────────────────
        CollectGarbage();
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, head));
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, fallback!));
        Assert.Equal(head, Assert.Single(ModuleActivationSidecar.Read(root).Entries));
    }
}
