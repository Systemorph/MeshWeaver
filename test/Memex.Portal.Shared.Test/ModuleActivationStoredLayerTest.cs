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
/// #4026 — Copilot's review of #4438: the STORED layer (the legacy aggregate and the per-module
/// entry files) is an input to the derivation like any record, so it must be read WHOLE before
/// anything is derived, projected or tombstoned from it; and an older image's event needs an
/// identity an uninstall can name, or a timestamp tie decides the order by accident.
/// </summary>
public class ModuleActivationStoredLayerTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.StoredLayer";
    private const string MarkerAsset = "wwwroot/build.txt";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-activation-stored-layer-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the landing root.</summary>
    public ModuleActivationStoredLayerTest() => Directory.CreateDirectory(root);

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
                Module, assemblies, frameworkMvid: "test-build", packagePath: "Plugins/StoredLayer",
                version: version, staticAssets: assets)
            .Timeout(TestTimeouts.Convergence).Await();
    }

    private ModuleLandingService Replica() => new(null, root, beforeRecording: null);

    private int Tombstones() =>
        Directory.GetFiles(ModuleActivationSidecar.LandingRecordsDirectory(root, Module),
            "*" + ModuleActivationSidecar.UninstallSuffix).Length;

    // ── 1. a stored-layer fault makes the modules it can hide not-whole ───────────────────

    /// <summary>
    /// 🚨 The legacy aggregate cannot be read, and this module has no per-module entry file — so
    /// its stored entry may be IN the aggregate, and nothing that reads the stored layer knows what
    /// it says. Before this change only an unreadable per-module file counted: the module was
    /// derived from its records as if the aggregate said nothing, and an uninstall wrote its
    /// tombstone over that partial read. Now the module is dropped from the read, reported, and an
    /// uninstall refuses — no tombstone.
    /// </summary>
    [Fact]
    public async Task AnUnreadableAggregate_ThatCouldHoldAModulesOnlyEntry_DropsIt_AndNothingIsWrittenOverTheRead()
    {
        using var replica = Replica();
        await Shelve(replica, "1.7.0");
        File.Delete(ModuleActivationSidecar.EntryPath(root, Module));
        File.WriteAllText(ModuleActivationSidecar.SidecarPath(root), "{ this is not an activation list");
        var faults = new List<string>();

        var read = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.DoesNotContain(read.Entries, e => e.Name == Module);
        Assert.Contains(faults, f => f.Contains(Module, StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => replica.RemoveCore(Module));
        Assert.Equal(0, Tombstones());
    }

    /// <summary>
    /// The control that keeps the rule precise rather than total: a module WITH its own per-module
    /// file cannot be hidden by the aggregate — that file wins by name — so an unreadable aggregate
    /// (which is never rewritten, and could stay unreadable for good) must not drop it.
    /// </summary>
    [Fact]
    public async Task AnUnreadableAggregate_CannotHideAModuleWithItsOwnEntryFile()
    {
        using var replica = Replica();
        await Shelve(replica, "1.7.0");
        File.WriteAllText(ModuleActivationSidecar.SidecarPath(root), "{ this is not an activation list");

        var entry = Assert.Single(ModuleActivationSidecar.Read(root).Entries, e => e.Name == Module);

        Assert.True(entry.Enabled);
        Assert.Equal("1.7.0", entry.Version);
        replica.RemoveCore(Module);
        Assert.Equal(1, Tombstones());
    }

    // ── 2. an uninstall that observed an older image's install wins a timestamp tie ─────────

    /// <summary>
    /// 🚨 A current image uninstalls a module an OLDER image installed, and the file server stamps
    /// the tombstone with the same tick as that image's entry file. The uninstall observed the
    /// install — so the install precedes it and the module is uninstalled. Before this change an
    /// older-image event had no identity the uninstall's <c>after</c> could name, and at a tie it
    /// counted as AFTER the uninstall: the module stayed enabled.
    /// </summary>
    [Fact]
    public async Task AnUninstallThatObservedAnOlderImagesInstall_PrecedesItAtATimestampTie()
    {
        using var current = Replica();
        await Shelve(current, "1.7.0");
        var landed = Assert.Single(ModuleActivationSidecar.Read(root).Entries, e => e.Name == Module);
        // An older image installs 1.8.0: its bytes, and its per-module entry with no projectionOf.
        var (assemblies, assets) = Bundle("1.8.0");
        var generation = $"{Module}@{ModuleLandingService.GenerationIdOf(assemblies, assets)}";
        var directory = Path.Combine(root, "modules", generation);
        Directory.CreateDirectory(Path.Combine(directory, "wwwroot"));
        File.WriteAllBytes(Path.Combine(directory, Module + ".dll"), assemblies[0].Item2);
        File.WriteAllBytes(Path.Combine(directory, "wwwroot", "build.txt"), assets[0].Item2);
        ModuleActivationSidecar.WriteEntry(root, landed with
        {
            Directory = generation,
            Version = "1.8.0",
            PreviousDirectory = landed.Directory,
            PreviousVersion = landed.Version,
            ProjectionOf = null,
        });
        var installedAt = File.GetLastWriteTimeUtc(ModuleActivationSidecar.EntryPath(root, Module));
        Assert.Equal("1.8.0", Assert.Single(ModuleActivationSidecar.Read(root).Entries, e => e.Name == Module).Version);

        current.RemoveCore(Module);
        // The file server stamps the tombstone in the very tick the older image's entry was written.
        var tombstone = Assert.Single(Directory.GetFiles(
            ModuleActivationSidecar.LandingRecordsDirectory(root, Module), "*" + ModuleActivationSidecar.UninstallSuffix));
        File.SetLastWriteTimeUtc(tombstone, installedAt);
        Assert.Equal(installedAt, File.GetLastWriteTimeUtc(tombstone));

        var entry = Assert.Single(ModuleActivationSidecar.Read(root).Entries, e => e.Name == Module);
        Assert.False(entry.Enabled, "the uninstall observed the older image's install, so it follows it — tie or not");
        Assert.Null(entry.Directory);
    }

    // ── 3. an entry file that does not hold the entry its name addresses is corrupt ─────────

    /// <summary>
    /// 🚨 A per-module entry file that deserializes to nothing (JSON <c>null</c>), to an entry with
    /// no name, or to an entry naming a DIFFERENT module is not the entry its name addresses — the
    /// record readers already treat exactly that as corrupt. It used to fall through silently: the
    /// module was derived from its records as if the file were absent (and the misaddressed entry
    /// even surfaced as a module of its own). Now it is reported and keyed by the FILE's name, so
    /// the module that has records is dropped.
    /// </summary>
    [Theory]
    [InlineData("json-null")]
    [InlineData("blank-name")]
    [InlineData("misaddressed")]
    public async Task AnEntryFileNotHoldingItsOwnEntry_IsCorrupt_AndDropsTheModule(string shape)
    {
        using var replica = Replica();
        await Shelve(replica, "1.7.0");
        var entryFile = ModuleActivationSidecar.EntryPath(root, Module);
        File.WriteAllText(entryFile, shape switch
        {
            "json-null" => "null",
            "blank-name" => """{ "name": "", "directory": "x@0123456789abcdef", "enabled": true }""",
            _ => JsonSerializer.Serialize(
                new ModuleActivationEntry { Name = "MeshWeaver.Test.SomeOtherModule", Directory = "x@0123456789abcdef" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        var faults = new List<string>();

        var read = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.DoesNotContain(read.Entries, e => e.Name == Module);
        Assert.DoesNotContain(read.Entries, e => e.Name == "MeshWeaver.Test.SomeOtherModule");
        Assert.Contains(faults, f => f.Contains(Module, StringComparison.Ordinal));
    }
}
