#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The readiness contract for <c>Modules:Required</c> (#2089): required-and-nothing-can-produce-it
/// versus expected-from-the-store-lane, told apart.
///
/// <para>🚨 <b>These assert BOTH directions on purpose.</b> The defect being fixed is a probe that
/// answered the same way for two situations with opposite remedies — and the obvious "fix" is a
/// probe that answers Degraded for everything, which is the skip-trapdoor: a gate that passes on no
/// evidence. So every test below has a partner asserting the OTHER verdict from the same shape of
/// input. A classifier that always said Absent (today's wedge) and one that always said
/// ExpectedLater (the lenient non-fix) each fail here.</para>
/// </summary>
public class RequiredModuleStatusTest
{
    private static IReadOnlySet<string> Loaded(params string[] names) =>
        names.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly Func<string?, string?> FloorSatisfied = _ => null;
    private static readonly Func<ModuleActivationEntry, bool> BytesPresent = _ => true;
    private static readonly Func<ModuleActivationEntry, bool> BytesGone = _ => false;

    private static ImmutableList<RequiredModuleVerdict> Classify(
        string[] required,
        string[] baseline,
        IReadOnlySet<string>? loaded = null,
        Func<string, bool>? resolves = null,
        ModuleActivationList? activation = null,
        Func<ModuleActivationEntry, bool>? bytes = null,
        Func<string?, string?>? floor = null) =>
        RequiredModuleStatus.Classify(
            required, baseline, loaded ?? Loaded(), resolves ?? (_ => false),
            activation, bytes ?? BytesPresent, floor ?? FloorSatisfied);

    // ── the hard gate keeps its teeth ────────────────────────────────────────────────────────────

    /// <summary>
    /// The case the gate exists for: the image's OWN list claims the pack and the image does not
    /// carry it. Nothing here can produce it and the previous pods still have it, so the rollout
    /// must stall. This is 3.0.0-rc5's shape.
    /// </summary>
    [Fact]
    public void APackTheImageClaimsToShip_AndDoesNot_IsABSENT()
    {
        var verdict = Classify(
            required: ["MeshWeaver.Blazor.Views.dll"],
            baseline: ["MeshWeaver.Blazor.Views.dll"]).Single();

        verdict.State.Should().Be(RequiredModuleState.Absent);
        verdict.Reason.Should().Contain("Modules:Assemblies");
    }

    /// <summary>The partner direction — the very same declaration, resolvable, is simply fine. A
    /// classifier that always reported Absent would fail here.</summary>
    [Fact]
    public void ThatSamePack_WhenTheImageActuallyCarriesIt_IsPRESENT()
    {
        Classify(
                required: ["MeshWeaver.Blazor.Views.dll"],
                baseline: ["MeshWeaver.Blazor.Views.dll"],
                resolves: _ => true)
            .Single().State.Should().Be(RequiredModuleState.Present);
    }

    // ── the store lane is expected, never a wedge ────────────────────────────────────────────────

    /// <summary>
    /// 🚨 #2089 itself. MeshWeaver.Speech moved OUT of the image into the store, so the image
    /// requires it while never claiming to ship it. Reporting that as Absent stalled both prod
    /// rollouts with no remedy but blanking the config on the live deployment — and stalling could
    /// never have delivered it, because the registry that serves it is a portal downstream of this
    /// very rollout.
    /// </summary>
    [Fact]
    public void AStoreDeliveredModuleTheImageNeverClaimed_IsEXPECTEDLATER_NotAbsent()
    {
        var verdict = Classify(
            required: ["MeshWeaver.Speech.dll"],
            baseline: ["MeshWeaver.Social.dll"]).Single();

        verdict.State.Should().Be(RequiredModuleState.ExpectedLater);
        verdict.Reason.Should().Contain("install the package");
    }

    /// <summary>The partner direction — expected-later is NOT "always degraded". Once it is loaded
    /// here, it is Present and nothing is reported at all.</summary>
    [Fact]
    public void ThatSameStoreModule_OnceLoaded_IsPRESENT()
    {
        Classify(
                required: ["MeshWeaver.Speech.dll"],
                baseline: ["MeshWeaver.Social.dll"],
                loaded: Loaded("MeshWeaver.Speech"))
            .Single().State.Should().Be(RequiredModuleState.Present);
    }

