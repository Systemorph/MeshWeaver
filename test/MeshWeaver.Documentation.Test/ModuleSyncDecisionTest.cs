#pragma warning disable CS1591
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 Policy <c>module-sync-per-manifest-hash</c>, as data: every module of a synced tree is judged
/// ALONE by the content hash in its <c>manifest.lock</c> — unchanged writes nothing, changed syncs,
/// a declared platform floor above the running platform declines that module and no other. The
/// seal is not an input at all, which is what the control-instance case below pins.
/// </summary>
public class ModuleSyncDecisionTest
{
    private const string Running = "3.0.0-ci.9218";

    private static ModuleReading Module(string name, string? version, string? floor = null, string root = "")
        => new(name, root, version, floor);

    [Fact]
    public void AnUnchangedModule_WritesNothing()
    {
        var outcome = ModuleSyncDecision.Decide(
                [Module("Hosting", "971f5cd34e9af1f2")],
                new Dictionary<string, string> { ["Hosting"] = "971f5cd34e9af1f2" },
                Running, reconcile: false)
            .Single();
        outcome.Outcome.Should().Be(ModuleSyncOutcomeKind.Unchanged);
        outcome.Reason.Should().Contain("nothing written");
    }

    [Fact]
    public void AChangedModule_Syncs()
    {
        var outcome = ModuleSyncDecision.Decide(
                [Module("Hosting", "1f75ade77bdf2fa5")],
                new Dictionary<string, string> { ["Hosting"] = "971f5cd34e9af1f2" },
                Running, reconcile: false)
            .Single();
        outcome.Outcome.Should().Be(ModuleSyncOutcomeKind.Synced);
        outcome.HeldVersion.Should().Be("971f5cd34e9af1f2");
        outcome.IncomingVersion.Should().Be("1f75ade77bdf2fa5");
    }

    [Fact]
    public void AModuleNeverRecorded_Syncs()
        => ModuleSyncDecision.Decide([Module("Hosting", "1f75ade77bdf2fa5")], null, Running, reconcile: false)
            .Single().Outcome.Should().Be(ModuleSyncOutcomeKind.Synced);

    [Fact]
    public void AManifestWithNoHash_IsNeverReadAsUnchanged()
        => ModuleSyncDecision.Decide(
                [Module("Hosting", null)],
                new Dictionary<string, string> { ["Hosting"] = "971f5cd34e9af1f2" },
                Running, reconcile: false)
            .Single().Outcome.Should().Be(ModuleSyncOutcomeKind.Synced,
                "a reading that established no hash must sync, never skip");

    [Fact]
    public void AReconcile_NeverSkipsAnUnchangedModule()
        => ModuleSyncDecision.Decide(
                [Module("Hosting", "971f5cd34e9af1f2")],
                new Dictionary<string, string> { ["Hosting"] = "971f5cd34e9af1f2" },
                Running, reconcile: true)
            .Single().Outcome.Should().Be(ModuleSyncOutcomeKind.Synced,
                "a reconcile measures the live mesh against the tree — the hash says nothing about drift");

    [Fact]
    public void AFloorAboveTheRunningPlatform_DeclinesThatModule_AndNamesBothVersions()
    {
        var outcome = ModuleSyncDecision.Decide(
                [Module("Hosting", "1f75ade77bdf2fa5", floor: "3.0.0-ci.9300")],
                new Dictionary<string, string> { ["Hosting"] = "971f5cd34e9af1f2" },
                Running, reconcile: false)
            .Single();
        outcome.Outcome.Should().Be(ModuleSyncOutcomeKind.Declined);
        outcome.Floor.Should().Be("3.0.0-ci.9300");
        outcome.Reason.Should().Contain("3.0.0-ci.9300").And.Contain(Running);
    }

    [Fact]
    public void AFloorAtOrBelowTheRunningPlatform_Syncs()
        => ModuleSyncDecision.Decide(
                [Module("Hosting", "1f75ade77bdf2fa5", floor: "3.0.0-ci.7845")], null, Running, reconcile: false)
            .Single().Outcome.Should().Be(ModuleSyncOutcomeKind.Synced);

    [Fact]
    public void AnUnknownRunningPlatform_NeverDeclines()
        => ModuleSyncDecision.Decide(
                [Module("Hosting", "1f75ade77bdf2fa5", floor: "3.0.0-ci.9300")], null, null, reconcile: false)
            .Single().Outcome.Should().Be(ModuleSyncOutcomeKind.Synced,
                "unknown is accepted, never declined — the ladder's own comparison");

    [Fact]
    public void ADeclinedSibling_NeverHoldsAnotherModule()
    {
        var outcomes = ModuleSyncDecision.Decide(
            [
                Module("Store", "aaaa", floor: "3.0.0-ci.9300", root: "Store"),
                Module("Hosting", "bbbb", root: "Hosting"),
                Module("Agent", "cccc", root: "Agent"),
            ],
            new Dictionary<string, string> { ["Hosting"] = "old", ["Agent"] = "cccc", ["Store"] = "old" },
            Running, reconcile: false);
        outcomes.Single(o => o.Module == "Store").Outcome.Should().Be(ModuleSyncOutcomeKind.Declined);
        outcomes.Single(o => o.Module == "Hosting").Outcome.Should().Be(ModuleSyncOutcomeKind.Synced,
            "a sibling's decline is a fact about the sibling");
        outcomes.Single(o => o.Module == "Agent").Outcome.Should().Be(ModuleSyncOutcomeKind.Unchanged);
    }

