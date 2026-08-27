#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// #2223 — <b>a view-pack fix can merge, build, land in the module store, and the portal still
/// serves the older copy, with every lane green.</b>
///
/// <para>The measurement (memex-cloud, 2026-08-25): the process had memory-mapped
/// <c>/app/modules/MeshWeaver.Blazor.Views/MeshWeaver.Blazor.Views.dll</c> — which did not contain
/// the merged fix — while <c>/data/modules/MeshWeaver.Blazor.Views@12d2c7c2/</c> and
/// <c>@3badbda5/</c> both did. So the repro below is the SHAPE of that pod's disk, and the
/// assertions are the two things nothing said at the time: which file is being loaded, and that a
/// newer different one was passed over.</para>
///
/// <para>🚨 These tests pin a WARNING, never a refusal. Which copy ought to win is a policy question
/// still open on the issue; a boot that dies on the answer cannot be given the module that fixes it.</para>
/// </summary>
public class ModuleLoadReportTest : IDisposable
{
    private const string PackName = "MeshWeaver.Blazor.Views";

    // Two REAL assemblies, so the MVIDs are real and genuinely different. The report reads identity
    // out of PE metadata, which a hand-made byte array does not have.
    private static readonly string OldBytes = typeof(FactAttribute).Assembly.Location;
    private static readonly string NewBytes = typeof(ModuleLoadReport).Assembly.Location;

    private readonly string imageRoot = Path.Combine(
        Path.GetTempPath(), $"mw-2223-image-{Guid.NewGuid():N}");
    private readonly string storeRoot = Path.Combine(
        Path.GetTempPath(), $"mw-2223-store-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(imageRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(storeRoot, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string PlacePack(string root, string directory, string sourceDll, DateTime writtenUtc)
    {
        var dir = Path.Combine(root, "modules", directory);
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, PackName + ".dll");
        File.Copy(sourceDll, dll, overwrite: true);
        File.SetLastWriteTimeUtc(dll, writtenUtc);
        return dll;
    }

