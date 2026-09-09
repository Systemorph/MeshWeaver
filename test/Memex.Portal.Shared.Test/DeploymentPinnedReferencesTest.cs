using System;
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

}
