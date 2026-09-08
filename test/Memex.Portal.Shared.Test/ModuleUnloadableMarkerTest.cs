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
}
