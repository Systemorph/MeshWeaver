using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Tests that partition-level nodes (Organizations) appear in global search.
/// Mimics the real scenario: Organization "TestCorp" in its own partition,
/// user has AccessAssignment granting Read access.
/// Verifies the full fan-out query path returns the node.
/// </summary>
public class GlobalSearchPartitionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(25.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddSampleUsers();

    // ── CORE TEST: Organization node appears in global search ──────────

    [Fact(Timeout = 30000)]
    public async Task GlobalSearch_ReturnsOrganizationNode()
    {
        // Arrange: create an Organization at the root of its partition
        // This mimics "PartnerRe" — namespace="" id="TestCorp" nodeType=Organization
        var orgNode = new MeshNode("TestCorp")
        {
            Name = "Test Corporation",
            NodeType = "Markdown"
        };
        await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);

        // Act: global search with no path (like the top search bar)
        var results = await MeshQuery
            .QueryAsync<MeshNode>("scope:descendants sort:LastModified-desc limit:50")
            .ToListAsync();

        // Assert: the Organization should appear
        results.Should().Contain(n => n.Path == "TestCorp",
            "Organization at partition root should appear in global search");
    }

    [Fact(Timeout = 30000)]
    public async Task GlobalSearch_ReturnsOrganizationByName()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(new MeshNode("AcmeCorp")
        {
            Name = "Acme Corporation",
            NodeType = "Markdown"
        }, TestTimeout);

        // Act: search by text that matches the name
        var results = await MeshQuery
            .QueryAsync<MeshNode>("Acme scope:descendants limit:50")
            .ToListAsync();

        // Assert
        results.Should().Contain(n => n.Path == "AcmeCorp",
            "Searching 'Acme' should find AcmeCorp organization");
    }

    [Fact(Timeout = 30000)]
    public async Task GlobalSearch_ReturnsChildNodesUnderOrganization()
    {
        // Arrange: create org + child markdown node
        await NodeFactory.CreateNodeAsync(new MeshNode("MegaCorp")
        {
            Name = "Mega Corporation",
            NodeType = "Markdown"
        }, TestTimeout);

        await NodeFactory.CreateNodeAsync(new MeshNode("readme", "MegaCorp")
        {
            Name = "Getting Started",
            NodeType = "Markdown"
        }, TestTimeout);

        // Act: search all descendants
        var results = await MeshQuery
            .QueryAsync<MeshNode>("scope:descendants limit:50")
            .ToListAsync();

        // Assert: both org and child should appear
        results.Should().Contain(n => n.Path == "MegaCorp",
            "Organization root should appear");
        results.Should().Contain(n => n.Path == "MegaCorp/readme",
            "Child node under organization should appear");
    }

    // ── Autocomplete ──────────────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_FindsOrganizationByPrefix()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(new MeshNode("AlphaCorp")
        {
            Name = "Alpha Corporation",
            NodeType = "Markdown"
        }, TestTimeout);

        // Act: autocomplete with "Alpha" prefix (like typing in search bar)
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "Alpha", 20)
            .ToListAsync();

        // Assert
        suggestions.Should().Contain(s => s.Path == "AlphaCorp",
            "Autocomplete with 'Alpha' should find AlphaCorp");
    }

    [Fact(Timeout = 30000)]
    public async Task Autocomplete_FindsOrganizationByPartialName()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(new MeshNode("BetaInc")
        {
            Name = "Beta Incorporated",
            NodeType = "Markdown"
        }, TestTimeout);

        // Act: autocomplete with partial name
        var suggestions = await MeshQuery
            .AutocompleteAsync("", "Beta", 20)
            .ToListAsync();

        // Assert
        suggestions.Should().Contain(s => s.Path == "BetaInc",
            "Autocomplete with 'Beta' should find BetaInc");
    }

    // ── nodeType:Markdown specific query ──────────────────────────

    [Fact(Timeout = 30000)]
    public async Task NodeTypeQuery_FindsOrganizations()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(new MeshNode("GammaCorp")
        {
            Name = "Gamma Corp",
            NodeType = "Markdown"
        }, TestTimeout);

        // Act: search by nodeType
        var results = await MeshQuery
            .QueryAsync<MeshNode>("nodeType:Markdown scope:descendants limit:50")
            .ToListAsync();

        // Assert
        results.Should().Contain(n => n.Path == "GammaCorp",
            "nodeType:Markdown query should find GammaCorp");
    }

    // ── Multiple partitions ──────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task GlobalSearch_ReturnsNodesFromMultiplePartitions()
    {
        // Arrange: two orgs in different partitions
        await NodeFactory.CreateNodeAsync(new MeshNode("OrgA")
        {
            Name = "Organization A",
            NodeType = "Markdown"
        }, TestTimeout);

        await NodeFactory.CreateNodeAsync(new MeshNode("OrgB")
        {
            Name = "Organization B",
            NodeType = "Markdown"
        }, TestTimeout);

        // Also create a child in each
        await NodeFactory.CreateNodeAsync(new MeshNode("doc1", "OrgA")
        {
            Name = "Doc in A",
            NodeType = "Markdown"
        }, TestTimeout);

        await NodeFactory.CreateNodeAsync(new MeshNode("doc2", "OrgB")
        {
            Name = "Doc in B",
            NodeType = "Markdown"
        }, TestTimeout);

        // Act: global search
        var results = await MeshQuery
            .QueryAsync<MeshNode>("scope:descendants limit:100")
            .ToListAsync();

        // Assert: all nodes from both partitions should appear
        results.Should().Contain(n => n.Path == "OrgA", "OrgA should appear");
        results.Should().Contain(n => n.Path == "OrgB", "OrgB should appear");
        results.Should().Contain(n => n.Path == "OrgA/doc1", "OrgA child should appear");
        results.Should().Contain(n => n.Path == "OrgB/doc2", "OrgB child should appear");
    }

    // ── Text search across partitions ────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task TextSearch_FindsNodesAcrossPartitions()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(new MeshNode("DeltaCorp")
        {
            Name = "Delta Corporation",
            NodeType = "Markdown"
        }, TestTimeout);

        await NodeFactory.CreateNodeAsync(new MeshNode("report", "DeltaCorp")
        {
            Name = "Delta Quarterly Report",
            NodeType = "Markdown"
        }, TestTimeout);

        // Act: search for "Delta"
        var results = await MeshQuery
            .QueryAsync<MeshNode>("Delta scope:descendants limit:50")
            .ToListAsync();

        // Assert: both the org and the child with "Delta" in name should appear
        results.Should().Contain(n => n.Path == "DeltaCorp",
            "Text search 'Delta' should find DeltaCorp");
        results.Should().Contain(n => n.Path == "DeltaCorp/report",
            "Text search 'Delta' should find child with 'Delta' in name");
    }

    // ── Access control: only accessible partitions ───────────────────

    [Fact(Timeout = 30000)]
    public async Task GlobalSearch_WithAccessAssignment_ReturnsGrantedNodes()
    {
        // Arrange: create org + grant current user access
        await NodeFactory.CreateNodeAsync(new MeshNode("SecureCorp")
        {
            Name = "Secure Corporation",
            NodeType = "Markdown"
        }, TestTimeout);

        // Grant the admin user (already logged in) Viewer role on SecureCorp
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync(
            TestUsers.Admin.ObjectId, "Viewer", "SecureCorp", "system", TestTimeout);

        // Act: search
        var results = await MeshQuery
            .QueryAsync<MeshNode>("scope:descendants limit:50")
            .ToListAsync();

        // Assert
        results.Should().Contain(n => n.Path == "SecureCorp",
            "User with Viewer role should see SecureCorp in global search");
    }

    // ── Query routing hints ──────────────────────────────────────────

    [Fact(Timeout = 30000)]
    public async Task RoutingHints_PathRestrictsToPartition()
    {
        // Arrange
        await NodeFactory.CreateNodeAsync(new MeshNode("EpsilonCorp")
        {
            Name = "Epsilon Corp",
            NodeType = "Markdown"
        }, TestTimeout);

        await NodeFactory.CreateNodeAsync(new MeshNode("project", "EpsilonCorp")
        {
            Name = "Main Project",
            NodeType = "Markdown"
        }, TestTimeout);

        // Act: search with explicit namespace (routing rule should restrict to EpsilonCorp partition)
        var results = await MeshQuery
            .QueryAsync<MeshNode>("namespace:EpsilonCorp scope:descendants limit:50")
            .ToListAsync();

        // Assert: should find nodes in EpsilonCorp
        results.Should().Contain(n => n.Path == "EpsilonCorp/project",
            "Namespace-scoped search should find children");
    }
}
