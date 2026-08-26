#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The restart-as-activation signal a PROCESS can honestly report (#1979).
///
/// <para><see cref="ModuleActivationList.PendingRestart"/> was written by both landing sites,
/// consumed at boot, and read by nothing. Reading it directly, though, answers the wrong question:
/// it is one deployment-wide boolean that the next boot clears, so on a fleet the pod that clears
/// it is not the pod that is missing the module. These pin the question that IS right —
/// which activated modules are not loaded in THIS process — and the rule that an unreadable
/// sidecar is the absence of an answer rather than a reassuring one.</para>
/// </summary>
public class ModuleActivationStatusTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-activation-status-" + Guid.NewGuid().ToString("N"));

    public ModuleActivationStatusTest() => Directory.CreateDirectory(Path.Combine(root, "modules"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    private static IReadOnlySet<string> Loaded(params string[] names) =>
        names.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A satisfied platform gate — the state of every pre-shelf test, kept explicit
    /// because the gate is a required input: pending-ness PROMISES that a restart loads the
    /// module, and only the floor gate can keep that promise honest (2026-08-22).</summary>
    private static readonly Func<string?, string?> FloorSatisfied = _ => null;

    /// <summary>The bytes-are-there half of the same promise (#2093): pending means a restart
    /// LOADS it, which is false when the landed assembly is gone. Explicit here for the same
    /// reason the floor gate is — production passes boot's own existence check.</summary>
    private static readonly Func<ModuleActivationEntry, bool> BytesPresent = _ => true;

    /// <summary>The opposite: the activation record says the module is on, the volume disagrees.</summary>
    private static readonly Func<ModuleActivationEntry, bool> BytesGone = _ => false;

    // ── the pure derivation ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnEnabledEntryThatIsNotLoaded_IsPending()
    {
        var pending = ModuleActivationStatus.NotYetLoaded(
            new ModuleActivationList
            {
                Entries =
                [
                    new ModuleActivationEntry { Name = "MeshWeaver.Acme", PackagePath = "Plugins/acme", Version = "1.2.0" },
                    new ModuleActivationEntry { Name = "MeshWeaver.Loaded" },
                ],
            },
            Loaded("MeshWeaver.Loaded"),
            FloorSatisfied, BytesPresent);

        pending.Should().ContainSingle();
        pending[0].Name.Should().Be("MeshWeaver.Acme");
        pending[0].PackagePath.Should().Be("Plugins/acme");
        pending[0].Version.Should().Be("1.2.0");
    }

    /// <summary>
    /// 🚨 The multi-replica hole. A cleared flag says only "some boot happened somewhere"; it says
    /// nothing about whether THIS process loaded the module.
    /// </summary>
    [Fact]
    public void TheDeploymentWideFlagBeingCleared_DoesNotMakeAnUnloadedModuleLoaded()
    {
        var pending = ModuleActivationStatus.NotYetLoaded(
            new ModuleActivationList
            {
                PendingRestart = false,
                Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Acme" }],
            },
            Loaded("MeshWeaver.Something.Else"),
            FloorSatisfied, BytesPresent);

        pending.Should().ContainSingle();
    }

    /// <summary>The converse: a set flag on a pod that HAS the module is not a pending restart.</summary>
    [Fact]
    public void TheDeploymentWideFlagBeingSet_DoesNotMakeALoadedModuleUnloaded()
    {
        ModuleActivationStatus.NotYetLoaded(
                new ModuleActivationList
                {
                    PendingRestart = true,
                    Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Acme" }],
                },
                Loaded("MeshWeaver.Acme"),
                FloorSatisfied, BytesPresent)
            .Should().BeEmpty();
    }

    [Fact]
    public void ADisabledEntry_IsNeverPending()
    {
        // A disabled entry is the record of an uninstall; the module's absence is the outcome.
        ModuleActivationStatus.NotYetLoaded(
                new ModuleActivationList
                {
                    Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Removed", Enabled = false }],
                },
                Loaded(),
                FloorSatisfied, BytesPresent)
            .Should().BeEmpty();
    }

    /// <summary>
    /// 🚨 A HELD entry (2026-08-22) — floor above the running platform, the registry-shelf state — is
    /// NOT pending: "pending" promises that a restart loads the module, and boot's floor gate
    /// would skip this one, so the prompt could never be cleared by the restart it asks for. The
    /// moment the platform satisfies the floor (same entry, same gate), the promise becomes true
    /// and the entry IS pending — until the boot that reported it loads it.
    /// </summary>
    [Fact]
    public void AHeldEntry_IsNotPending_UntilThePlatformSatisfiesItsFloor()
    {
        var list = new ModuleActivationList
        {
            Entries = [new ModuleActivationEntry
            {
                Name = "MeshWeaver.Speech", MinMeshVersion = "3.0.0-rc7",
            }],
        };

        ModuleActivationStatus.NotYetLoaded(list, Loaded(),
                floor => ModulePlatformFloor.DeclineReason(floor, "3.0.0-rc6"), BytesPresent)
            .Should().BeEmpty(
                "a restart cannot activate a held module — reporting it would be a permanent "
                + "restart prompt no restart can clear");

        ModuleActivationStatus.NotYetLoaded(list, Loaded(),
                floor => ModulePlatformFloor.DeclineReason(floor, "3.0.0-rc7"), BytesPresent)
            .Should().ContainSingle(
                "once the platform satisfies the floor, the restart promise is honest again")
            .Subject.Name.Should().Be("MeshWeaver.Speech");
    }

    /// <summary>
    /// 🚨 #2093, pure. The same entry lands in EXACTLY ONE of the two buckets, decided by whether
    /// its bytes are on the volume — and the buckets must not overlap, because the remedies are
    /// opposite: wait for the restart, versus re-install the package. Asserted in BOTH directions,
    /// so a derivation that always answered "pending" (today's bug) or always answered
    /// "unresolvable" would fail.
    /// </summary>
    [Fact]
    public void AnEntryIsPENDING_OrUNRESOLVABLE_ByWhetherItsBytesAreThere()
    {
        var list = new ModuleActivationList
        {
            Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Mcp", PackagePath = "Plugins/mcp" }],
        };

        ModuleActivationStatus.NotYetLoaded(list, Loaded(), FloorSatisfied, BytesPresent)
            .Should().ContainSingle("bytes on the volume — a restart genuinely loads it");
        ModuleActivationStatus.Unresolvable(list, Loaded(), FloorSatisfied, BytesPresent)
            .Should().BeEmpty();

        ModuleActivationStatus.NotYetLoaded(list, Loaded(), FloorSatisfied, BytesGone)
            .Should().BeEmpty("no restart loads an assembly that is not on the volume");
        ModuleActivationStatus.Unresolvable(list, Loaded(), FloorSatisfied, BytesGone)
            .Should().ContainSingle().Subject.Name.Should().Be("MeshWeaver.Mcp");
    }

    /// <summary>A HELD entry is neither: the platform, not the bytes, is what it waits on, and the
    /// platform update that satisfies the floor is itself the restart that loads it.</summary>
    [Fact]
    public void AHeldEntry_IsNeitherPendingNorUnresolvable()
    {
        var list = new ModuleActivationList
        {
            Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Speech", MinMeshVersion = "9.9.9" }],
        };
        Func<string?, string?> floorRefused = _ => "platform 1.0.0 is below the declared floor 9.9.9";

        ModuleActivationStatus.NotYetLoaded(list, Loaded(), floorRefused, BytesGone).Should().BeEmpty();
        ModuleActivationStatus.Unresolvable(list, Loaded(), floorRefused, BytesGone).Should().BeEmpty();
    }

    /// <summary>An unresolvable module must not put a restart prompt on a buyer's package card:
    /// the card's question is "will a restart finish this install", and the answer is no.</summary>
    [Fact]
    public void AnUnresolvableModule_IsNotAPendingRestartForItsPackage()
    {
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Mcp", PackagePath = "Plugins/mcp" }],
        });

        new PendingModuleActivations(root).IsPendingForPackage("Plugins/mcp").Should().BeFalse();
    }

    [Fact]
    public void MatchingIsCaseInsensitive_LikeAssemblyNames()
    {
        ModuleActivationStatus.NotYetLoaded(
                new ModuleActivationList { Entries = [new ModuleActivationEntry { Name = "MeshWeaver.ACME" }] },
                Loaded("meshweaver.acme"),
                FloorSatisfied, BytesPresent)
            .Should().BeEmpty();
    }

    [Fact]
    public void TheLiveProcessSet_ContainsThisAssembly()
    {
        // Guards the one non-pure step: if LoadedAssemblyNames ever stopped seeing the process's
        // own assemblies, every module would read as pending and every surface would cry wolf.
        ModuleActivationStatus.LoadedAssemblyNames()
            .Should().Contain(typeof(ModuleActivationStatus).Assembly.GetName().Name!);
    }

    // ── the per-package question the Store card asks ────────────────────────────────────────────

    [Fact]
    public void APackagePathMatches_ItsOwnPendingModuleOnly()
    {
        var pending = new[]
        {
            new PendingModuleActivation("MeshWeaver.Acme", "Plugins/acme", "1.0.0"),
        };

        ModuleActivationStatus.IsPendingForPackage(pending, "Plugins/acme").Should().BeTrue();
        ModuleActivationStatus.IsPendingForPackage(pending, "/Plugins/acme/").Should().BeTrue();
        ModuleActivationStatus.IsPendingForPackage(pending, "Plugins/other").Should().BeFalse();
    }

    /// <summary>
    /// A blank path is not a wildcard. A card with no path to match on must not inherit some other
    /// package's pending restart and tell a buyer to restart for something they did not install.
    /// </summary>
    [Fact]
    public void ABlankPackagePath_MatchesNothing()
    {
        var pending = new[] { new PendingModuleActivation("MeshWeaver.Acme", "Plugins/acme", null) };

        ModuleActivationStatus.IsPendingForPackage(pending, null).Should().BeFalse();
        ModuleActivationStatus.IsPendingForPackage(pending, "  ").Should().BeFalse();
        ModuleActivationStatus.IsPendingForPackage(
            [new PendingModuleActivation("X", null, null)], "Plugins/acme").Should().BeFalse();
    }

    // ── the reader, over the durable state ──────────────────────────────────────────────────────

    /// <summary>Puts an entry's landed DLL where boot looks for it, so the entry is genuinely one
    /// restart from loading rather than a record pointing at nothing.</summary>
    private void LandBytesFor(string name, string? directory = null)
    {
        var dir = Path.Combine(root, "modules", directory ?? name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".dll"), [1]);
    }

    [Fact]
    public void TheReader_ReportsWhatTheSidecarSays_AgainstThisProcess()
    {
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Acme", PackagePath = "Plugins/acme" }],
        });
        LandBytesFor("MeshWeaver.Acme");

        var report = new PendingModuleActivations(root).Read(Loaded());

        report.IsUndetermined.Should().BeFalse();
        report.HasPending.Should().BeTrue();
        report.HasUnresolvable.Should().BeFalse();
        report.Describe().Should().Contain("MeshWeaver.Acme");
        new PendingModuleActivations(root).IsPendingForPackage("Plugins/acme").Should().BeTrue();
    }

    /// <summary>
    /// 🚨 #2093, over the durable state. The record says the module is ON; the volume says its
    /// assembly is not there. That is NOT "pending": no restart loads it, and boot skips it. It
    /// must land in the OTHER bucket, or the surface makes a promise every restart breaks — which
    /// is what let <c>/mcp</c> 404 for a whole pod lifetime while everything read "installed".
    /// </summary>
    [Fact]
    public void AnActivatedEntryWhoseBytesAreGone_IsUnresolvable_NotPending()
    {
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Mcp", PackagePath = "Plugins/mcp" }],
        });
        // Deliberately no bytes on the volume — the half-completed landing.

        var report = new PendingModuleActivations(root).Read(Loaded());

        report.IsUndetermined.Should().BeFalse();
        report.HasPending.Should().BeFalse("a restart cannot load an assembly that is not there");
        report.HasUnresolvable.Should().BeTrue();
        report.Unresolvable.Should().ContainSingle().Subject.Name.Should().Be("MeshWeaver.Mcp");
        report.Describe().Should().Contain("re-install");
        report.Describe().Should().NotContain("a restart activates them");
    }

    /// <summary>The generation directory is the ONE resolution — an entry whose bytes live in its
    /// generation folder is pending, exactly as boot would find it.</summary>
    [Fact]
    public void BytesInTheEntrysGenerationDirectory_CountAsLanded()
    {
        ModuleActivationSidecar.Write(root, new ModuleActivationList
        {
            Entries =
            [
                new ModuleActivationEntry { Name = "MeshWeaver.Acme", Directory = "MeshWeaver.Acme@abc123" },
            ],
        });
        LandBytesFor("MeshWeaver.Acme", "MeshWeaver.Acme@abc123");

        var report = new PendingModuleActivations(root).Read(Loaded());

        report.HasPending.Should().BeTrue();
        report.HasUnresolvable.Should().BeFalse();
    }

    [Fact]
    public void AnAbsentSidecar_IsAKnownEmptyAnswer_NotAnUndeterminedOne()
    {
        // A fresh deployment genuinely has nothing landed. That IS evidence.
        var report = new PendingModuleActivations(Path.Combine(root, "fresh")).Read(Loaded());

        report.IsUndetermined.Should().BeFalse();
        report.HasPending.Should().BeFalse();
        report.Describe().Should().Be("no module activation pending");
    }

    /// <summary>
    /// 🚨 FAIL CLOSED. <see cref="ModuleActivationSidecar.Read"/> swallows an unparseable file into
    /// the EMPTY list, so any reader that ignores its corruption callback reports "nothing pending"
    /// — indistinguishably from a healthy deployment, forever. The report must separate the two.
    /// </summary>
    [Fact]
    public void ACorruptSidecar_IsUNDETERMINED_NotEmpty()
    {
        File.WriteAllText(ModuleActivationSidecar.SidecarPath(root), "{ not activation json");

        var report = new PendingModuleActivations(root).Read(Loaded());

        report.IsUndetermined.Should().BeTrue();
        report.HasPending.Should().BeFalse("an undetermined report asserts nothing either way");
        report.Describe().Should().Contain("could not be determined");
        report.UndeterminedReason.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The per-package answer degrades to "nothing to say", never to a restart prompt a buyer
    /// cannot act on. The undetermined state is an OPERATOR signal; it is reported there.
    /// </summary>
    [Fact]
    public void AnUndeterminedState_DoesNotPutARestartPromptOnEveryCard()
    {
        File.WriteAllText(ModuleActivationSidecar.SidecarPath(root), "{ not activation json");

        new PendingModuleActivations(root).IsPendingForPackage("Plugins/acme").Should().BeFalse();
    }

    [Fact]
    public void TheDescriptionTruncates_RatherThanPrintingTheWholeFleet()
    {
        var many = Enumerable.Range(0, 25)
            .Select(i => new PendingModuleActivation($"MeshWeaver.M{i:00}", null, null))
            .ToArray();

        var line = ModuleActivationStatus.Describe(many);

        line.Should().Contain("25 module(s)");
        line.Should().Contain("…(+15)");
    }
}
