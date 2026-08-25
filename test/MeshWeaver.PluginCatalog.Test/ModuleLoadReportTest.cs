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
    /// 🚨 THE deliverable. The exact prod topology: a baseline <c>Modules:Assemblies</c> entry, an
    /// older image copy, and a NEWER landed generation carrying different bytes. The image copy
    /// still wins (that resolution is unchanged and deliberate) — but the report now NAMES it and
    /// says out loud that a newer, different copy was passed over.
    /// </summary>
    [Fact]
    public void A_newer_landed_generation_that_loses_to_the_image_copy_is_reported_as_stale()
    {
        var image = PlacePack(imageRoot, PackName, OldBytes, new DateTime(2026, 8, 25, 10, 35, 0, DateTimeKind.Utc));
        var landed = PlacePack(storeRoot, $"{PackName}@12d2c7c2", NewBytes, new DateTime(2026, 8, 25, 13, 53, 0, DateTimeKind.Utc));

        // The baseline claims the name, so the sidecar entry for the SAME module is deduped away —
        // silently, by design (ModuleActivationBootTest.PersistedDuplicateOfBaseline_…). That is
        // exactly how the landed generation stops being consulted.
        var resolved = ResolveAsBootDoes(
            [PackName + ".dll"],
            new ModuleActivationList
            {
                Entries = [new ModuleActivationEntry { Name = PackName, Directory = $"{PackName}@12d2c7c2" }],
            });
        // The store's landed probe looks in modules/<name>/, which a generation landing never
        // writes, so resolution falls through to the image copy. Pin it — if this ever changes, the
        // report below is describing a different world.
        resolved.Should().ContainSingle();
        resolved[0].Path.Should().NotBe(landed);

        // The report describes the path the loader was handed — here, forced to the image copy the
        // prod pod actually mapped.
        var lines = ModuleLoadReport.Describe(storeRoot, [(resolved[0].Module, image)]);

        var line = lines.Should().ContainSingle().Subject;
        line.Name.Should().Be(PackName);
        line.Source.Should().Be(ModuleActivationSources.AppSettings);
        line.Path.Should().Be(image);
        line.Mvid.Should().NotBeNull("the line must carry the identity of the bytes, not just a path");
        line.Shadowed.Should().NotBeNull(
            "a newer, different copy in the module store is #2223 — nothing said so, and the fix "
            + "shipped end-to-end without ever running");
        line.Shadowed!.Path.Should().Be(landed);
        line.Shadowed.Mvid.Should().NotBe(line.Mvid);

        var warnings = Render(lines).Warnings;
        warnings.Should().ContainSingle().Which.Should().Contain(PackName).And.Contain(landed);
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

    private static (System.Collections.Generic.List<string> Info, System.Collections.Generic.List<string> Warnings)
        Render(System.Collections.Generic.IEnumerable<ModuleLoadLine> lines)
    {
        System.Collections.Generic.List<string> info = [];
        System.Collections.Generic.List<string> warnings = [];
        ModuleLoadReport.Write(lines, info.Add, warnings.Add);
        return (info, warnings);
    }
}
