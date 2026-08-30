using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The #2509 hardening, pinned: generations GC must never turn a readable-yesterday module into a
/// broken one. Three invariants, each of which was violated on prod 2026-08-27:
/// (1) an UNREADABLE activation entry file makes the reference set incomplete, so no generation
/// may be deleted that pass — unreadable is never unreferenced;
/// (2) removal is ATOMIC per directory — an interrupted delete must never leave a half-gutted
/// generation (entry DLL present, lazily-loaded dependency DLLs gone);
/// (3) a store-landed generation is PINNED to process-local storage before loading, so a GC pass
/// on another replica reclaiming the shared directory cannot break this process's lazy loads.
/// </summary>
public class GenerationGcHardeningTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-gc-" + Guid.NewGuid().ToString("N"));

    public GenerationGcHardeningTest() => Directory.CreateDirectory(Path.Combine(root, "modules"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private string Modules => Path.Combine(root, "modules");

    private void Write(string folder, string file, string content)
    {
        Directory.CreateDirectory(Path.Combine(Modules, folder));
        File.WriteAllText(Path.Combine(Modules, folder, file), content);
    }

    private static void Backdate(string dir) =>
        Directory.SetLastWriteTimeUtc(dir,
            DateTime.UtcNow - ModuleLandingService.DefaultGarbageMinAge - TimeSpan.FromMinutes(1));

    [Fact]
    public void CollectGarbage_SkipsEveryGenerationDelete_WhenAnEntryFileIsUnreadable()
    {
        // Widget's generation is ACTIVE — but its entry file is unreadable garbage, so the read
        // skips it (per-module resilience, #2189) and the reference set no longer knows the
        // generation is claimed. Before the fix, GC deleted it: the dangling activation entries
        // #2509 measured on both prods.
        Write("MeshWeaver.Widget@live0001", "MeshWeaver.Widget.dll", "LIVE");
        Backdate(Path.Combine(Modules, "MeshWeaver.Widget@live0001"));
        var entries = Path.Combine(Modules, ModuleActivationSidecar.EntriesDirectoryName);
        Directory.CreateDirectory(entries);
        File.WriteAllText(Path.Combine(entries, "MeshWeaver.Widget.json"), "{ not json");

        // A genuine orphan of ANOTHER module sits beside it. With the reference set unreliable,
        // GC cannot tell these two cases apart — so it must delete NEITHER.
        Write("MeshWeaver.Other@dead0001", "MeshWeaver.Other.dll", "DEAD");
        Backdate(Path.Combine(Modules, "MeshWeaver.Other@dead0001"));

        // Transient folders are still collectable — nothing references .staging-* by design.
        Write(".staging-MeshWeaver.Widget-x", "half.dll", "H");
        Backdate(Path.Combine(Modules, ".staging-MeshWeaver.Widget-x"));

        var removed = ModuleLandingService.CollectGarbage(root);

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(Path.Combine(Modules, "MeshWeaver.Widget@live0001")),
            "the ACTIVE generation of the module with the unreadable entry must survive — deleting "
            + "it is exactly the dangling-entry outage (#2509)");
        Assert.True(Directory.Exists(Path.Combine(Modules, "MeshWeaver.Other@dead0001")),
            "with the reference set incomplete, even a genuine orphan must wait for a pass that "
            + "can actually tell it apart from an active generation");
        Assert.False(Directory.Exists(Path.Combine(Modules, ".staging-MeshWeaver.Widget-x")),
            ".staging-* is unreferenced by design and still collects on an unreliable pass");
    }

    [Fact]
    public void CollectGarbage_RemovalIsAtomic_AnInterruptedDeleteLeavesNoHalfGuttedGeneration()
    {
        // An unreferenced old generation with an entry DLL and a lazily-loaded dependency — the
        // shape whose PARTIAL deletion (entry survives a locked-file abort, dependency does not)
        // produced `Could not load file or assembly 'OpenAI'` on prod.
        Write("MeshWeaver.Widget@dead0001", "MeshWeaver.Widget.dll", "ENTRY");
        Write("MeshWeaver.Widget@dead0001", "OpenAI.dll", "LAZY-DEP");
        Backdate(Path.Combine(Modules, "MeshWeaver.Widget@dead0001"));

        // The delete is interrupted (an SMB lock mid-recursion). The rename must already have
        // committed the removal: gone from modules/ resolution, fully intact in .trash-*.
        var removed = ModuleLandingService.CollectGarbage(
            root, logger: null, minAge: null, nowUtc: null,
            deleteDirectory: _ => throw new IOException("locked mid-recursion"));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(Path.Combine(Modules, "MeshWeaver.Widget@dead0001")),
            "the generation must be OUT of the modules/ namespace in one atomic rename");
        var trash = Directory.EnumerateDirectories(Modules, ".trash-*").ToArray();
        var trashed = Assert.Single(trash);
        Assert.True(File.Exists(Path.Combine(trashed, "MeshWeaver.Widget.dll")));
        Assert.True(File.Exists(Path.Combine(trashed, "OpenAI.dll")),
            "the trash folder holds the COMPLETE generation — no state in which the entry DLL "
            + "exists and the dependency does not");

        // A later pass finishes the job — .trash-* is exempt from the age grace (it is
        // unreferenced by construction).
        var secondPass = ModuleLandingService.CollectGarbage(root);
        Assert.Equal(1, secondPass);
        Assert.Empty(Directory.EnumerateDirectories(Modules, ".trash-*"));
    }

    /// <summary>
    /// #2684: the pass observes cancellation BETWEEN directories — the unit of atomic removal — so
    /// a teardown drain never waits out a sweep crawling a slow CIFS volume. A cancelled pass
    /// stops before its next rename and leaves only states a later pass already plans for: an
    /// intact orphan, or nothing (the in-flight removal completes, it is never torn mid-rename).
    /// </summary>
    [Fact]
    public void CollectGarbage_StopsBetweenDirectories_WhenCancelled()
    {
        Write("MeshWeaver.A@dead0001", "MeshWeaver.A.dll", "DEAD");
        Write("MeshWeaver.B@dead0002", "MeshWeaver.B.dll", "DEAD");
        Backdate(Path.Combine(Modules, "MeshWeaver.A@dead0001"));
        Backdate(Path.Combine(Modules, "MeshWeaver.B@dead0002"));

        // Already cancelled: the pass removes NOTHING — it never reaches a rename.
        var none = ModuleLandingService.CollectGarbage(
            root, cancellationToken: new CancellationToken(canceled: true));
        Assert.Equal(0, none);
        Assert.True(Directory.Exists(Path.Combine(Modules, "MeshWeaver.A@dead0001")));
        Assert.True(Directory.Exists(Path.Combine(Modules, "MeshWeaver.B@dead0002")));

        // Cancelled MID-pass (from inside the first removal's delete): the in-flight removal
        // completes atomically, and the loop stops before touching the second directory.
        using var cts = new CancellationTokenSource();
        var removed = ModuleLandingService.CollectGarbage(
            root, logger: null, minAge: null, nowUtc: null,
            deleteDirectory: dir =>
            {
                cts.Cancel();
                Directory.Delete(dir, recursive: true);
            },
            cancellationToken: cts.Token);

        Assert.Equal(1, removed);
        var survivors = Directory.EnumerateDirectories(Modules)
            .Where(d => Path.GetFileName(d).Contains('@'))
            .ToArray();
        Assert.Single(survivors);
        Assert.Empty(Directory.EnumerateDirectories(Modules, ".trash-*"));

        // Deferred is not dropped: an uncancelled later pass reclaims the survivor.
        Assert.Equal(1, ModuleLandingService.CollectGarbage(root));
    }

    [Fact]
    public void PinnedLoadPath_SurvivesTheSharedGenerationBeingReclaimed()
    {
        Write("MeshWeaver.Widget@gen00001", "MeshWeaver.Widget.dll", "ENTRY");
        Write("MeshWeaver.Widget@gen00001", "OpenAI.dll", "LAZY-DEP");
        var entry = new ModuleActivationEntry
        {
            Name = "MeshWeaver.Widget",
            Directory = "MeshWeaver.Widget@gen00001",
        };
        var pinRoot = Path.Combine(root, "pin");

        var pinned = ModuleGenerationPin.PinnedLoadPath(root, entry, pinRoot);

        Assert.StartsWith(pinRoot, pinned);
        Assert.Equal("ENTRY", File.ReadAllText(pinned));

        // Another replica's GC reclaims the shared generation — the pod keeps running.
        Directory.Delete(Path.Combine(Modules, "MeshWeaver.Widget@gen00001"), recursive: true);

        Assert.True(File.Exists(pinned), "the pinned entry DLL has process lifetime");
        // The lazily-loaded dependency is pinned WITH the entry — the outage was precisely a
        // dependency resolving after the shared directory was gone.
        Assert.Equal("LAZY-DEP",
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(pinned)!, "OpenAI.dll")));
    }

    [Fact]
    public void PinnedLoadPath_FallsBackToTheSharedPath_WhenPinningFails()
    {
        Write("MeshWeaver.Widget@gen00001", "MeshWeaver.Widget.dll", "ENTRY");
        var entry = new ModuleActivationEntry
        {
            Name = "MeshWeaver.Widget",
            Directory = "MeshWeaver.Widget@gen00001",
        };
        // A pin root that cannot be a directory: an existing FILE at that path.
        var blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "not a directory");
        string? warning = null;

        var resolved = ModuleGenerationPin.PinnedLoadPath(root, entry, blocked, msg => warning = msg);

        Assert.Equal(ModuleActivationBoot.LandedDllPath(root, entry), resolved);
        Assert.NotNull(warning);
        Assert.Contains("could not pin", warning);
    }
}
