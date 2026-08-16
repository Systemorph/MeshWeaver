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
/// enabled persisted store installs, deduped by name, with the two loud skip rules (framework-MVID
/// mismatch, missing DLL) that keep an image roll or a lost volume from crashing boot. Plus the
/// sidecar's read/write round-trip and its corrupt-file loudness.
/// </summary>
public class ModuleActivationBootTest
{
    private static readonly Func<string?, string?> AcceptAll = _ => null;
    private static readonly Func<string, bool> AllDllsExist = _ => true;

    private static ModuleActivationList List(params ModuleActivationEntry[] entries) =>
        new() { Entries = [.. entries] };

    private static ModuleActivationEntry Store(string name, bool enabled = true, string? mvid = "m1") =>
        new() { Name = name, FrameworkMvid = mvid, Enabled = enabled };

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
    public void FrameworkMvidMismatch_SkipsWithLoudReason_NeverCrashes()
    {
        var skips = new List<(string Module, string Reason)>();
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            ["MeshWeaver.OgCard.dll"],
            List(Store("Acme.Widgets", mvid: "stale-mvid"), Store("Acme.Reports", mvid: "live")),
            // The gate is injected — production passes PrebuiltAssemblySeeder.DeclineReason, so
            // there is never a second notion of framework identity to test here, only the skip.
            mvid => mvid == "live" ? null : $"built against framework {mvid}, live framework is live",
            AllDllsExist,
            (m, r) => skips.Add((m, r)));

        effective.Should().Equal("MeshWeaver.OgCard.dll", "Acme.Reports.dll");
        var skip = skips.Should().ContainSingle().Subject;
        skip.Module.Should().Be("Acme.Widgets");
        skip.Reason.Should().Contain("stale-mvid").And.Contain("live",
            "the skip must name both identities — the entry stays for the post-roll re-install");
    }

    [Fact]
    public void NullRecordedMvid_IsSkipped_WhenTheGateDeclinesAbsentIdentity()
    {
        // Production's gate (PrebuiltAssemblySeeder.DeclineReason) declines an ABSENT identity —
        // "cannot be shown ABI-compatible" — and the boot union inherits that: an unverifiable
        // store entry never loads on faith.
        var skips = new List<(string Module, string Reason)>();
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            [],
            List(Store("Acme.Widgets", mvid: null)),
            mvid => mvid is null ? "no recorded framework identity" : null,
            AllDllsExist,
            (m, r) => skips.Add((m, r)));

        effective.Should().BeEmpty();
        skips.Should().ContainSingle().Which.Module.Should().Be("Acme.Widgets");
    }

    [Fact]
    public void MissingDll_SkipsWithLoudReason_BaselineUnaffected()
    {
        var skips = new List<(string Module, string Reason)>();
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            ["MeshWeaver.OgCard.dll"],
            List(Store("Acme.Widgets"), Store("Acme.Reports")),
            AcceptAll,
            entry => entry != "Acme.Widgets.dll",
            (m, r) => skips.Add((m, r)));

        effective.Should().Equal("MeshWeaver.OgCard.dll", "Acme.Reports.dll");
        var skip = skips.Should().ContainSingle().Subject;
        skip.Module.Should().Be("Acme.Widgets");
        skip.Reason.Should().Contain("Acme.Widgets.dll");
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