    [Fact]
    public void TheRecordedHashes_KeepADeclinedModulesOldHash_AndTakeEveryOtherIncomingOne()
    {
        var outcomes = ModuleSyncDecision.Decide(
            [
                Module("Store", "new-store", floor: "3.0.0-ci.9300", root: "Store"),
                Module("Hosting", "new-hosting", root: "Hosting"),
            ],
            new Dictionary<string, string> { ["Hosting"] = "old-hosting", ["Store"] = "old-store" },
            Running, reconcile: false);
        var recorded = ModuleSyncDecision.Recorded(outcomes);
        recorded["Hosting"].Should().Be("new-hosting");
        recorded["Store"].Should().Be("old-store",
            "a declined module did not land — recording its new hash would read it as unchanged after the roll");
    }

    /// <summary>
    /// 🚨 THE CASE THE POLICY EXISTS FOR. The control instance runs an image whose identity's only
    /// Plugins seal is at an OLD commit, and every newer seal is for a NEWER platform (the ladder
    /// refuses those bytes here). The Hosting module changed on main. It must SYNC: nothing in this
    /// decision reads the seal, and the gate that used to hold the whole Space now proceeds.
    /// </summary>
    [Fact]
    public void AChangedHostingModule_OnAnInstanceWhoseSealIsOld_AndWhoseNewerSealsAreForNewerPlatforms_Syncs()
    {
        var plugins = new RepoIdentity("Systemorph", "MeshWeaver.Plugins");
        const string oldSeal = "7545d35500000000000000000000000000000000";
        const string head = "d0457587ee9ee881275081be2affe3200dd7a8be";
        const string identity = "c3e1";
        var sealedForThisIdentity = new[]
        {
            new SealedSource("plugins", "Systemorph/MeshWeaver.Plugins", oldSeal, true, null),
            new SealedSource("plugins-next", "Systemorph/MeshWeaver.Plugins", head, false, "produced by a newer platform")
            {
                HeldForNewerPlatform = true,
                ProducerPlatformVersion = "3.0.0-ci.9300",
                RunningPlatformVersion = Running,
            },
        };

        // The source lane: the green build of `head` lands AT head — no hold, no redirect.
        var plan = SealedSyncGate.DecideBuild(plugins, head, oldSeal, sealedForThisIdentity, identity,
            new PublicationLine("c3e2", "3.0.0-ci.9300"));
        plan.Proceed.Should().BeTrue();
        plan.Redirected.Should().BeFalse();
        plan.Commit.Should().Be(head);

        // The module lane: Hosting's manifest hash moved, and its floor is below the running build.
        var hosting = ModuleSyncDecision.Decide(
                [Module("Hosting", "1f75ade77bdf2fa5", floor: "3.0.0-ci.7845")],
                new Dictionary<string, string> { ["Hosting"] = "971f5cd34e9af1f2" },
                Running, reconcile: false)
            .Single();
        hosting.Outcome.Should().Be(ModuleSyncOutcomeKind.Synced,
            "control-plane code newer than the image's seal must converge by sync alone, without a roll");
    }

    [Fact]
    public void ReadingATree_FindsEachManifest_ItsHash_AndItsModulesDeclaredFloor()
    {
        var modules = ModuleSyncDecision.Read(
        [
            ("manifest.lock", """{"module":"Hosting","moduleVersion":"1f75ade77bdf2fa5","files":{}}"""),
            ("index.json", """{"content":{"minMeshVersion":"3.0.0-ci.7845"}}"""),
            ("Admin/Source/X.cs", "class X {}"),
        ]);
        var hosting = modules.Single();
        hosting.Module.Should().Be("Hosting");
        hosting.Root.Should().Be("");
        hosting.ModuleVersion.Should().Be("1f75ade77bdf2fa5");
        hosting.Floor.Should().Be("3.0.0-ci.7845");
    }

    [Fact]
    public void ReadingATreeOfSeveralModules_RootsEachAtItsFolder()
    {
        var modules = ModuleSyncDecision.Read(
        [
            ("Hosting/manifest.lock", """{"module":"Hosting","moduleVersion":"h1","files":{}}"""),
            ("Store/manifest.lock", """{"module":"Store","moduleVersion":"s1","files":{}}"""),
            ("Store/index.json", """{"content":{"minMeshVersion":"3.0.0-ci.9300"}}"""),
        ]);
        modules.Should().HaveCount(2);
        modules[0].Module.Should().Be("Hosting");
        modules[0].Root.Should().Be("Hosting");
        modules[0].Floor.Should().BeNull();
        modules[1].Module.Should().Be("Store");
        modules[1].Root.Should().Be("Store");
        modules[1].Floor.Should().Be("3.0.0-ci.9300");
    }

    [Fact]
    public void AnUnparsableManifest_StillNamesItsModule_WithNoHash_SoItSyncs()
    {
        var module = ModuleSyncDecision.Read([("Hosting/manifest.lock", "{ not json")]).Single();
        module.Module.Should().Be("Hosting");
        module.ModuleVersion.Should().BeNull();
    }

    [Fact]
    public void ATreeWithNoManifest_StatesNoModule()
        => ModuleSyncDecision.Read([("index.json", "{}"), ("Doc/Page.md", "# hi")])
            .Should().BeEmpty("a course or content repo imports exactly as before");
}
