#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The boot-time effective-set computation of #1664 Slice A, step 9 —
/// <see cref="ModuleActivationBoot.ComputeEffectiveModuleEntries"/>, pure: appsettings baseline ∪
/// enabled persisted store installs, deduped by name, with the two loud skip rules (unsatisfied
/// platform floor, missing DLL) that keep a platform rollback or a lost volume from crashing
/// boot. Plus the sidecar's read/write round-trip and its corrupt-file loudness.
/// </summary>
public class ModuleActivationBootTest
{
    private static readonly Func<string?, string?> AcceptAll = _ => null;
    private static readonly Func<string, bool> AllDllsExist = _ => true;

    private static ModuleActivationList List(params ModuleActivationEntry[] entries) =>
        new() { Entries = [.. entries] };

    private static ModuleActivationEntry Store(
        string name, bool enabled = true, string? floor = null) =>
        new() { Name = name, FrameworkMvid = "m1", MinMeshVersion = floor, Enabled = enabled };

    [Fact]
    public void EffectiveSet_IsBaselineUnionEnabledPersisted_InOrder()
    {
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            ["MeshWeaver.OgCard.dll", "MeshWeaver.Speech.dll"],
            List(Store("Acme.Widgets"), Store("Acme.Reports")),
            AcceptAll, AllDllsExist);

