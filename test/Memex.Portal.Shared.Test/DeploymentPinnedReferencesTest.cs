using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
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
        var asNode = new MeshNode("fabrikam", "Deployments")
        {
            NodeType = DeploymentPinnedReferences.DeploymentNodeType,
            Content = JsonNode.Parse("""{ "host": "fabrikam.example", "pinnedImageTag": "memex-portal-ai:3.0.0-ci.8080" }"""),
        };
        var asElement = asNode with
        {
            Content = JsonSerializer.Deserialize<JsonElement>("""{ "PinnedImageTag": "3.0.0-ci.8080" }"""),
        };

        var fromNode = DeploymentPinnedReferences.DeploymentPinOf(asNode, Options)!;
        fromNode.Origin.Should().Be("Deployment Deployments/fabrikam");
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
            Deployment = "initech",
            PlatformVersion = "3.0.0-ci.7000+deadbeef",
            FrameworkIdentity = "s9c0b05d61cb34bbffde9ad7a32ab1a8b",
        };
        var node = new MeshNode("initech", "Deployments/Modules")
        {
            NodeType = DeploymentReportService.InventoryNodeType,
            Content = JsonNode.Parse(JsonSerializer.Serialize(report, Options)),
        };

        var reference = DeploymentPinnedReferences.ReportedBuildOf(node, Options)!;
        reference.Origin.Should().Be("instance report Deployments/Modules/initech");
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

    /// <summary>The store the fixtures' retention runs on: the registry every fixture record mounts.</summary>
    private const string StoreHost = "registry.example";

    private static MeshNode Deployment(string id, string? pin = null, string? extra = null,
        string? mounts = "https://" + StoreHost)
    {
        var content = new JsonObject { ["host"] = id + ".example" };
        if (mounts is not null)
            content["pluginRepos"] = new JsonArray(new JsonObject { ["name"] = "Plugins", ["url"] = mounts });
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
        IReadOnlyList<MeshNode> deployments, IReadOnlyList<MeshNode> reports,
        bool isControlInstance = false) =>
        Resolve(deployments, reports,
            new ConsumerScope("registry", [StoreHost], isControlInstance));

    private static ImmutableList<PinnedPlatformReference> Resolve(
        IReadOnlyList<MeshNode> deployments, IReadOnlyList<MeshNode> reports, ConsumerScope scope) =>
        DeploymentPinnedReferences.Resolve(deployments, reports, Options, Now,
            DeploymentPinnedReferences.StaleAfter, scope);

    /// <summary>A report as a STORE files it: keyed by the instance its credential authenticated as.</summary>
    private static MeshNode StoreReport(string registeredInstance, TimeSpan age, string running,
        string? claimedDeployment = null)
    {
        var report = Report(claimedDeployment ?? registeredInstance, age, running);
        ((JsonObject)report.Content!)["registeredInstance"] = registeredInstance;
        return report with
        {
            Id = registeredInstance,
            Namespace = $"{DeploymentReportService.DefaultOperationalSpace}/{DeploymentReportService.StoreReportsSegment}",
        };
    }

    /// <summary>The same body as <paramref name="report"/>, filed somewhere the platform never files one.</summary>
    private static MeshNode Elsewhere(MeshNode report, string @namespace) => report with { Namespace = @namespace };

    // ── #6989: a report is where the platform FILES one ──────────────────────────────────────────
    //
    // Inventories are read mesh-wide, and `Hosting/ModuleInventory` is a type anyone may create in a
    // partition they own. Measured on the registry: a leftover probe under a user's partition was
    // matched as the `memex` record's sink report and refused every pass, standing in for the report
    // that never arrived.

    [Fact]
    public void AFreshCompleteReportInAUsersPartition_DoesNotSatisfyARecord()
    {
        // The dangerous direction: a hand-made body that LOOKS like a fresh, complete report for the
        // silent `customer` would complete the pass and authorise deleting what `customer` runs.
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(SyncedFleet(),
            [StoreReport("company", TimeSpan.FromMinutes(30), "s-company"),
             StoreReport("control", TimeSpan.FromHours(2), "s-control"),
             Elsewhere(StoreReport("customer", TimeSpan.FromMinutes(1), "s-forged"), "someone/OpsProbe/ConsumerReports"),
             Elsewhere(Report("customer", TimeSpan.FromMinutes(1), "s-forged-sink"), "someone/OpsProbe/Modules")],
            new ConsumerScope("registry", [], IsControlInstance: false)));

        refusal.Message.Should().Contain("Deployments/customer: no Hosting/ModuleInventory report has ever arrived",
            because: "a node outside the filing locations is not a report, so `customer` is still silent");
    }

    [Fact]
    public void ALegacyProbeInAUsersPartition_DoesNotRefuseAPassItsRecordsRealReportCompletes()
    {
        // The measured shape: an incomplete legacy body under a user's partition, named after a
        // record whose own authenticated report is fresh and complete.
        var references = Resolve(SyncedFleet(),
            [StoreReport("company", TimeSpan.FromMinutes(30), "s-company"),
             StoreReport("control", TimeSpan.FromHours(2), "s-control"),
             StoreReport("customer", TimeSpan.FromHours(1), "s-customer"),
             Elsewhere(Report("customer", TimeSpan.FromDays(40), "s-probe", complete: false), "someone/OpsProbe/Modules")],
            new ConsumerScope("registry", [], IsControlInstance: false));

        references.Select(r => r.Identity).Should().Contain(["s-company", "s-control", "s-customer"])
            .And.NotContain("s-probe", because: "a probe is not a consumer, and its legacy body is no refusal");
    }

    [Fact]
    public void AnAuthenticatedStampOutsideTheStoresFilingLocation_IsNotAnAuthenticatedReport()
    {
        // `registeredInstance` is stamped by the STORE, at its filing location. The same field on a
        // node under the sink's location was never stamped by anybody who authenticated anything; it
        // is read as a sink report of the deployment its body names (`intruder`), never as `customer`.
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(SyncedFleet(),
            [StoreReport("company", TimeSpan.FromMinutes(30), "s-company"),
             StoreReport("control", TimeSpan.FromHours(2), "s-control"),
             Elsewhere(StoreReport("customer", TimeSpan.FromMinutes(1), "s-claimed", claimedDeployment: "intruder"),
                 $"{DeploymentReportService.DefaultOperationalSpace}/Modules")],
            new ConsumerScope("registry", [], IsControlInstance: false)));

        refusal.Message.Should().Contain("Deployments/customer: no Hosting/ModuleInventory report has ever arrived");
    }

    [Fact]
    public void TheFilingLocations_FollowTheConfiguredOperationalSpace()
    {
        var underOps = new[]
        {
            StoreReport("company", TimeSpan.FromMinutes(30), "s-company"),
            StoreReport("control", TimeSpan.FromHours(2), "s-control"),
            StoreReport("customer", TimeSpan.FromHours(1), "s-customer"),
        };
        var underFleet = underOps
            .Select(r => Elsewhere(r, $"Fleet/{DeploymentReportService.StoreReportsSegment}"))
            .ToImmutableList();

        Resolve(SyncedFleet(), underFleet, new ConsumerScope("registry", [], false) { OperationalSpace = "Fleet" })
            .Select(r => r.Identity).Should().Contain(["s-company", "s-control", "s-customer"]);
        Assert.Throws<InvalidOperationException>(() =>
            Resolve(SyncedFleet(), underOps, new ConsumerScope("registry", [], false) { OperationalSpace = "Fleet" }));
    }

    [Fact]
    public void ScopeOf_ReadsTheOperationalSpace_DefaultingToOps()
    {
        DeploymentPinnedReferences.ScopeOf(
                new ConfigurationBuilder()
                    .AddInMemoryCollection([new(DeploymentReportService.OperationalSpaceKey, "Fleet")]).Build(), null)
            .OperationalSpace.Should().Be("Fleet");
        DeploymentPinnedReferences.ScopeOf(
                new ConfigurationBuilder().Build(), null)
            .OperationalSpace.Should().Be(DeploymentReportService.DefaultOperationalSpace);
    }

    // ── #6989: whose consumers a store accounts for ──────────────────────────────────────────────
    //
    // The fleet as it is: one report sink (the control instance), one plugin registry every other
    // instance mounts, and a synced copy of the whole Deployments folder on hosts that are neither.
    // The denominator used to be "every record this host holds", so every host but the sink refused
    // on every pass, for ever, and its shared volume was never reclaimed.

    private const string Company = "company.example";

    private static ImmutableList<MeshNode> SyncedFleet() =>
    [
        Deployment("registry", extra: $$"""{ "host": "{{StoreHost}}" }""", mounts: null),
        Deployment("company", extra: $$"""{ "host": "{{Company}}" }"""),
        Deployment("control", extra: """{ "host": "control.example" }"""),
        Deployment("customer"),
    ];

    [Fact]
    public void AHostHoldingASyncedCopyOfRecords_WhoseInstancesMountAnotherRegistry_CompletesItsPass()
    {
        // The company instance: it holds every record (a synced copy), it is not the report sink, and
        // nobody mounts it. Its consumers are itself alone — protected by its live identity and its
        // adoption stamps — so the inventory is COMPLETE. Before #6989 this was "INCOMPLETE — 4
        // refusal(s) over 4 expected instance(s)" on every tick.
        var references = Resolve(SyncedFleet(),
            [Report("company", TimeSpan.FromDays(12), "s-company-stale")],
            new ConsumerScope("company", [], IsControlInstance: false));

        references.Select(r => r.Identity).Should().NotContain("s-company-stale",
            because: "a report describing ANOTHER store's consumer is not a consumer of this one");
    }

    [Fact]
    public void TheReportSink_WhichNobodyMounts_CompletesItsPass_TooEvenWithStaleFleetReports()
    {
        // The control instance holds the records AND the reports, and is mounted by nobody either.
        Resolve(SyncedFleet(),
                [Report("customer", TimeSpan.FromDays(30), "s-customer")],
                new ConsumerScope("control", [], IsControlInstance: true))
            .Should().BeEmpty(because: "no record mounts the control instance's store, so the fleet's "
                + "reports describe another store's consumers — and the pass is COMPLETE, not refused");
    }

    [Fact]
    public void TheRegistry_WhoseMountingConsumersAllReportedThere_CompletesAndProtectsWhatTheyRun()
    {
        var references = Resolve(SyncedFleet(),
            [StoreReport("company", TimeSpan.FromMinutes(30), "s-company"),
             StoreReport("control", TimeSpan.FromHours(2), "s-control"),
             StoreReport("customer", TimeSpan.FromHours(5), "s-customer")],
            new ConsumerScope("registry", [], IsControlInstance: false));

        references.Select(r => r.Identity).Should().Contain(["s-company", "s-control", "s-customer"],
            because: "the registry serves each consumer bundles keyed on what it runs; those identities are referenced");
    }

    [Fact]
    public void TheRegistry_WithAMountingConsumerSilent_StillRefuses_NamingIt()
    {
        // The negative control: the fix narrows the denominator to the DECLARED consumers, and a
        // declared consumer that has not reported still refuses the whole pass.
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(SyncedFleet(),
            [StoreReport("company", TimeSpan.FromMinutes(30), "s-company"),
             StoreReport("control", TimeSpan.FromHours(2), "s-control")],
            new ConsumerScope("registry", [], IsControlInstance: false)));

        refusal.Message.Should().Contain("Deployments/customer").And.Contain("1 refusal(s) over 3 expected instance(s)")
            .And.Contain("1 not mounting this store");
    }

    [Fact]
    public void AReportCanOnlyStandForTheInstanceTheStoreAuthenticated_NotTheOneItsBodyClaims()
    {
        // `customer` holds a key; its body claims to be `company`. Filed under `customer`, it cannot
        // make the silent `company` read as reported.
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(SyncedFleet(),
            [StoreReport("customer", TimeSpan.FromMinutes(5), "s-customer", claimedDeployment: "company"),
             StoreReport("control", TimeSpan.FromHours(2), "s-control")],
            new ConsumerScope("registry", [], IsControlInstance: false)));

        refusal.Message.Should().Contain("Deployments/company");
    }

    [Fact]
    public void ADeclaredRegistryInstanceId_IsWhereARecordsStoreReportIsFound()
    {
        var fleet = SyncedFleet().Select(r => r.Id == "customer"
            ? Deployment("customer", extra: """{ "registryInstanceId": "customer-portal" }""")
            : r).ToImmutableList();

        Resolve(fleet,
                [StoreReport("company", TimeSpan.FromMinutes(30), "s-company"),
                 StoreReport("control", TimeSpan.FromHours(2), "s-control"),
                 StoreReport("customer-portal", TimeSpan.FromHours(1), "s-customer")],
                new ConsumerScope("registry", [], IsControlInstance: false))
            .Select(r => r.Identity).Should().Contain("s-customer");
    }

    [Fact]
    public void AStoreReportFromAnAccountThatIsNotTheDeclaredRegistryIdentity_DoesNotSatisfyTheRecord()
    {
        // `customer` declares that it reaches this store as `customer-portal`. A fresh report from
        // a DIFFERENT registry account that happens to be called `customer` is not that consumer:
        // `customer-portal` is still silent, and the pass must not complete.
        var fleet = SyncedFleet().Select(r => r.Id == "customer"
            ? Deployment("customer", extra: """{ "registryInstanceId": "customer-portal" }""")
            : r).ToImmutableList();

        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(fleet,
            [StoreReport("company", TimeSpan.FromMinutes(30), "s-company"),
             StoreReport("control", TimeSpan.FromHours(2), "s-control"),
             StoreReport("customer", TimeSpan.FromMinutes(5), "s-unrelated-account")],
            new ConsumerScope("registry", [], IsControlInstance: false)));

        refusal.Message.Should().Contain("Deployments/customer");
    }

    [Fact]
    public void AStoreReportOfARecordThatMovedToAnotherRegistry_StillProtectsWhatItIsServedFromHere()
    {
        // `customer`'s record now mounts another registry, but its instance has not applied that
        // yet: a fresh, authenticated report still arrives HERE. It no longer counts as an expected
        // consumer, and its builds must still be protected — they are being served from this store.
        var fleet = SyncedFleet().Select(r => r.Id == "customer"
            ? Deployment("customer", mounts: "https://other-registry.example")
            : r).ToImmutableList();

        var references = Resolve(fleet,
            [StoreReport("company", TimeSpan.FromMinutes(30), "s-company"),
             StoreReport("control", TimeSpan.FromHours(2), "s-control"),
             StoreReport("customer", TimeSpan.FromMinutes(10), "s-customer-still-served")],
            new ConsumerScope("registry", [], IsControlInstance: false));

        references.Select(r => r.Identity).Should().Contain("s-customer-still-served",
            because: "a report this store authenticated names a build it is serving, whatever the record now mounts");
    }

    [Fact]
    public void AHostThatCannotNameItsStore_RefusesRatherThanReadingEveryMountAsSomeoneElses()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(SyncedFleet(), [],
            new ConsumerScope(null, [], IsControlInstance: false)));

        refusal.Message.Should().Contain("cannot name the store");
    }

    [Fact]
    public void ARegistrySourceRepository_IsNotAMountOfARegistry()
    {
        var registry = Deployment("registry", extra: """
            { "pluginRepos": [ { "url": "https://github.com/example/plugins", "isRegistrySource": true },
                               { "url": "https://registry.example/" } ] }
            """, mounts: null);

        DeploymentPinnedReferences.MountedHostsOf(registry, Options).Should().Equal(StoreHost);
    }

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
            [Deployment("memex"), Deployment("fabrikam")],
            [Report("memex", TimeSpan.FromMinutes(5))]));

        refusal.Message.Should().Contain("INCOMPLETE").And.Contain("Deployments/fabrikam");
        refusal.Message.Should().Contain("1 refusal(s) over 2 expected instance(s)",
            because: "the denominator belongs in the refusal, not only in a log line — and a refusal "
                     + "COUNT is not a fraction of the expected set, because an orphan report can be "
                     + "the refused subject");
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
        Resolve([Deployment("fabrikam", extra: """{ "retired": true }""")], []).Should().BeEmpty();
        Resolve([Deployment("fabrikam", extra: """{ "retiredAt": "2026-09-01T00:00:00Z" }""")], []).Should().BeEmpty();
    }

    [Fact]
    public void AnInstanceThatIsMerelyUnREACHABLE_IsNotRetired()
    {
        // The distinction #3858 asks for: retirement is a declaration on the record, never an
        // inference from silence. A portal that is down reports nothing, and so does a portal that
        // was decommissioned — only one of them may have its artifacts collected.
        Assert.Throws<InvalidOperationException>(() => Resolve([Deployment("fabrikam")], []));
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
    public void AReportStampedInTheFUTURE_IsAnUnknownAge_NotAFreshOne()
    {
        // A negative age passes `age > budget` trivially, so a skewed clock would make every report
        // that producer files permanently fresh — protecting one identity for ever while the
        // installation moves on, which is exactly what the budget exists to detect.
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(
            [Deployment("memex")], [Report("memex", -TimeSpan.FromHours(6))]));
        refusal.Message.Should().Contain("FUTURE");
    }

    [Fact]
    public void AControlInstanceWhoseIndexHasNotCaughtUp_IsRefused_NotReadAsAFleetOfZero()
    {
        // `Records` reads an eventually-consistent index, so a control instance whose Deployment
        // AND inventory queries have both not landed answers exactly as an ordinary portal does.
        // Configuration is authoritative where the query is not.
        Resolve([], []).Should().BeEmpty(because: "an ordinary portal owns no fleet");
        var refusal = Assert.Throws<InvalidOperationException>(
            () => Resolve([], [], isControlInstance: true));
        refusal.Message.Should().Contain("control instance").And.Contain("returned NOTHING");

        // …and it says nothing once the records are there.
        Resolve([Deployment("memex")], [Report("memex", TimeSpan.FromMinutes(5))], isControlInstance: true)
            .Should().NotBeEmpty();
    }

    [Fact]
    public void TheRefusalCountsExpectedAndORPHANReportsSeparately()
    {
        // "1 of 0 expected instance(s)" is the shape of an operator message nobody can act on: the
        // refused subject was an orphan report, not a fraction of the expected set.
        var refusal = Assert.Throws<InvalidOperationException>(() => Resolve(
            [], [Report("stranger", TimeSpan.FromMinutes(5))]));
        refusal.Message.Should().Contain("1 refusal(s) over 0 expected instance(s)")
            .And.Contain("1 reporting without a record");
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
