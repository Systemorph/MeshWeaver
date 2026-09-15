using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #4026's landing RECORDS beyond the race itself (<see cref="ConcurrentModuleLandingTest"/>):
/// the common same-bundle case stays one file, the on-disk shapes older images wrote keep their
/// meaning (a stored head is the first landing's fallback; a stored uninstall beats every record),
/// the adopt lane keeps "an older version is an operator who asked for it", and retention can
/// never change what <see cref="ModuleActivationSidecar.Read"/> answers.
/// </summary>
public class ModuleLandingRecordsTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.LandingRecords";
    private const string MarkerAsset = "wwwroot/build.txt";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-landing-records-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the landing root both replicas share.</summary>
    public ModuleLandingRecordsTest() => Directory.CreateDirectory(root);

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

    private static async Task Adopt(ModuleLandingService replica, string version, string content)
    {
        var (assemblies, assets) = Bundle(content);
        await replica.LandModule(
                Module, assemblies, frameworkMvid: "test-build", packagePath: "Plugins/ConcurrentShelf",
                version: version, staticAssets: assets)
            .Timeout(TestTimeouts.Convergence).Await();
    }

    /// <summary>WHICH bytes a generation holds, read off the disk rather than inferred from the
    /// version the entry claims.</summary>
    private string MarkerIn(ModuleActivationEntry entry) =>
        File.ReadAllText(Path.Combine(
            ModuleLandingService.ModuleDirectoryFor(root, Module, entry),
            MarkerAsset.Replace('/', Path.DirectorySeparatorChar)));

    private string[] RecordFiles() =>
        Directory.Exists(ModuleActivationSidecar.LandingRecordsDirectory(root, Module))
            ? Directory.GetFiles(ModuleActivationSidecar.LandingRecordsDirectory(root, Module), "*.json")
            : [];

    private void CollectGarbage() =>
        ModuleLandingService.CollectGarbage(root, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));

    /// <summary>
    /// The COMMON shape, and the one that must stay free: every replica reconciles the same feed,
    /// so two replicas landing the SAME bundle at once happens all day. Content addressing makes it
    /// one generation (#3656) — and, one level down, one landing RECORD: the second writes nothing.
    /// A record store that grew by one file per replica per landing would trade a race for
    /// unbounded growth.
    /// </summary>
    [Fact]
    public async Task TwoReplicasLandingTheSameBundleInOneWindow_WriteOneRecord()
    {
        using var replicaA = new ModuleLandingService(null, root, beforeRecording: null);
        var ran = false;
        using var replicaB = new ModuleLandingService(null, root, beforeRecording: _ =>
        {
            if (!ran)
            {
                ran = true;
                ShelveNow(replicaA, "1.7.0", "1.7.0");
            }
        });

        var outcome = await Shelve(replicaB, "1.7.0", "1.7.0");

        Assert.True(ran);
        Assert.False(outcome.ShelfOnly);
        Assert.Single(RecordFiles());
        var head = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("1.7.0", head.Version);
        Assert.Null(head.PreviousDirectory);
    }

    /// <summary>
    /// 🚨 BACKWARD COMPATIBILITY, read side: a module landed by an image that predates the records
    /// has only its stored entry. It must read exactly as before, and the first landing on a
    /// current image must keep that stored head as the new head's FALLBACK — or the upgrade would
    /// silently drop the one generation known to load (#3649) and the GC would reclaim it.
    /// </summary>
    [Fact]
    public async Task AStoredEntryFromAnOlderImage_ReadsUnchanged_AndBecomesTheFirstLandingsFallback()
    {
        using var replica = new ModuleLandingService(null, root, beforeRecording: null);
        await Shelve(replica, "1.6.0", "1.6.0");
        // What an older image leaves behind: the per-module entry, and no landing records at all.
        ModuleActivationSidecar.RemoveLandingRecords(root, Module);
        Assert.Empty(RecordFiles());
        var stored = Assert.Single(ModuleActivationSidecar.ReadStored(root).Entries);
        Assert.Equal(stored, Assert.Single(ModuleActivationSidecar.Read(root).Entries));

        await Shelve(replica, "1.7.0", "1.7.0");

        var head = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("1.7.0", head.Version);
        Assert.Equal(stored.Directory, head.PreviousDirectory);
        Assert.Equal("1.6.0", head.PreviousVersion);
        CollectGarbage();
        Assert.True(ModuleActivationBoot.PreviousLandedModuleDllExists(root, head),
            "the pre-upgrade generation is the fallback now, so the GC must keep it");
    }

    /// <summary>
    /// 🚨 BACKWARD COMPATIBILITY, write side: an image that predates the records uninstalls by
    /// writing a DISABLED per-module entry and never touches the records. That uninstall must win
    /// — the stored layers decide WHETHER a module is installed, the records only WHICH generation
    /// it runs. Ranking the records above it would resurrect a module an operator removed.
    /// </summary>
    [Fact]
    public async Task AnUninstallWrittenByAnOlderImage_BeatsTheLandingRecords()
    {
        using var replica = new ModuleLandingService(null, root, beforeRecording: null);
        await Shelve(replica, "1.7.0", "1.7.0");
        var landed = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.NotEmpty(RecordFiles());

        // Exactly what the pre-#4026 RemoveCore writes, and all it writes.
        ModuleActivationSidecar.WriteEntry(root, landed with
        {
            Enabled = false,
            Directory = null,
            PreviousDirectory = null,
            PreviousVersion = null,
            PreviousFrameworkMvid = null,
        });

        var entry = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.False(entry.Enabled);
        Assert.Null(entry.Directory);
    }

    /// <summary>
    /// The documented registry rollback — "uninstall the module first, then publish the older
    /// build" (#3996) — needs the uninstall to forget the landings, or the newer build's record
    /// would still outrank the older one and the rollback would be refused as shelf-only.
    /// </summary>
    [Fact]
    public async Task UninstallThenPublishingAnOlderBuild_IsAFirstLanding()
    {
        using var replica = new ModuleLandingService(null, root, beforeRecording: null);
        await Shelve(replica, "1.7.0", "1.7.0");
        await replica.RemoveModule(Module).Timeout(TestTimeouts.Convergence).Await();
        Assert.False(Assert.Single(ModuleActivationSidecar.Read(root).Entries).Enabled);

        var outcome = await Shelve(replica, "1.6.1", "1.6.1");

        Assert.False(outcome.ShelfOnly);
        var head = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.True(head.Enabled);
        Assert.Equal("1.6.1", head.Version);
        Assert.Equal("1.6.1", MarkerIn(head));
    }

    /// <summary>
    /// The ADOPT lane keeps its own rule: an older version there is an operator who asked for it.
    /// Re-installing the generation this deployment ran before — the Store's rollback — lands bytes
    /// whose record is ALREADY on the volume, and it must still take the head: an identical record
    /// is idempotent, but a landing that would move the head records a re-arrival of its own. A
    /// second identical re-install, with the head already there, writes nothing.
    /// </summary>
    [Fact]
    public async Task AnAdoptReinstallOfThePreviousGeneration_TakesTheHeadAgain()
    {
        using var replica = new ModuleLandingService(null, root, beforeRecording: null);
        await Adopt(replica, "1.6.0", "1.6.0");
        await Adopt(replica, "1.7.0", "1.7.0");
        Assert.Equal("1.7.0", Assert.Single(ModuleActivationSidecar.Read(root).Entries).Version);

        await Adopt(replica, "1.6.0", "1.6.0");

        var head = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("1.6.0", head.Version);
        Assert.Equal("1.6.0", MarkerIn(head));
        Assert.Equal("1.7.0", head.PreviousVersion);
        var records = RecordFiles().Length;

        await Adopt(replica, "1.6.0", "1.6.0");

        Assert.Equal(records, RecordFiles().Length);
        Assert.Equal(head, Assert.Single(ModuleActivationSidecar.Read(root).Entries));
    }

    /// <summary>
    /// The other half of re-arrival: a landing whose record is already on the volume and which
    /// would NOT change the derived entry writes nothing. The head's own bytes re-published under a
    /// lower label (the late gate lane re-sending a build the newer lane already published) fold to
    /// the entry already derived — a record per such publish would be growth for nothing.
    /// </summary>
    [Fact]
    public async Task ARepeatedLowerLabelOfTheHeadsOwnBytes_WritesNoFurtherRecord()
    {
        using var replica = new ModuleLandingService(null, root, beforeRecording: null);
        await Shelve(replica, "1.7.0", "same-bytes");
        await Shelve(replica, "1.6.1", "same-bytes");
        var records = RecordFiles().Length;
        var head = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("1.7.0", head.Version);

        var outcome = await Shelve(replica, "1.6.1", "same-bytes");

        Assert.True(outcome.ShelfOnly);
        Assert.Equal(records, RecordFiles().Length);
        Assert.Equal(head, Assert.Single(ModuleActivationSidecar.Read(root).Entries));
    }

    /// <summary>
    /// 🚨 RETENTION can never change the answer. A record is retired only when the entry derived
    /// without it is identical; a superseded build's record goes, its generation is then
    /// unreferenced and the GC reclaims it, and the head and fallback are untouched.
    /// </summary>
    [Fact]
    public async Task Retention_RetiresARecordTheDerivationNoLongerNeeds_AndNothingElse()
    {
        using var replica = new ModuleLandingService(null, root, beforeRecording: null);
        await Shelve(replica, "1.5.0", "1.5.0");
        var superseded = Assert.Single(ModuleActivationSidecar.Read(root).Entries).Directory;
        await Shelve(replica, "1.6.0", "1.6.0");
        await Shelve(replica, "1.7.0", "1.7.0");
        Assert.Equal(3, RecordFiles().Length);
        var before = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("1.7.0", before.Version);
        Assert.Equal("1.6.0", before.PreviousVersion);

        CollectGarbage();

        Assert.Equal(2, RecordFiles().Length);
        Assert.Equal(before, Assert.Single(ModuleActivationSidecar.Read(root).Entries));
        Assert.False(Directory.Exists(Path.Combine(root, "modules", superseded!)),
            "the superseded generation is referenced by nothing, so the GC reclaims it");
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, before));
        Assert.True(ModuleActivationBoot.PreviousLandedModuleDllExists(root, before));
    }

    /// <summary>
    /// 🚨 The case a "keep the head and the fallback" retention rule gets WRONG. An UNVERSIONED
    /// landing moves the head whatever it follows (rule R2), so a later OLDER shelf landing takes
    /// the head from it — and the head therefore depends on a record that is neither the head nor
    /// the fallback. Retiring that record would let 1.0.0 displace 0.9.0 five minutes after the
    /// last landing, with nothing having landed. Retention keeps it, because the derivation without
    /// it differs.
    /// </summary>
    [Fact]
    public async Task Retention_KeepsARecordTheHeadDependsOn_EvenWhenItIsNeitherHeadNorFallback()
    {
        using var replica = new ModuleLandingService(null, root, beforeRecording: null);
        await Shelve(replica, "1.0.0", "1.0.0");
        await Shelve(replica, null, "unversioned");
        await Shelve(replica, "0.9.0", "0.9.0");
        // Arrival is the files' own write time; state it rather than trust the clock's resolution.
        var arrival = DateTime.UtcNow.AddMinutes(-30);
        foreach (var file in RecordFiles())
        {
            var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            var version = record.TryGetProperty("version", out var v) ? v.GetString() : null;
            File.SetLastWriteTimeUtc(file, arrival.AddSeconds(version switch
            {
                "1.0.0" => 1,
                null => 2,
                _ => 3,
            }));
        }
        var before = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("0.9.0", before.Version);
        Assert.Equal("1.0.0", before.PreviousVersion);

        CollectGarbage();

        Assert.Equal(3, RecordFiles().Length);
        Assert.Equal(before, Assert.Single(ModuleActivationSidecar.Read(root).Entries));
    }
}
