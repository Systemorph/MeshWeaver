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
/// #4026 after #4427 merged — the post-merge review's three defects, each reproduced on the real
/// landing service over a real landing root, deterministically.
///
/// <list type="number">
///   <item>An OLDER image's install or uninstall exists only as a <c>&lt;Name&gt;.json</c> without
///     <c>projectionOf</c>; the next current-image write of that file used to overwrite the only
///     trace of it, and from then on the derivation re-dated or dropped the event.</item>
///   <item>An UNREADABLE record, tombstone or entry file used to be skipped with a log line — so the
///     answer CHANGED (an uninstalled module loaded, an older generation took the head) instead of
///     the module being dropped, and a module set could be proposed from the partial read.</item>
///   <item>Per-platform verdicts were kept forever, so every old build pinned its own fallback
///     record and bytes until uninstall.</item>
/// </list>
///
/// <para>"An older image" is simulated exactly the way one writes: its generation's bytes put in
/// place under the content-addressed leaf, and its per-module entry written by
/// <see cref="ModuleActivationSidecar.WriteEntry"/> with no <c>projectionOf</c> — a field that image
/// does not know.</para>
/// </summary>
public class ModuleActivationPostMergeTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.PostMerge";
    private const string MarkerAsset = "wwwroot/build.txt";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-activation-post-merge-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the landing root the replicas share.</summary>
    public ModuleActivationPostMergeTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private static (IReadOnlyList<(string, byte[])> Assemblies, IReadOnlyList<(string, byte[])> Assets) Bundle(string content) =>
        ([(Module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
         [(MarkerAsset, Encoding.UTF8.GetBytes(content))]);

    private static async Task<ModuleLandingOutcome> Shelve(ModuleLandingService replica, string version)
    {
        var (assemblies, assets) = Bundle(version);
        return await replica.ShelveModule(
                Module, assemblies, frameworkMvid: "test-build", packagePath: "Plugins/PostMerge",
                version: version, staticAssets: assets)
            .Timeout(TestTimeouts.Convergence).Await();
    }

    private ModuleLandingService Replica(string? platform = null) =>
        new(null, root, beforeRecording: null, platformIdentity: platform);

    private ModuleActivationEntry? Entry() =>
        ModuleActivationSidecar.Read(root).Entries.SingleOrDefault(e => e.Name == Module);

    private string MarkerIn(ModuleActivationEntry entry) =>
        File.ReadAllText(Path.Combine(
            ModuleLandingService.ModuleDirectoryFor(root, Module, entry),
            MarkerAsset.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// What an image that predates the landing records does when it installs a build: the bytes go
    /// into the content-addressed generation, and its per-module entry names them — the displaced
    /// head as the previous generation, the way that image's <c>PreviousToKeep</c> did. No record,
    /// no <c>projectionOf</c>.
    /// </summary>
    private string OlderImageInstalls(string version)
    {
        var (assemblies, assets) = Bundle(version);
        var generation = $"{Module}@{ModuleLandingService.GenerationIdOf(assemblies, assets)}";
        var directory = Path.Combine(root, "modules", generation);
        Directory.CreateDirectory(Path.Combine(directory, "wwwroot"));
        foreach (var (file, bytes) in assemblies)
            File.WriteAllBytes(Path.Combine(directory, file), bytes);
        foreach (var (file, bytes) in assets)
            File.WriteAllBytes(Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar)), bytes);
        var displaced = Entry() is { Enabled: true } current ? current : null;
        ModuleActivationSidecar.WriteEntry(root, new ModuleActivationEntry
        {
            Name = Module,
            PackagePath = "Plugins/PostMerge",
            Directory = generation,
            Version = version,
            FrameworkMvid = "test-build",
            PreviousDirectory = displaced?.Directory,
            PreviousVersion = displaced?.Version,
            PreviousFrameworkMvid = displaced?.FrameworkMvid,
            Enabled = true,
        });
        return generation;
    }

    /// <summary>What an image that predates the landing records writes to uninstall: its disabled
    /// per-module entry, and nothing else.</summary>
    private void OlderImageUninstalls()
    {
        var existing = Entry()!;
        ModuleActivationSidecar.WriteEntry(root, existing with
        {
            Enabled = false,
            Directory = null,
            PreviousDirectory = null,
            PreviousVersion = null,
            PreviousFrameworkMvid = null,
        });
    }

    // ── 1. an older image's event outlives the next current-image write of its file ──────────

    /// <summary>
    /// 🚨 The review's scenario, verbatim: a current image uninstalls (tombstone T1); during a
    /// rollback an older image reinstalls 1.7.0 (T2); current images read 1.7.0; a current image
    /// then shelves 1.6.1 (T3) — and its own report says 1.7.0 stays the head. The NEXT read must
    /// say the same: before this change it derived 1.6.1 with no fallback, because the overwrite
    /// was the only trace of T2, and the module set would have carried that fleet-wide (#3996
    /// again).
    /// </summary>
    [Fact]
    public async Task AnOlderImagesReinstallAfterAnUninstall_SurvivesTheNextCurrentLanding()
    {
        using var current = Replica();
        await Shelve(current, "1.7.0");
        current.RemoveCore(Module);
        Assert.False(Entry()!.Enabled);

        OlderImageInstalls("1.7.0");
        var reinstalled = Entry()!;
        Assert.True(reinstalled.Enabled, "precondition: current images read the older image's reinstall");
        Assert.Equal("1.7.0", reinstalled.Version);

        var outcome = await Shelve(current, "1.6.1");
        Assert.True(outcome.ShelfOnly);
        Assert.Equal("1.7.0", outcome.HeadVersion);

        var after = Entry()!;
        Assert.True(after.Enabled);
        Assert.Equal("1.7.0", after.Version);
        Assert.Equal("1.7.0", MarkerIn(after));
        Assert.Equal("1.6.1", after.PreviousVersion);
    }

    /// <summary>
    /// The mirror: an older image UNINSTALLS after current-image landings, then a current image
    /// lands an older build. That landing comes after the uninstall, so it is a FIRST landing and
    /// takes the head — and it must stay one after the overwrite. Before this change the overwrite
    /// erased the uninstall and the pre-uninstall 1.7.0 record came back to life as the head.
    /// </summary>
    [Fact]
    public async Task AnOlderImagesUninstall_SurvivesTheNextCurrentLanding()
    {
        using var current = Replica();
        await Shelve(current, "1.7.0");
        OlderImageUninstalls();
        Assert.False(Entry()!.Enabled, "precondition: current images read the older image's uninstall");

        var outcome = await Shelve(current, "1.6.0");
        Assert.False(outcome.ShelfOnly, "the first landing after an uninstall is the head");

        var after = Entry()!;
        Assert.True(after.Enabled);
        Assert.Equal("1.6.0", after.Version);
        Assert.Null(after.PreviousDirectory);
    }

    /// <summary>
    /// An older image's ADOPT of an older version (the Store's rollback there: 1.5.0 over 1.6.0)
    /// is that image's decision at its time. A later shelf landing of 1.4.0 ranks below it and
    /// leaves it the head — and the overwrite that landing does must not flip the head back to
    /// 1.6.0, which is what re-dating the older image's head to "the beginning of time" did.
    /// </summary>
    [Fact]
    public async Task AnOlderImagesAdoptOfAnOlderVersion_SurvivesTheNextShelfLanding()
    {
        using var current = Replica();
        await Shelve(current, "1.6.0");
        OlderImageInstalls("1.5.0");
        Assert.Equal("1.5.0", Entry()!.Version);

        var outcome = await Shelve(current, "1.4.0");
        Assert.True(outcome.ShelfOnly);

        var after = Entry()!;
        Assert.Equal("1.5.0", after.Version);
        Assert.Equal("1.5.0", MarkerIn(after));
    }

    // ── 2. an unreadable record changes nothing — the module is dropped, and says so ────────

    private static void Corrupt(string path) => File.WriteAllText(path, "{ this is not a record");

    private async Task<(ModuleLandingService Replica, List<string> Faults)> LandedAndUninstalled()
    {
        var replica = Replica();
        await Shelve(replica, "1.7.0");
        replica.RemoveCore(Module);
        return (replica, []);
    }

    /// <summary>
    /// 🚨 An SMB sharing violation on the tombstone at boot used to load an UNINSTALLED module: the
    /// unreadable tombstone was skipped, the landing before it counted again, and the module came
    /// back. An unreadable event must never produce a different answer — the module is dropped
    /// (not loaded) and the read says why; and a module set is never proposed from such a read.
    /// </summary>
    [Fact]
    public async Task AnUnreadableTombstone_DropsTheModule_AndNothingIsProposedFromThePartialRead()
    {
        var (replica, faults) = await LandedAndUninstalled();
        using var _ = replica;
        var tombstone = Assert.Single(Directory.GetFiles(
            ModuleActivationSidecar.LandingRecordsDirectory(root, Module), "*" + ModuleActivationSidecar.UninstallSuffix));
        Corrupt(tombstone);

        var read = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.DoesNotContain(read.Entries, e => e.Name == Module);
        Assert.Contains(faults, f => f.Contains(Module, StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await replica.ProposeModuleSet().Timeout(TestTimeouts.Convergence).Await());
    }

    /// <summary>An unreadable HEAD record used to promote an older generation to the head — a
    /// different answer, which boot would load and a wave would propose. Dropped instead.</summary>
    [Fact]
    public async Task AnUnreadableHeadRecord_DropsTheModule_NeverFallsBackToAnOlderGeneration()
    {
        using var replica = Replica();
        await Shelve(replica, "1.6.0");
        await Shelve(replica, "1.7.0");
        var head = Entry()!;
        Assert.Equal("1.7.0", head.Version);
        var headRecord = Assert.Single(Directory.GetFiles(
            ModuleActivationSidecar.LandingRecordsDirectory(root, Module), head.Directory + ".*.json"));
        Corrupt(headRecord);
        var faults = new List<string>();

        var read = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.DoesNotContain(read.Entries, e => e.Name == Module);
        Assert.Contains(faults, f => f.Contains(Module, StringComparison.Ordinal));
    }

    /// <summary>
    /// The per-module file itself unreadable, with a stale legacy-aggregate row behind it: the
    /// row used to be taken as an older image's install stamped with the UNREADABLE file's recent
    /// write time — newer than the uninstall — and the module came back. The module is dropped.
    /// </summary>
    [Fact]
    public async Task AnUnreadableEntryFile_DoesNotLetAStaleAggregateRowDecide()
    {
        var (replica, faults) = await LandedAndUninstalled();
        using var _ = replica;
        var stale = ModuleActivationSidecar.ReadStored(root).Entries.Single(e => e.Name == Module) with
        {
            Enabled = true,
            Directory = Module + "@0123456789abcdef",
            Version = "1.7.0",
            ProjectionOf = null,
        };
        File.WriteAllText(ModuleActivationSidecar.SidecarPath(root), JsonSerializer.Serialize(
            new ModuleActivationList { Entries = [stale] }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.SetLastWriteTimeUtc(ModuleActivationSidecar.SidecarPath(root), DateTime.UtcNow.AddDays(-30));
        Corrupt(ModuleActivationSidecar.EntryPath(root, Module));

        var read = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.DoesNotContain(read.Entries, e => e.Name == Module && e.Enabled);
        Assert.Contains(faults, f => f.Contains(Module, StringComparison.Ordinal));
    }

    // ── 3. a platform no replica has measured recently stops protecting its fallback ───────

    /// <summary>
    /// 🚨 Verdicts are per platform build, and the platform key changes with most builds — so a
    /// rule that protected every platform's fallback kept every old build's fallback record and
    /// bytes for the life of the module. Only the platforms that measured the module most recently
    /// (and the GC's own, and the one with no verdicts) protect their fallback; an older build's
    /// verdicts are retired, and the generation only IT would fall back to is reclaimed.
    ///
    /// <para>build-1 measured 1.6.0 as not loading, so ITS fallback is 1.5.0 while every later
    /// build's is 1.6.0. Four builds later, nothing should still hold 1.5.0.</para>
    /// </summary>
    [Fact]
    public async Task APlatformThatNoLongerMeasuresTheModule_StopsProtectingItsFallback()
    {
        using var build4 = Replica("build-4");
        await Shelve(build4, "1.5.0");
        var onlyBuild1Needs = Entry()!.Directory!;
        await Shelve(build4, "1.6.0");
        var newestFallback = Entry()!.Directory!;
        await Shelve(build4, "1.7.0");
        var head = Entry()!.Directory!;

        var now = DateTime.UtcNow;
        void Measured(string platform, string generation, bool linkable, DateTime at)
        {
            Assert.True(ModuleActivationSidecar.WriteVerdict(root, Module, generation, platform, linkable));
            var file = Directory.GetFiles(ModuleActivationSidecar.LandingRecordsDirectory(root, Module),
                    generation + ".*" + ModuleActivationSidecar.VerdictSuffix)
                .Single(path => File.ReadAllText(path).Contains("\"" + platform + "\"", StringComparison.Ordinal));
            File.SetLastWriteTimeUtc(file, at);
        }
        Measured("build-1", onlyBuild1Needs, true, now.AddHours(-4));
        Measured("build-1", newestFallback, false, now.AddHours(-4));
        Measured("build-1", head, true, now.AddHours(-4));
        foreach (var (platform, age) in new[] { ("build-2", -3), ("build-3", -2) })
            foreach (var generation in new[] { onlyBuild1Needs, newestFallback, head })
                Measured(platform, generation, true, now.AddHours(age));
        Assert.True(Directory.Exists(Path.Combine(root, "modules", onlyBuild1Needs)));

        ModuleLandingService.CollectGarbage(root, minAge: TimeSpan.Zero, nowUtc: now.AddHours(1));

        Assert.False(Directory.Exists(Path.Combine(root, "modules", onlyBuild1Needs)),
            "only build-1 — four builds ago — would fall back to 1.5.0; it no longer pins those bytes");
        var platformsLeft = Directory.GetFiles(ModuleActivationSidecar.LandingRecordsDirectory(root, Module),
                "*" + ModuleActivationSidecar.VerdictSuffix)
            .Select(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("platform").GetString())
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["build-3", "build-4"], platformsLeft);
        var after = Entry()!;
        Assert.Equal(head, after.Directory);
        Assert.Equal(newestFallback, after.PreviousDirectory);
        Assert.True(ModuleActivationBoot.PreviousLandedModuleDllExists(root, after));
    }
}