    /// <summary>
    /// Resolve exactly the way boot does: the appsettings baseline ∪ the sidecar, deduped by name,
    /// then <see cref="ModuleActivationBoot.ResolveLoadPath"/> per entry.
    /// </summary>
    private (EffectiveModule Module, string Path)[] ResolveAsBootDoes(
        string[] baseline, ModuleActivationList sidecar)
    {
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            baseline, sidecar, _ => null,
            entry => File.Exists(ModuleActivationBoot.LandedDllPath(storeRoot, entry)));
        return effective
            .Select(m => (Module: m, Path: ModuleActivationBoot.ResolveLoadPath(storeRoot, m)))
            .ToArray();
    }

    /// <summary>
    /// 🚨 #2548 CHANGED WHO WINS HERE, and this test now pins the new answer. The topology is the
    /// prod one: a baseline <c>Modules:Assemblies</c> entry, an older image copy, and a NEWER
    /// landed generation carrying different bytes.
    ///
    /// <para>Before: the baseline claimed the name, the sidecar entry was deduped away, and the
    /// image copy loaded — the report's whole job was to say out loud that a newer copy had been
    /// passed over. Now the usable store entry OVERRIDES the baseline, so the landed generation is
    /// what loads and there is nothing shadowed to report.</para>
    ///
    /// <para>The report itself is unchanged and still needed: it fires whenever a baseline entry is
    /// genuinely the one loading — which, after #2548, means no usable store entry exists (covered
    /// by <see cref="A_baseline_that_wins_because_no_usable_store_entry_exists_is_still_reported"/>).</para>
    /// </summary>
    [Fact]
    public void A_usable_landed_generation_now_WINS_over_the_image_copy()
    {
        var image = PlacePack(imageRoot, PackName, OldBytes, new DateTime(2026, 8, 25, 10, 35, 0, DateTimeKind.Utc));
        var landed = PlacePack(storeRoot, $"{PackName}@12d2c7c2", NewBytes, new DateTime(2026, 8, 25, 13, 53, 0, DateTimeKind.Utc));

        var resolved = ResolveAsBootDoes(
            [PackName + ".dll"],
            new ModuleActivationList
            {
                Entries = [new ModuleActivationEntry { Name = PackName, Directory = $"{PackName}@12d2c7c2" }],
            });

        resolved.Should().ContainSingle();
        resolved[0].Path.Should().Be(landed,
            "#2548: a usable store entry overrides the same-named baseline, so the LANDED "
            + "generation is what the loader is handed — not the older image copy it used to fall "
            + "through to");
        resolved[0].Module.Landed.Should().NotBeNull(
            "the winning entry must carry its landed pointer, or resolution has no generation to "
            + "aim at and the override is cosmetic");

        // Nothing was passed over, so there is nothing to warn about.
        var lines = ModuleLoadReport.Describe(storeRoot, [(resolved[0].Module, resolved[0].Path)]);
        var line = lines.Should().ContainSingle().Subject;
        line.Name.Should().Be(PackName);
        line.Source.Should().Be(ModuleActivationSources.Store);
        line.Path.Should().Be(landed);
        line.Shadowed.Should().BeNull(
            "the newer copy is the one loading now; reporting it as shadowed would name a problem "
            + "that no longer exists");

        Render(lines).Warnings.Should().BeEmpty();
        image.Should().NotBe(landed, "the fixture must actually have two distinct copies, or this "
            + "test would pass without discriminating anything");
    }

    /// <summary>
    /// The report's remaining job after #2548, and the case its remediation text now describes: a
    /// baseline entry that is loading because NO usable store entry claims the name, while a newer
    /// generation sits on disk. The warning still has to fire — and it must no longer tell the
    /// operator to delist the baseline, which is now the only copy that loads.
    /// </summary>
    [Fact]
    public void A_baseline_that_wins_because_no_usable_store_entry_exists_is_still_reported()
    {
        var image = PlacePack(imageRoot, PackName, OldBytes, new DateTime(2026, 8, 25, 10, 35, 0, DateTimeKind.Utc));
        var landed = PlacePack(storeRoot, $"{PackName}@12d2c7c2", NewBytes, new DateTime(2026, 8, 25, 13, 53, 0, DateTimeKind.Utc));

        // No sidecar entry at all — nothing can override, so the baseline legitimately wins.
        var resolved = ResolveAsBootDoes([PackName + ".dll"], new ModuleActivationList());
        var lines = ModuleLoadReport.Describe(storeRoot, [(resolved[0].Module, image)]);

        var line = lines.Should().ContainSingle().Subject;
        line.Source.Should().Be(ModuleActivationSources.AppSettings);
        line.Shadowed.Should().NotBeNull("a newer, different copy is sitting unused — #2223");
        line.Shadowed!.Path.Should().Be(landed);

        var warning = Render(lines).Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain(PackName).And.Contain(landed);
        warning.Should().NotContain("delist it from Modules:Assemblies",
            "since #2548 that instruction would remove the only copy that loads rather than promote "
            + "the newer one — a warning that names the wrong fix is worse than one that names none");
        warning.Should().Contain("do NOT delist the baseline",
            "the replacement must say so explicitly: an operator who read the old advice, or who "
            + "reasons the old way, needs the instruction contradicted, not merely absent");
    }

    /// <summary>
    /// 🚨 The acceptance the issue asked for, expressed the way a test can: the path in the boot line
    /// is the path whose BYTES the runtime maps. On the pod that comparison is
    /// <c>/proc/1/maps</c> vs the line; here it is the load context's own answer for the same file.
    /// A report that named a path the loader did not use would be worse than no report.
    /// </summary>
    [Fact]
    public void The_reported_path_is_the_file_the_runtime_actually_loads()
    {
        var image = PlacePack(imageRoot, PackName, OldBytes, new DateTime(2026, 8, 25, 10, 35, 0, DateTimeKind.Utc));
        PlacePack(storeRoot, $"{PackName}@12d2c7c2", NewBytes, new DateTime(2026, 8, 25, 13, 53, 0, DateTimeKind.Utc));

        var resolved = ResolveAsBootDoes([PackName + ".dll"], new ModuleActivationList());
        var lines = ModuleLoadReport.Describe(storeRoot, [(resolved[0].Module, image)]);

        // Collectible, so this test loads the bytes without pinning them in the default context the
        // way InstallAssemblies would.
        var context = new AssemblyLoadContext("mw-2223-probe", isCollectible: true);
        try
        {
            var loaded = context.LoadFromAssemblyPath(lines[0].Path);
            loaded.Location.Should().Be(lines[0].Path,
                "the boot line must name the file that is mapped — that equality IS the assertion "
                + "#2223 asked for");
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// The same pack published to two places is NORMAL — the image ships a baseline copy and the
    /// store lands the same version. Warning on that would make the line noise, and an operator who
    /// learns to scroll past it has lost the whole signal. The warning is gated on the bytes
    /// DIFFERING, not on there being two of them.
    /// </summary>
    [Fact]
    public void An_identical_copy_in_the_store_is_not_a_stale_pack()
    {
        var image = PlacePack(imageRoot, PackName, NewBytes, new DateTime(2026, 8, 25, 10, 35, 0, DateTimeKind.Utc));
        PlacePack(storeRoot, $"{PackName}@12d2c7c2", NewBytes, new DateTime(2026, 8, 25, 13, 53, 0, DateTimeKind.Utc));

        var resolved = ResolveAsBootDoes([PackName + ".dll"], new ModuleActivationList());
        var lines = ModuleLoadReport.Describe(storeRoot, [(resolved[0].Module, image)]);

        lines[0].Shadowed.Should().BeNull("same MVID = the same bytes in two places, which is not a defect");
        Render(lines).Warnings.Should().BeEmpty();
    }

    /// <summary>
    /// A superseded generation still on the volume is history, not a stale pack: it is OLDER than
    /// what is loaded. Only a copy that is newer AND different means the portal is behind.
    /// </summary>
    [Fact]
    public void An_older_copy_in_the_store_is_not_a_stale_pack()
    {
        var image = PlacePack(imageRoot, PackName, NewBytes, new DateTime(2026, 8, 25, 13, 53, 0, DateTimeKind.Utc));
        PlacePack(storeRoot, $"{PackName}@old", OldBytes, new DateTime(2026, 8, 25, 10, 35, 0, DateTimeKind.Utc));

        var resolved = ResolveAsBootDoes([PackName + ".dll"], new ModuleActivationList());
        var lines = ModuleLoadReport.Describe(storeRoot, [(resolved[0].Module, image)]);

        lines[0].Shadowed.Should().BeNull();
        Render(lines).Warnings.Should().BeEmpty();
    }

    /// <summary>
    /// The store lane, working as intended: no baseline entry claims the name, so the sidecar entry
    /// survives the dedupe and resolves to ITS generation directory — which is then what the report
    /// names, with nothing shadowed.
    /// </summary>
    [Fact]
    public void A_store_landed_module_reports_its_own_generation_and_nothing_shadowed()
    {
        var landed = PlacePack(storeRoot, $"{PackName}@12d2c7c2", NewBytes, new DateTime(2026, 8, 25, 13, 53, 0, DateTimeKind.Utc));

        var resolved = ResolveAsBootDoes(
            [],
            new ModuleActivationList
            {
                Entries = [new ModuleActivationEntry { Name = PackName, Directory = $"{PackName}@12d2c7c2" }],
            });

        resolved.Should().ContainSingle();
        resolved[0].Path.Should().Be(landed);

        var lines = ModuleLoadReport.Describe(storeRoot, resolved);
        lines[0].Source.Should().Be(ModuleActivationSources.Store);
        lines[0].Path.Should().Be(landed);
        lines[0].Shadowed.Should().BeNull("nothing newer was passed over — this IS the landed copy");

        var rendered = Render(lines);
        rendered.Warnings.Should().BeEmpty();
        rendered.Info.Should().ContainSingle().Which.Should().Contain(landed);
    }

    /// <summary>
    /// 🚨 The remediation must match the LANE, because the two lanes are shadowed for different
    /// reasons — and a warning that names the wrong fix is worse than one that names none. A
    /// store-installed module is not listed in <c>Modules:Assemblies</c> at all, so "delist it"
    /// sends the operator hunting for a line that does not exist; what decides its generation is
    /// the sidecar's own <c>Directory</c> pointer.
    ///
    /// <para>Reachable in practice: landing writes a fresh generation and THEN moves the pointer,
    /// so a landing whose pointer write did not land leaves exactly this — newer bytes on disk,
    /// an entry still naming the previous generation. (Boot's GC would normally collect an
    /// unreferenced generation, but it is skip-on-locked.)</para>
    /// </summary>
    [Fact]
    public void The_remediation_names_the_lane_that_actually_decides_which_generation_loads()
    {
        var older = PlacePack(storeRoot, $"{PackName}@old", OldBytes, new DateTime(2026, 8, 25, 10, 35, 0, DateTimeKind.Utc));
        var newer = PlacePack(storeRoot, $"{PackName}@new", NewBytes, new DateTime(2026, 8, 25, 13, 53, 0, DateTimeKind.Utc));

        // A STORE entry still pointing at the older generation while newer bytes sit beside it.
        var resolved = ResolveAsBootDoes(
            [],
            new ModuleActivationList
            {
                Entries = [new ModuleActivationEntry { Name = PackName, Directory = $"{PackName}@old" }],
            });
        resolved[0].Path.Should().Be(older);

        var storeLines = ModuleLoadReport.Describe(storeRoot, resolved);
        storeLines[0].Source.Should().Be(ModuleActivationSources.Store);
        storeLines[0].Shadowed!.Path.Should().Be(newer);

        var storeWarning = Render(storeLines).Warnings.Should().ContainSingle().Subject;
        storeWarning.Should().Contain("activation.json")
            .And.Contain("Re-install the module");
        storeWarning.Should().NotContain("delist it from Modules:Assemblies",
            "a store-installed module has no Modules:Assemblies line to delist");

        // …and the BASELINE lane still gets a DIFFERENT remediation — but no longer the old one.
        // 🚨 Until #2548 this said "delist it from Modules:Assemblies to let the landed generation
        // win", which was true only while a baseline entry shadowed the store entry by name. Now a
        // usable store entry overrides the baseline, so reaching this branch means no usable store
        // entry exists — and delisting would remove the only copy that loads.
        var baselineLines = ModuleLoadReport.Describe(
            storeRoot, [(new EffectiveModule(PackName + ".dll", null), older)]);
        var baselineWarning = Render(baselineLines).Warnings.Should().ContainSingle().Subject;
        baselineWarning.Should().NotContain("delist it from Modules:Assemblies",
            "that instruction removes the fallback instead of promoting the newer copy (#2548)");
        baselineWarning.Should().Contain("do NOT delist the baseline");
        baselineWarning.Should().Contain("Re-install the module");
        baselineWarning.Should().NotContain("activation.json");
    }

    private static (System.Collections.Generic.List<string> Info, System.Collections.Generic.List<string> Warnings)
        Render(System.Collections.Generic.IEnumerable<ModuleLoadLine> lines)
    {
        System.Collections.Generic.List<string> info = [];
        System.Collections.Generic.List<string> warnings = [];
        ModuleLoadReport.Write(lines, info.Add, warnings.Add);
        return (info, warnings);
    }
}