    /// <summary>Landed on the volume and awaiting the restart — still expected, and the reason says
    /// which of the four store states it is in, so an operator is never left guessing.</summary>
    [Fact]
    public void AStoreModuleLandedButNotLoaded_SaysARestartActivatesIt()
    {
        var verdict = Classify(
            required: ["MeshWeaver.Speech.dll"],
            baseline: [],
            activation: new ModuleActivationList
            {
                Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Speech" }],
            }).Single();

        verdict.State.Should().Be(RequiredModuleState.ExpectedLater);
        verdict.Reason.Should().Contain("a restart activates it");
    }

    /// <summary>A landing that did not complete is still expected-later — but it says so, and it
    /// says the remedy is a re-install, not a restart (#2093's promise, kept honest).</summary>
    [Fact]
    public void AStoreModuleRecordedButWithoutBytes_SaysReinstall_NotRestart()
    {
        var verdict = Classify(
            required: ["MeshWeaver.Mcp.dll"],
            baseline: [],
            activation: new ModuleActivationList
            {
                Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Mcp" }],
            },
            bytes: BytesGone).Single();

        verdict.State.Should().Be(RequiredModuleState.ExpectedLater);
        verdict.Reason.Should().Contain("Re-install");
        verdict.Reason.Should().NotContain("a restart activates it");
    }

    /// <summary>
    /// The near-circular case (#2089's registry shelf): the module is landed but HELD above this
    /// platform's version. The platform update that satisfies the floor is itself the restart that
    /// loads it — so holding the rollout is precisely the wrong move, and the reason names the
    /// floor rather than saying "missing".
    /// </summary>
    [Fact]
    public void AStoreModuleHeldAboveThePlatformFloor_NamesTheFloor()
    {
        var verdict = Classify(
            required: ["MeshWeaver.Speech.dll"],
            baseline: [],
            activation: new ModuleActivationList
            {
                Entries = [new ModuleActivationEntry
                {
                    Name = "MeshWeaver.Speech", MinMeshVersion = "3.0.0-rc7",
                }],
            },
            floor: _ => "the running platform 3.0.0-rc6 is below the declared floor 3.0.0-rc7").Single();

        verdict.State.Should().Be(RequiredModuleState.ExpectedLater);
        verdict.Reason.Should().Contain("3.0.0-rc7");
    }

    /// <summary>A DISABLED record is an uninstall, not an install in flight — the module is simply
    /// not installed, and the remedy is to install it (or delist it).</summary>
    [Fact]
    public void ADisabledRecord_ReadsAsNotInstalled()
    {
        Classify(
                required: ["MeshWeaver.Speech.dll"],
                baseline: [],
                activation: new ModuleActivationList
                {
                    Entries = [new ModuleActivationEntry { Name = "MeshWeaver.Speech", Enabled = false }],
                })
            .Single().Reason.Should().Contain("NOT installed");
    }

    // ── the buckets never blur ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 The two verdicts in ONE deployment, which is the state that actually shipped: the image
    /// lost a pack it claims AND the store lane owes another. Both must be reported, separately —
    /// an aggregate that reduced to one number would hide whichever half the operator needed.
    /// </summary>
    [Fact]
    public void ALostPackAndAnOwedModule_AreReportedSeparately()
    {
        var verdicts = Classify(
            required: ["MeshWeaver.Blazor.Views.dll", "MeshWeaver.Speech.dll"],
            baseline: ["MeshWeaver.Blazor.Views.dll"]);

        RequiredModuleStatus.Absent(verdicts).Select(v => v.Name)
            .Should().Equal(["MeshWeaver.Blazor.Views"]);
        RequiredModuleStatus.ExpectedLater(verdicts).Select(v => v.Name)
            .Should().Equal(["MeshWeaver.Speech"]);
    }

    [Fact]
    public void DeclaringNothingRequired_IsInert()
    {
        // Today's deployments that declare none must behave exactly as they do now.
        Classify(required: [], baseline: ["MeshWeaver.Social.dll"]).Should().BeEmpty();
    }

    [Fact]
    public void BlankEntries_AreIgnored_NotReportedAsFaults()
    {
        RequiredModuleStatus.Classify(
                ["", "   ", null], null, Loaded(), _ => false, null, BytesPresent, FloorSatisfied)
            .Should().BeEmpty();
    }

    [Fact]
    public void MatchingIsCaseInsensitive_LikeAssemblyNames()
    {
        Classify(
                required: ["MeshWeaver.SPEECH.dll"],
                baseline: [],
                loaded: Loaded("meshweaver.speech"))
            .Single().State.Should().Be(RequiredModuleState.Present);
    }
}