        effective.Should().Equal(
            "MeshWeaver.OgCard.dll", "MeshWeaver.Speech.dll",
            "Acme.Widgets.dll", "Acme.Reports.dll");
    }

    [Fact]
    public void PersistedDuplicateOfBaseline_IsDeduplicatedByName_CaseInsensitive()
    {
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            ["MeshWeaver.OgCard.dll"],
            List(Store("meshweaver.ogcard"), Store("Acme.Widgets"), Store("ACME.WIDGETS")),
            AcceptAll, AllDllsExist);

        // A store install of an already-baseline module (any casing) must not double-load it.
        effective.Should().Equal("MeshWeaver.OgCard.dll", "Acme.Widgets.dll");
    }

    [Fact]
    public void DisabledPersistedEntry_ContributesNothing()
    {
        var skips = new List<(string Module, string Reason)>();
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            [],
            List(Store("Acme.Widgets", enabled: false)),
            AcceptAll, AllDllsExist,
            (m, r) => skips.Add((m, r)));

        effective.Should().BeEmpty();
        skips.Should().BeEmpty("an uninstall is a deliberate state, not a problem to report");
    }

    [Fact]
    public void UnsatisfiedPlatformFloor_SkipsWithLoudReason_NeverCrashes()
    {
        // The PRODUCTION gate (ModulePlatformFloor.DeclineReason), bound to a fixed running
        // version — the platform rolled BACK below one module's declared requirement. Deliberately
        // a FLOOR and not an MVID check: modules bind by simple name, so a landed module keeps
        // loading across ordinary platform updates; only a rollback below its floor skips it.
        var skips = new List<(string Module, string Reason)>();
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            ["MeshWeaver.OgCard.dll"],
            List(Store("Acme.Widgets", floor: "9.0.0"), Store("Acme.Reports", floor: "1.0.0")),
            floor => ModulePlatformFloor.DeclineReason(floor, "3.2.0"),
            AllDllsExist,
            (m, r) => skips.Add((m, r)));

        effective.Should().Equal("MeshWeaver.OgCard.dll", "Acme.Reports.dll");
        var skip = skips.Should().ContainSingle().Subject;
        skip.Module.Should().Be("Acme.Widgets");
        skip.Reason.Should().Contain("9.0.0").And.Contain("3.2.0",
            "the skip must name both versions — the entry stays for when the platform moves forward");
    }

    [Fact]
    public void NoRecordedFloor_Loads_AbsenceIsNoConstraint()
    {
        // An entry landed without a declared minMeshVersion (most modules; every entry written
        // before the field existed) has stated no requirement — the production gate reads absence
        // as satisfied, so such landings keep loading across platform builds. The recorded MVID
        // is diagnostic and deliberately not consulted.
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            [],
            List(Store("Acme.Widgets")),
            floor => ModulePlatformFloor.DeclineReason(floor, "3.2.0"),
            AllDllsExist);

        effective.Should().Equal("Acme.Widgets.dll");
    }

    [Fact]
    public void MissingDll_SkipsWithLoudReason_BaselineUnaffected()
    {
        var skips = new List<(string Module, string Reason)>();
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            ["MeshWeaver.OgCard.dll"],
            List(Store("Acme.Widgets"), Store("Acme.Reports")),
            AcceptAll,
            name => name != "Acme.Widgets",
            (m, r) => skips.Add((m, r)));

        effective.Should().Equal("MeshWeaver.OgCard.dll", "Acme.Reports.dll");
        var skip = skips.Should().ContainSingle().Subject;
        skip.Module.Should().Be("Acme.Widgets");
        skip.Reason.Should().Contain("Acme.Widgets.dll");
    }

    // ---- The landed-DLL check is modules-folder-SPECIFIC (Copilot review on PR #1668) ---------
    //
    // ResolveModulePath falls back to AppContext.BaseDirectory, which is correct for baseline
    // entries but would let a sidecar entry whose modules/<name>/ folder is gone silently BIND a
    // same-named app-closure DLL instead of being skipped. These pins exercise the PRODUCTION
    // check (LandedModuleDllExists) against real files.

    [Fact]
    public void SidecarEntry_WithLandedModulesFolderDll_IsIncluded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-landed-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "modules", "Acme.Widgets"));
            File.WriteAllBytes(
                ModuleActivationBoot.LandedDllPath(dir, "Acme.Widgets"), [1]);

            var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
                [],
                List(Store("Acme.Widgets")),
                AcceptAll,
                name => ModuleActivationBoot.LandedModuleDllExists(dir, name));

            effective.Should().Equal("Acme.Widgets.dll");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SidecarEntry_WithOnlyASameNamedAppClosureDll_IsSkippedLoudly_NeverBindsTheFallback()
    {
        // THE gap: the DLL exists in the app's base directory — where ResolveModulePath would
        // happily fall back to — but modules/Acme.Orphan/ is gone. A store-installed entry must
        // SKIP, not bind the app-closure binary.
        var dir = Path.Combine(Path.GetTempPath(), "mw-landed-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "Acme.Orphan.dll"), [1]);

            var skips = new List<(string Module, string Reason)>();
            var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
                [],
                List(Store("Acme.Orphan")),
                AcceptAll,
                name => ModuleActivationBoot.LandedModuleDllExists(dir, name),
                (m, r) => skips.Add((m, r)));

            effective.Should().BeEmpty("a same-named app-closure DLL must never satisfy a store-installed entry");
            var skip = skips.Should().ContainSingle().Subject;
            skip.Module.Should().Be("Acme.Orphan");
            skip.Reason.Should().Contain("modules/Acme.Orphan/Acme.Orphan.dll");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BaselineEntry_WithBaseDirectoryDll_IsIncluded_NoLandedFolderRequired()
    {
        // Baseline entries keep today's contract: both the modules/ layout and the classic
        // BaseDirectory location are legitimate (ResolveModulePath handles the probing at
        // install time), so the union includes them without a landed-folder check.
        var dir = Path.Combine(Path.GetTempPath(), "mw-landed-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "MeshWeaver.OgCard.dll"), [1]);

            var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
                ["MeshWeaver.OgCard.dll"],
                new ModuleActivationList(),
                AcceptAll,
                name => ModuleActivationBoot.LandedModuleDllExists(dir, name));

            effective.Should().Equal("MeshWeaver.OgCard.dll");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoBaseline_NoPersisted_IsEmpty()
    {
        ModuleActivationBoot.ComputeEffectiveModuleEntries(null, null, AcceptAll, AllDllsExist)
            .Should().BeEmpty();
        ModuleActivationBoot.ComputeEffectiveModuleEntries([], new ModuleActivationList(),
                AcceptAll, AllDllsExist)
            .Should().BeEmpty();
    }

    [Fact]
    public void Sidecar_RoundTrips_AndReadsMissingAsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-sidecar-" + Guid.NewGuid().ToString("N"));
        try
        {
            var missing = ModuleActivationSidecar.Read(dir);
            missing.Entries.Should().BeEmpty("a fresh deployment has no sidecar");
            missing.PendingRestart.Should().BeFalse();

            var written = new ModuleActivationList
            {
                Entries = [Store("Acme.Widgets") with { PackagePath = "Plugins/acme" }],
                PendingRestart = true,
            };
            ModuleActivationSidecar.Write(dir, written);
            var read = ModuleActivationSidecar.Read(dir);
            read.PendingRestart.Should().BeTrue();
            var entry = read.Entries.Should().ContainSingle().Subject;
            entry.Should().Be(written.Entries[0], "records round-trip value-equal through the sidecar JSON");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Sidecar_CorruptFile_ReadsEmpty_ButReportsLoudly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-sidecar-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "modules"));
            File.WriteAllText(ModuleActivationSidecar.SidecarPath(dir), "{ not json ]");

            string? reported = null;
            var list = ModuleActivationSidecar.Read(dir, msg => reported = msg);

            list.Entries.Should().BeEmpty("the deployment must boot — baseline modules unaffected");
            reported.Should().NotBeNull("a corrupt sidecar must never be a silent skip")
                .And.Contain(ModuleActivationSidecar.FileName);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
