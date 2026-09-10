using System;
using System.IO;
using System.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The unloadable-head marker (#3650) — the sidecar half, pinned pure over a temp directory: the
/// boot's measurement is a per-module marker FILE beside the entries, attached to the entry at
/// read time only while the entry still heads the generation it measured, never stored in the
/// entry file, and written only on a transition. The whole chain through the real loader is
/// <c>ConfiguredModuleActivationTest</c> (MeshWeaver.Compiler.Pipeline.Test); this file is the
/// contract each half of that chain relies on.
/// </summary>
public sealed class ModuleUnloadableMarkerTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.Marker";
    private const string Head = Module + "@head0000";
    private const string Previous = Module + "@prev0000";
    private const string HeadMvid = "s1111111111111111111111111111111a";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-marker-" + Guid.NewGuid().ToString("N"));

    public ModuleUnloadableMarkerTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp cleanup is the OS's problem */ }
    }

    private void WriteHead(string? frameworkMvid = HeadMvid) =>
        ModuleActivationSidecar.WriteEntry(root, new ModuleActivationEntry
        {
            Name = Module, Directory = Head, Version = "1.0.0", FrameworkMvid = frameworkMvid,
            PreviousDirectory = Previous, PreviousVersion = "1.0.0", PreviousFrameworkMvid = "s0",
        });

    private ModuleActivationEntry ReadEntry() =>
        ModuleActivationSidecar.Read(root).Entries.Single(e => e.Name == Module);

    /// <summary>The marker for the head reads back onto the entry; one for another generation is
    /// inert — a landing that moved the head on needs no write to retire it.</summary>
    [Fact]
    public void AMarker_AttachesOnlyWhileTheEntryHeadsTheGenerationItMeasured()
    {
        WriteHead();
        Assert.Null(ReadEntry().UnloadableFrameworkMvid);

        ModuleActivationSidecar.SetUnloadable(root, Module, Head, HeadMvid, "missing type");
        Assert.Equal(HeadMvid, ReadEntry().UnloadableFrameworkMvid);

        ModuleActivationSidecar.SetUnloadable(root, Module, Module + "@stale000", HeadMvid, "missing type");
        Assert.Null(ReadEntry().UnloadableFrameworkMvid);
        Assert.NotNull(ModuleActivationSidecar.ReadUnloadable(root, Module));
    }

    /// <summary>🚨 The identity is derived, never persisted: an entry written WITH the field reads
    /// back without it unless the marker file says so, and a marker without an identity attaches
    /// nothing (an unknown identity cannot be compared).</summary>
    [Fact]
    public void TheEntryFile_NeverCarriesTheMarker()
    {
        ModuleActivationSidecar.WriteEntry(root, new ModuleActivationEntry
        {
            Name = Module, Directory = Head, FrameworkMvid = HeadMvid, UnloadableFrameworkMvid = HeadMvid,
        });
        Assert.Null(ReadEntry().UnloadableFrameworkMvid);
        Assert.DoesNotContain("nloadable", File.ReadAllText(ModuleActivationSidecar.EntryPath(root, Module)),
            StringComparison.Ordinal);

        ModuleActivationSidecar.SetUnloadable(root, Module, Head, frameworkMvid: null, "missing type");
        Assert.Null(ReadEntry().UnloadableFrameworkMvid);
    }

    /// <summary>A marker is not an entry: the enumeration of entry files never sees it, so a
    /// deployment with markers reads exactly its modules.</summary>
    [Fact]
    public void AMarker_IsNeverReadAsAnEntry()
    {
        WriteHead();
        ModuleActivationSidecar.SetUnloadable(root, Module, Head, HeadMvid, "missing type");
        var corrupt = 0;

        var list = ModuleActivationSidecar.Read(root, _ => corrupt++);

        Assert.Single(list.Entries);
        Assert.Equal(0, corrupt);
    }

    [Fact]
    public void ClearUnloadable_RemovesIt_AndTolerates_Absence()
    {
        WriteHead();
        ModuleActivationSidecar.SetUnloadable(root, Module, Head, HeadMvid, "missing type");
        ModuleActivationSidecar.ClearUnloadable(root, Module);
        ModuleActivationSidecar.ClearUnloadable(root, Module);

        Assert.Null(ModuleActivationSidecar.ReadUnloadable(root, Module));
        Assert.Null(ReadEntry().UnloadableFrameworkMvid);
    }

    /// <summary>The measurement: a fallback or a refusal naming the TRIED generation says
    /// unloadable; a record naming another generation of the same module says nothing.</summary>
    [Fact]
    public void MeasureLoadability_ReadsTheLoadersRecordsOntoTheTriedGeneration()
    {
        var tried = new ModuleActivationEntry { Name = Module, Directory = Head, FrameworkMvid = HeadMvid };
        var fellBack = new FallbackModule(Module, $"/pin/{Head}/{Module}.dll", $"/pin/{Previous}/{Module}.dll", "no type");
        var parked = new IncompatibleModule($"/pin/{Head}/{Module}.dll", Module, "no type either", null);
        var elsewhere = new FallbackModule(Module, $"/pin/{Module}@other000/{Module}.dll", $"/pin/{Previous}/{Module}.dll", "no type");

        var viaFallback = Assert.Single(ModuleActivationBoot.MeasureLoadability([tried], [fellBack], []));
        Assert.True(viaFallback.Unloadable);
        Assert.Equal("no type", viaFallback.Reason);
        Assert.Equal(HeadMvid, viaFallback.FrameworkMvid);

        var viaRefusal = Assert.Single(ModuleActivationBoot.MeasureLoadability([tried], [], [parked]));
        Assert.True(viaRefusal.Unloadable);
        Assert.Equal("no type either", viaRefusal.Reason);

        var loaded = Assert.Single(ModuleActivationBoot.MeasureLoadability([tried], [elsewhere], []));
        Assert.False(loaded.Unloadable);

        // A disabled entry and one without a generation are not measured at all.
        Assert.Empty(ModuleActivationBoot.MeasureLoadability(
            [tried with { Enabled = false }, tried with { Directory = null }], [fellBack], [parked]));
    }

    /// <summary>Only transitions write: the same measurement twice costs nothing, a changed one
    /// rewrites, a loaded head clears — and each transition is reported once.</summary>
    [Fact]
    public void RecordMeasuredLoadability_WritesOnTransitionsOnly()
    {
        WriteHead();
        var tried = new[] { ReadEntry() };
        var fellBack = new FallbackModule(Module, $"/pin/{Head}/{Module}.dll", $"/pin/{Previous}/{Module}.dll", "no type");
        var reports = 0;

        var first = ModuleActivationBoot.RecordMeasuredLoadability(root, tried, [fellBack], [], _ => reports++);
        Assert.Single(first);
        Assert.Equal(HeadMvid, ReadEntry().UnloadableFrameworkMvid);

        var again = ModuleActivationBoot.RecordMeasuredLoadability(root, tried, [fellBack], [], _ => reports++);
        Assert.Empty(again);

        var cleared = ModuleActivationBoot.RecordMeasuredLoadability(root, tried, [], [], _ => reports++);
        Assert.Single(cleared);
        Assert.False(cleared[0].Unloadable);
        Assert.Null(ReadEntry().UnloadableFrameworkMvid);

        Assert.Empty(ModuleActivationBoot.RecordMeasuredLoadability(root, tried, [], [], _ => reports++));
        Assert.Equal(2, reports);
    }

    /// <summary>
    /// 🚨 <b>THE THIRD ANSWER (MeshWeaver#3911): a boot that did not LOOK writes nothing and clears
    /// nothing.</b> When the default load context already held the module's simple name,
    /// <c>Assembly.LoadFrom</c> handed back that copy and the head generation's bytes never reached
    /// the loader — so this boot has no verdict on them.
    ///
    /// <para><b>Both collapses are wrong, in opposite directions, and each is asserted here.</b>
    /// Reading the fallback as UNLOADABLE writes a marker that makes the update reconcile
    /// permanently <c>SkipUnloadable</c> every rebuild of a version nobody executed; reading it as
    /// LOADED clears a marker an earlier boot wrote from a real measurement. The two assertions
    /// below are that pair — a not-measured boot over a clean sidecar leaves it clean, and over an
    /// existing marker leaves the marker exactly as it found it.</para>
    /// </summary>
    [Fact]
    public void ABootThatNeverReachedTheHeadsBytes_NeitherWritesNorClearsItsMarker()
    {
        WriteHead();
        var tried = new[] { ReadEntry() };
        var substituted = new FallbackModule(
            Module, $"/pin/{Head}/{Module}.dll", $"/pin/{Previous}/{Module}.dll",
            "this process had already loaded an assembly of that name")
        {
            RunsAlreadyLoadedCopy = true,
        };

        // The measurement says so in as many words: not unloadable, and not a measurement.
        var verdict = Assert.Single(ModuleActivationBoot.MeasureLoadability([tried[0]], [substituted], []));
        Assert.True(verdict.HeadNotMeasured);
        Assert.False(verdict.Unloadable,
            "nothing executed these bytes, so calling them unloadable would permanently skip every rebuild");

        // Direction 1 — nothing to clear: the boot writes no marker.
        var overClean = ModuleActivationBoot.RecordMeasuredLoadability(root, tried, [substituted], []);
        Assert.Empty(overClean);
        Assert.Null(ReadEntry().UnloadableFrameworkMvid);

        // Direction 2 — a marker a REAL measurement wrote survives untouched.
        var measured = new FallbackModule(
            Module, $"/pin/{Head}/{Module}.dll", $"/pin/{Previous}/{Module}.dll", "no type");
        Assert.Single(ModuleActivationBoot.RecordMeasuredLoadability(root, tried, [measured], []));
        Assert.Equal(HeadMvid, ReadEntry().UnloadableFrameworkMvid);

        var overMarked = ModuleActivationBoot.RecordMeasuredLoadability(root, tried, [substituted], []);
        Assert.Empty(overMarked);
        Assert.Equal(HeadMvid, ReadEntry().UnloadableFrameworkMvid);

        // 🚨 The negative control on the control: the SAME sidecar, the SAME entry, one boot that
        // genuinely measured the head as loading — and the marker goes. Without this the two
        // assertions above would also pass if RecordMeasuredLoadability had simply stopped
        // clearing markers at all.
        Assert.Single(ModuleActivationBoot.RecordMeasuredLoadability(root, tried, [], []));
        Assert.Null(ReadEntry().UnloadableFrameworkMvid);
    }
}
