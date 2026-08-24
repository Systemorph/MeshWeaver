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
    private static readonly Func<ModuleActivationEntry, bool> AllDllsExist = _ => true;

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

        effective.Select(m => m.Entry).Should().Equal(
            "MeshWeaver.OgCard.dll", "MeshWeaver.Speech.dll",
            "Acme.Widgets.dll", "Acme.Reports.dll");
        // The union carries each module's LANE, so the loader never re-derives it.
        effective.Where(m => m.Landed is not null).Select(m => m.Landed!.Name)
            .Should().Equal("Acme.Widgets", "Acme.Reports");
    }

    [Fact]
    public void PersistedDuplicateOfBaseline_IsDeduplicatedByName_CaseInsensitive()
    {
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            ["MeshWeaver.OgCard.dll"],
            List(Store("meshweaver.ogcard"), Store("Acme.Widgets"), Store("ACME.WIDGETS")),
            AcceptAll, AllDllsExist);

        // A store install of an already-baseline module (any casing) must not double-load it.
        effective.Select(m => m.Entry).Should().Equal("MeshWeaver.OgCard.dll", "Acme.Widgets.dll");
        effective[0].Landed.Should().BeNull("the BASELINE entry won the dedupe, so it stays baseline "
            + "— resolving it through the shadowed sidecar entry's folder would be a different file");
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

        effective.Select(m => m.Entry).Should().Equal("MeshWeaver.OgCard.dll", "Acme.Reports.dll");
        var skip = skips.Should().ContainSingle().Subject;
        skip.Module.Should().Be("Acme.Widgets");
        skip.Reason.Should().Contain("9.0.0").And.Contain("3.2.0",
            "the skip must name both versions — the entry stays for when the platform moves forward");
    }

    /// <summary>
    /// 🚨 The FLIP that finishes the registry-shelf story (2026-08-22): a HELD entry — shelved by the
    /// publish path while its floor exceeded the platform — activates on the NORMAL boot path the
    /// moment a platform update satisfies the floor. Same entry, same gate, no extra state and no
    /// separate reconcile: held-ness is DERIVED from the recorded floor against whatever platform
    /// is running, so the platform update (which is itself a restart) is the whole flip. This is
    /// the same computation the skip test above pins from the other side.
    /// </summary>
    [Fact]
    public void AHeldEntry_ActivatesOnceThePlatformSatisfiesItsFloor()
    {
        // The very entry ShelveModule records for an above-floor publish: enabled, floor recorded.
        var held = List(Store("MeshWeaver.Speech", floor: "3.0.0-rc7"));

        // While the registry still runs rc6, boot skips it — loudly, naming the floor.
        var skips = new List<(string Module, string Reason)>();
        ModuleActivationBoot.ComputeEffectiveModuleEntries(
                [], held,
                floor => ModulePlatformFloor.DeclineReason(floor, "3.0.0-rc6"),
                AllDllsExist,
                (m, r) => skips.Add((m, r)))
            .Should().BeEmpty("a held module must not load into a platform below its floor");
        skips.Should().ContainSingle().Subject.Reason.Should()
            .Contain("3.0.0-rc7").And.Contain("3.0.0-rc6");

        // The platform updates to rc7 — the SAME entry now passes the SAME gate and loads.
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            [], held,
            floor => ModulePlatformFloor.DeclineReason(floor, "3.0.0-rc7"),
            AllDllsExist);
        effective.Select(m => m.Entry).Should().Equal("MeshWeaver.Speech.dll");
        effective.Single().Landed.Should().NotBeNull(
            "it activates as the store-landed module it is, resolving to its own generation");
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

        effective.Select(m => m.Entry).Should().Equal("Acme.Widgets.dll");
    }

    [Fact]
    public void MissingDll_SkipsWithLoudReason_BaselineUnaffected()
    {
        var skips = new List<(string Module, string Reason)>();
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            ["MeshWeaver.OgCard.dll"],
            List(Store("Acme.Widgets"), Store("Acme.Reports")),
            AcceptAll,
            entry => entry.Name != "Acme.Widgets",
            (m, r) => skips.Add((m, r)));

        effective.Select(m => m.Entry).Should().Equal("MeshWeaver.OgCard.dll", "Acme.Reports.dll");
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
                ModuleActivationBoot.LandedDllPath(dir, Store("Acme.Widgets")), [1]);

            var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
                [],
                List(Store("Acme.Widgets")),
                AcceptAll,
                entry => ModuleActivationBoot.LandedModuleDllExists(dir, entry));

            effective.Select(m => m.Entry).Should().Equal("Acme.Widgets.dll");
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
                entry => ModuleActivationBoot.LandedModuleDllExists(dir, entry),
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

    // ---- #1949: the gate must follow the entry's GENERATION pointer, not the legacy folder -----
    //
    // Landing writes every version into a FRESH modules/<name>@<gen>/ directory and moves
    // ModuleActivationEntry.Directory; the legacy fixed modules/<name>/ folder is not written at
    // all any more. A gate that looked there found NOTHING for any generation-landed module, so
    // every store module on the deployment was SKIPPED at boot while its bytes sat correctly on
    // disk — and the reconciler, whose lookup DID follow the pointer, re-landed them into fresh
    // generations on every boot. Landing and activation never converged.

    /// <summary>A module landed the way landing lands today — bytes only in the generation
    /// directory, the entry pointing at it — must LOAD, and must resolve to that directory's
    /// DLL. Fails against the legacy-folder gate: the entry is skipped and never resolved.</summary>
    [Fact]
    public void GenerationLandedEntry_LoadsAtBoot_AndResolvesToItsGenerationDll()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-landed-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Exactly what ModuleLandingService leaves behind: ONE generation directory, no
            // legacy modules/Acme.Widgets/ folder anywhere.
            var entry = Store("Acme.Widgets") with { Directory = "Acme.Widgets@a1b2c3d4e" };
            var generationDir = Path.Combine(dir, "modules", entry.Directory!);
            Directory.CreateDirectory(generationDir);
            var generationDll = Path.Combine(generationDir, "Acme.Widgets.dll");
            File.WriteAllBytes(generationDll, [1]);
            Directory.Exists(Path.Combine(dir, "modules", "Acme.Widgets")).Should().BeFalse(
                "the legacy fixed folder is deliberately NOT written — that is the whole premise");

            var skips = new List<(string Module, string Reason)>();
            var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
                [],
                List(entry),
                AcceptAll,
                e => ModuleActivationBoot.LandedModuleDllExists(dir, e),
                (m, r) => skips.Add((m, r)));

            skips.Should().BeEmpty("a module landed into its generation directory is PRESENT");
            var module = effective.Should().ContainSingle().Subject;
            module.Entry.Should().Be("Acme.Widgets.dll");

            // …and the loader resolves the very file the gate just proved exists. Gate and
            // resolver naming one path is the invariant; #1949 was them naming two.
            ModuleActivationBoot.ResolveLoadPath(dir, module).Should().Be(generationDll);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The POINTER is authoritative, not the folder that happens to exist: an entry whose
    /// generation directory is gone is skipped even when a stale legacy modules/&lt;name&gt;/ copy
    /// sits beside it (the live stopgap shape). Otherwise boot would silently load bytes the
    /// activation record does not point at.</summary>
    [Fact]
    public void EntryWhoseGenerationIsGone_IsSkipped_EvenWithAStaleLegacyCopy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-landed-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "modules", "Acme.Widgets"));
            File.WriteAllBytes(
                Path.Combine(dir, "modules", "Acme.Widgets", "Acme.Widgets.dll"), [1]);

            var skips = new List<(string Module, string Reason)>();
            var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
                [],
                List(Store("Acme.Widgets") with { Directory = "Acme.Widgets@deadbeef" }),
                AcceptAll,
                e => ModuleActivationBoot.LandedModuleDllExists(dir, e),
                (m, r) => skips.Add((m, r)));

            effective.Should().BeEmpty();
            var skip = skips.Should().ContainSingle().Subject;
            skip.Reason.Should().Contain("modules/Acme.Widgets@deadbeef/Acme.Widgets.dll",
                "the skip must name the directory the ENTRY points at, or it sends the reader "
                + "looking in a folder nothing writes any more");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A BASELINE entry keeps <c>ResolveModulePath</c>'s probes — including the app-closure
    /// fallback, which is legitimate for the image's own closure and forbidden for a sidecar
    /// entry.</summary>
    [Fact]
    public void BaselineModule_ResolvesThroughResolveModulePath_NotTheLandedRule()
    {
        var module = new EffectiveModule("MeshWeaver.OgCard.dll", Landed: null);
        var root = Path.Combine(Path.GetTempPath(), "mw-noroot-" + Guid.NewGuid().ToString("N"));

        ModuleActivationBoot.ResolveLoadPath(root, module).Should().Be(
            Path.Combine(AppContext.BaseDirectory, "MeshWeaver.OgCard.dll"),
            "nothing is laid out under the module root, so the baseline entry falls back to the "
            + "app closure exactly as ResolveModulePath does");
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
                entry => ModuleActivationBoot.LandedModuleDllExists(dir, entry));

            effective.Select(m => m.Entry).Should().Equal("MeshWeaver.OgCard.dll");
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
