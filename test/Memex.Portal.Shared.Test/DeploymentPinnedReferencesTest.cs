using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// How a control instance turns its records into <see cref="PinnedPlatformReference"/>s for the
/// prebuilt-bundle retention: a <c>Hosting/Deployment</c> record's <c>pinnedImageTag</c> (a mesh
/// NodeType with no CLR type here, read as JSON in whatever shape the content arrived) and an
/// instance's <c>Hosting/ModuleInventory</c> report (<see cref="DeploymentReport.PlatformVersion"/>
/// + <see cref="DeploymentReport.FrameworkIdentity"/>).
/// </summary>
public class DeploymentPinnedReferencesTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ADeploymentRecordsPin_IsAReference_WhateverJsonShapeTheContentTook()
    {
        var asNode = new MeshNode("pearl", "Deployments")
        {
            NodeType = DeploymentPinnedReferences.DeploymentNodeType,
            Content = JsonNode.Parse("""{ "host": "pearl.example", "pinnedImageTag": "memex-portal-ai:3.0.0-ci.8080" }"""),
        };
        var asElement = asNode with
        {
            Content = JsonSerializer.Deserialize<JsonElement>("""{ "PinnedImageTag": "3.0.0-ci.8080" }"""),
        };

        var fromNode = DeploymentPinnedReferences.DeploymentPinOf(asNode, Options)!;
        fromNode.Origin.Should().Be("Deployment Deployments/pearl");
        fromNode.Version.Should().Be("memex-portal-ai:3.0.0-ci.8080");
        fromNode.Identity.Should().BeNull();
        PlatformVersionLine.Normalize(fromNode.Version).Should().Be("3.0.0-ci.8080");

        DeploymentPinnedReferences.DeploymentPinOf(asElement, Options)!.Version.Should().Be("3.0.0-ci.8080");
    }

    [Fact]
    public void ARecordThatPinsNothing_IsNoReference()
    {
        var node = new MeshNode("memex", "Deployments")
        {
            NodeType = DeploymentPinnedReferences.DeploymentNodeType,
            Content = JsonNode.Parse("""{ "host": "memex.example", "pinnedImageTag": "" }"""),
        };
        DeploymentPinnedReferences.DeploymentPinOf(node, Options).Should().BeNull();
        DeploymentPinnedReferences.DeploymentPinOf(node with { Content = null }, Options).Should().BeNull();
    }

    [Fact]
    public void AnInstanceReport_CarriesBothItsVersionAndItsIdentity()
    {
        var report = new DeploymentReport
        {
            Deployment = "atioz",
            PlatformVersion = "3.0.0-ci.7000+deadbeef",
            FrameworkIdentity = "s9c0b05d61cb34bbffde9ad7a32ab1a8b",
        };
        var node = new MeshNode("atioz", "Deployments/Modules")
        {
            NodeType = DeploymentReportService.InventoryNodeType,
            Content = JsonNode.Parse(JsonSerializer.Serialize(report, Options)),
        };

        var reference = DeploymentPinnedReferences.ReportedBuildOf(node, Options)!;
        reference.Origin.Should().Be("instance report Deployments/Modules/atioz");
        reference.Version.Should().Be("3.0.0-ci.7000+deadbeef");
        reference.Identity.Should().Be("s9c0b05d61cb34bbffde9ad7a32ab1a8b");
    }

    [Fact]
    public void AReportWithNeitherVersionNorIdentity_IsNoReference()
    {
        var node = new MeshNode("blank", "Deployments/Modules")
        {
            NodeType = DeploymentReportService.InventoryNodeType,
            Content = JsonNode.Parse("""{ "deployment": "blank", "modules": [] }"""),
        };
        DeploymentPinnedReferences.ReportedBuildOf(node, Options).Should().BeNull();
    }
    [Fact]
    public void ARemoteAdoptedFallback_IsProtectedAlongsideTheRunningFramework()
    {
        var node = new MeshNode("remote", "Ops/Modules")
        {
            Content = JsonNode.Parse("""{"frameworkIdentity":"s-current","platformVersion":"4.0.0-ci.1","adoptedFrameworkInventoryComplete":true,"adoptedFrameworkIdentities":["s-old"]}""")
        };
        var references = DeploymentPinnedReferences.ReportedBuildsOf(node, Options);
        references.Select(r => r.Identity).Should().Equal("s-current", "s-old");
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-100);
        PrebuiltIdentityEntry Identity(string id) => new(id, "/store/" + id,
            [new PrebuiltSourceEntry("plugins", true, old, old, null)], 1024, old);
        var scan = new PrebuiltStoreScan([Identity("s-current"), Identity("s-old"), Identity("s-newer")],
            [new ReleaseMarkerEntry("3.0.0-ci.1", "s-old", "/markers/old", old, null),
             new ReleaseMarkerEntry("3.0.0-ci.2", "s-newer", "/markers/newer", old, null)]);
        var policy = new PrebuiltBundleRetention { KeepNewestPerSource = 0 };

        var before = PrebuiltBundleStore.Plan("s-current", "4.0.0-ci.1", scan, [], [], policy, now);
        before.Collectable.Select(i => i.Identity).Should().Contain("s-old");
        var protectedPlan = PrebuiltBundleStore.Plan("s-current", "4.0.0-ci.1", scan, [], references, policy, now);
        protectedPlan.Collectable.Select(i => i.Identity).Should().NotContain("s-old");
        protectedPlan.Protected["s-old"].Should().Contain("adopted build reported by");
    }

    [Theory]
    [InlineData("""{"frameworkIdentity":"s-current"}""")]
    [InlineData("""{"frameworkIdentity":"s-current","adoptedFrameworkInventoryComplete":false,"adoptedFrameworkIdentities":[]}""")]
    [InlineData("""{"frameworkIdentity":"s-current","adoptedFrameworkInventoryComplete":true}""")]
    [InlineData("""{"frameworkIdentity":"s-current","adoptedFrameworkInventoryComplete":true,"adoptedFrameworkIdentities":[null]}""")]
    [InlineData("""{"frameworkIdentity":"s-current","adoptedFrameworkInventoryComplete":true,"adoptedFrameworkIdentities":[""]}""")]
    [InlineData("""{"adoptedFrameworkInventoryComplete":true,"adoptedFrameworkIdentities":[]}""")]
    public void AnIncompleteOrMalformedConsumerInventory_CannotAuthorizeCleanup(string json)
    {
        var node = new MeshNode("remote", "Ops/Modules") { Content = JsonNode.Parse(json) };
        Assert.Throws<InvalidOperationException>(() => DeploymentPinnedReferences.ReportedBuildsOf(node, Options));
    }

    [Fact]
    public void AnExplicitCompleteEmptyAdoptionInventory_IsValid_AndStillProtectsTheRunningBuild()
    {
        var node = new MeshNode("remote", "Ops/Modules")
        {
            Content = JsonSerializer.Deserialize<JsonElement>("""{"FrameworkIdentity":"s-current","AdoptedFrameworkInventoryComplete":true,"AdoptedFrameworkIdentities":[]}""")
        };
        DeploymentPinnedReferences.ReportedBuildsOf(node, Options).Select(r => r.Identity).Should().Equal("s-current");
    }

    // ── The FLEET inventory: coverage and freshness, #3438/#3858 ───────────────────────────────
    //
    // The per-report signal above answers "did THIS instance answer fully". These answer the two
    // questions that decide whether anything may be deleted at all: did every expected instance
    // answer, and does its answer still describe it. A refusal here is the whole point — an
    // instance whose report never arrived contributes exactly the same reference list as an
    // instance that consumes nothing, and one of those two readings authorises deleting what it is
    // running.

    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static MeshNode Deployment(string id, string? pin = null, string? extra = null)
    {
        var content = new JsonObject { ["host"] = id + ".example" };
        if (pin is not null)
            content["pinnedImageTag"] = pin;
        if (extra is not null)
            foreach (var property in JsonNode.Parse(extra)!.AsObject())
                content[property.Key] = property.Value?.DeepClone();
        return new MeshNode(id, "Deployments")
        {
            NodeType = DeploymentPinnedReferences.DeploymentNodeType,
            Content = content,
        };
    }

    private static MeshNode Report(string id, TimeSpan age, string running = "s-running",
        string[]? adopted = null, bool complete = true, bool stamp = true) =>
        new(id, "Ops/Modules")
        {
            NodeType = DeploymentReportService.InventoryNodeType,
            Content = JsonNode.Parse(JsonSerializer.Serialize(new DeploymentReport
            {
                Deployment = id,
                PlatformVersion = "3.0.0-ci.8399",
                FrameworkIdentity = running,
                AdoptedFrameworkIdentities = [.. adopted ?? []],
                AdoptedFrameworkInventoryComplete = complete,
                SampledAt = stamp ? DeploymentReportService.Stamp(Now - age) : "",
            }, Options)),
        };

    private static ImmutableList<PinnedPlatformReference> Resolve(
        IReadOnlyList<MeshNode> deployments, IReadOnlyList<MeshNode> reports) =>
        DeploymentPinnedReferences.Resolve(deployments, reports, Options, Now,
            DeploymentPinnedReferences.StaleAfter);

    [Fact]
    public void ACompleteFreshFleet_ProtectsEveryRunningAndAdoptedBuildOfEveryInstance()
    {
        var references = Resolve(
            [Deployment("memex"), Deployment("memex-cloud", pin: "3.0.0-ci.8399")],
            [Report("memex", TimeSpan.FromMinutes(20), "s-memex", ["s-old-memex"]),
             Report("memex-cloud", TimeSpan.FromHours(3), "s-cloud")]);

        references.Select(r => r.Identity).Where(i => i is not null).Should().Contain(
            ["s-memex", "s-old-memex", "s-cloud"],
            because: "a complete, fresh inventory protects what each instance runs AND the older builds its modules still adopt");
        references.Select(r => r.Version).Should().Contain("3.0.0-ci.8399",
            because: "a Deployment record's pin is a reference in its own right");
    }

    [Fact]
    public void AnExpectedInstanceThatHasNeverReported_RefusesTheWholePass_NamingIt()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(
            [Deployment("memex"), Deployment("pearl")],
            [Report("memex", TimeSpan.FromMinutes(5))]));

        refusal.Message.Should().Contain("INCOMPLETE").And.Contain("Deployments/pearl");
        refusal.Message.Should().Contain("1 of 2 expected instance(s)",
            because: "the denominator belongs in the refusal, not only in a log line");
    }

    [Fact]
    public void AReportOlderThanTheFreshnessBudget_RefusesTheWholePass_NamingTheAge()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(
            [Deployment("memex-cloud")],
            [Report("memex-cloud", DeploymentPinnedReferences.StaleAfter + TimeSpan.FromHours(1))]));

        refusal.Message.Should().Contain("Deployments/memex-cloud").And.Contain("25.0 h ago");
    }

    [Fact]
    public void AReportThatIsExactlyAtTheBudget_IsStillFresh()
    {
        Resolve([Deployment("memex-cloud")], [Report("memex-cloud", DeploymentPinnedReferences.StaleAfter)])
            .Should().NotBeEmpty(because: "the budget is a limit, and a boundary that flips is a flake");
    }

    [Fact]
    public void AReportWithNoReadableSampledAt_IsAReportOfUnknownAge_AndRefuses()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(
            [Deployment("memex")], [Report("memex", TimeSpan.Zero, stamp: false)]));

        refusal.Message.Should().Contain("SampledAt");
    }

    [Fact]
    public void AnExplicitlyRetiredInstance_LeavesTheExpectedSet_AndItsSilenceIsNotARefusal()
    {
        Resolve([Deployment("pearl", extra: """{ "retired": true }""")], []).Should().BeEmpty();
        Resolve([Deployment("pearl", extra: """{ "retiredAt": "2026-09-01T00:00:00Z" }""")], []).Should().BeEmpty();
    }

    [Fact]
    public void AnInstanceThatIsMerelyUnREACHABLE_IsNotRetired()
    {
        // The distinction #3858 asks for: retirement is a declaration on the record, never an
        // inference from silence. A portal that is down reports nothing, and so does a portal that
        // was decommissioned — only one of them may have its artifacts collected.
        Assert.Throws<InvalidOperationException>(() => Resolve([Deployment("pearl")], []));
    }

    [Fact]
    public void AReportFromAnInstanceWithNoRecord_IsStillProtected()
    {
        Resolve([Deployment("memex")],
                [Report("memex", TimeSpan.FromMinutes(1), "s-memex"),
                 Report("stranger", TimeSpan.FromDays(400), "s-stranger")])
            .Select(r => r.Identity).Should().Contain("s-stranger",
                because: "an unexpected consumer is still a consumer; only the EXPECTED set decides completeness");
    }

    [Fact]
    public void AHostThatIsNobodysFleet_IsAMeasuredZero_NotARefusal()
    {
        // This source is registered on EVERY portal, not only the control instance. An ordinary
        // installation holds no Deployment records and receives no reports; refusing there would
        // wedge retention on every portal in the fleet for ever.
        Resolve([], []).Should().BeEmpty();
    }

    [Fact]
    public void AControlInstanceHoldingReportsButNoRecords_IsRefused_RatherThanReadAsAFleetOfZero()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(
            [], [Report("memex", TimeSpan.FromMinutes(5)), Report("memex-cloud", TimeSpan.FromMinutes(5))]));

        refusal.Message.Should().Contain("control instance").And.Contain("missing denominator");
    }

    [Fact]
    public void AnIncompleteAdoptionInventoryOnAnExpectedInstance_RefusesNamingTheInstance()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(
            [Deployment("memex-cloud")],
            [Report("memex-cloud", TimeSpan.FromMinutes(5), complete: false)]));

        refusal.Message.Should().Contain("Deployments/memex-cloud").And.Contain("retention must not delete");
    }

    [Fact]
    public void TwoReportsForOneInstance_AreJudgedByTheNEWEST_AndBothStillProtect()
    {
        // A control instance files its own report locally while remote ones arrive through the
        // inbox, so two nodes can describe one deployment. Picking whichever came first out of the
        // query would make the verdict depend on ordering — a stale duplicate beside a current
        // report is not a stale instance, and a current duplicate beside a stale one is not a fresh
        // one either. Both contribute references; the freshness verdict comes from the newest.
        var stale = Report("memex", TimeSpan.FromDays(9), "s-was-running") with { Id = "legacy" };
        var current = Report("memex", TimeSpan.FromMinutes(20), "s-running");

        var references = Resolve([Deployment("memex")], [stale, current]);
        references.Select(r => r.Identity).Should().Contain(["s-running", "s-was-running"],
            because: "the older report still names a build something may be serving");

        // …and the order it comes back in must not decide the verdict.
        Resolve([Deployment("memex")], [current, stale]).Select(r => r.Identity)
            .Should().Contain(["s-running", "s-was-running"]);

        // The stale one ALONE is the refusal, which is what makes the pair above a real distinction
        // rather than a guard that never fires.
        Assert.Throws<InvalidOperationException>(() => Resolve([Deployment("memex")], [stale]));
    }

    [Fact]
    public void AStaleInstance_CannotHaveItsRunningBuildCollected_EvenThoughItsOldReportNamesAnother()
    {
        // The end-to-end shape of the failure, in the store's own terms. The instance reported
        // `s-two-rolls-ago` a week ago and has rolled twice since; without the freshness verdict the
        // pass would protect `s-two-rolls-ago`, collect everything else, and take with it whatever
        // the instance is running now — which nothing in this process can name.
        var stale = new[] { Report("memex-cloud", TimeSpan.FromDays(7), "s-two-rolls-ago") };
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve([Deployment("memex-cloud")], stale));
        refusal.Message.Should().Contain("Retention must not delete");

        // …and the identical fleet with a fresh report is NOT refused: the guard distinguishes, it
        // does not simply always refuse.
        Resolve([Deployment("memex-cloud")], [Report("memex-cloud", TimeSpan.FromHours(2), "s-two-rolls-ago")])
            .Select(r => r.Identity).Should().Equal("s-two-rolls-ago");
    }
}
