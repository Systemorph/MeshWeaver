using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Unit tests for the <see cref="MeshNodeVisibility"/> "dotfile" convention: a path segment
/// starting with <c>'_'</c> (e.g. <c>{user}/_Memex/ThreadComposer</c>, <c>{p}/_Access/…</c>) marks a
/// node as hidden and excludes it from the <c>search</c> context — decoupled from satellite-table
/// routing. Pure logic, no fixture.
/// </summary>
public class MeshNodeVisibilityUnitTests
{
    [Theory]
    [InlineData("rsalzmann/_Memex/ThreadComposer", true)]   // the ThreadComposer singleton
    [InlineData("ACME/_Access/G1", true)]              // a known satellite namespace is also a dotfile
    [InlineData("_Memex", true)]                       // single leading-underscore segment
    [InlineData("a/_b/c", true)]                       // underscore in a middle segment
    [InlineData("ACME/Project/Foo", false)]           // ordinary main node
    [InlineData("Markdown", false)]                    // a type node
    [InlineData("ACME/Source/X", false)]              // "Source" routes to a satellite table but has NO underscore → visible
    [InlineData("", false)]                            // empty
    [InlineData(null, false)]                          // null
    public void IsHiddenPath_DetectsLeadingUnderscoreSegments(string? path, bool expected)
        => MeshNodeVisibility.IsHiddenPath(path).Should().Be(expected);

    [Fact]
    public void IsExcludedFromContext_DotfilePath_ExcludedFromSearchOnly()
    {
        var hidden = new MeshNode("ThreadComposer", "rsalzmann/_Memex");

        hidden.IsExcludedFromContext("search").Should().BeTrue(
            "a _-prefixed (dotfile) path is hidden from search");
        hidden.IsExcludedFromContext("create").Should().BeFalse(
            "the dotfile rule is scoped to the search context only");
        hidden.IsExcludedFromContext(null).Should().BeFalse();
        hidden.IsExcludedFromContext("").Should().BeFalse();
    }

    [Fact]
    public void IsExcludedFromContext_ExplicitOptOut_HonoredIndependentlyOfPath()
    {
        // A non-dotfile node that opts out of "create" only.
        var node = new MeshNode("X", "ACME")
        {
            ExcludeFromContext = new HashSet<string> { "create" }
        };

        node.IsExcludedFromContext("create").Should().BeTrue("explicit ExcludeFromContext opt-out");
        node.IsExcludedFromContext("search").Should().BeFalse(
            "not a dotfile and no search opt-out → visible in search");
    }

    [Fact]
    public void IsExcludedFromContext_OrdinaryNode_NeverExcluded()
    {
        var node = new MeshNode("Foo", "ACME/Project");

        node.IsExcludedFromContext("search").Should().BeFalse();
        node.IsExcludedFromContext("create").Should().BeFalse();
    }
}

/// <summary>
/// Integration test: a <c>_</c>-prefixed (dotfile) node — e.g. the per-user
/// <c>{user}/_Memex/ThreadComposer</c> singleton — must be dropped from the <c>search</c> context by the
/// query backend (here the in-memory storage-adapter provider), yet remain directly queryable
/// without that context (the composer's own read). Mirrors <see cref="CreatableTypesIntegrationTest"/>'s
/// in-memory setup.
/// </summary>
public class SearchExclusionIntegrationTest : MonolithMeshTestBase
{
    private static readonly JsonSerializerOptions SetupJsonOptions = new();

    public SearchExclusionIntegrationTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var persistence = new InMemoryStorageAdapter();

        // Parent so path:ACME resolves (Group is a stock AddGraph type — no extra module needed).
        Save(persistence, MeshNode.FromPath("ACME") with { Name = "ACME", NodeType = "Group" });
        // Ordinary, searchable node.
        Save(persistence, MeshNode.FromPath("ACME/Visible") with { Name = "Visible", NodeType = "Markdown" });
        // Hidden node under a _Memex dotfile namespace — same shape as {user}/_Memex/ThreadComposer.
        Save(persistence, MeshNode.FromPath("ACME/_Memex/Hidden") with { Name = "Hidden", NodeType = "Markdown" });

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services => services.AddInMemoryPersistence(persistence))
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    private static void Save(InMemoryStorageAdapter persistence, MeshNode node) =>
        persistence.SaveNode(node, SetupJsonOptions).FirstAsync().ToTask().GetAwaiter().GetResult();

    [Fact(Timeout = 20000)]
    public void Search_Context_Excludes_UnderscorePrefixedPaths()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // WITH context:search → the dotfile node is hidden; the ordinary node is not.
        var searchItems = meshQuery.Query<MeshNode>("path:ACME scope:descendants context:search")
            .Should().Within(20.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        Output.WriteLine($"search results: [{string.Join(", ", searchItems.Select(n => n.Path))}]");
        searchItems.Should().Contain(n => n.Path == "ACME/Visible",
            "an ordinary node is visible in search");
        searchItems.Should().NotContain(n => n.Path == "ACME/_Memex/Hidden",
            "a _-prefixed (dotfile) path must be hidden from the search context");
    }

    [Fact(Timeout = 20000)]
    public void NoSearchContext_StillReturns_UnderscorePrefixedPaths()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // WITHOUT context:search → the dotfile node is still queryable (e.g. the composer's direct read).
        var allItems = meshQuery.Query<MeshNode>("path:ACME scope:descendants")
            .Should().Within(20.Seconds())
            .Match(c => c.ChangeType == QueryChangeType.Initial).Items;

        Output.WriteLine($"unscoped results: [{string.Join(", ", allItems.Select(n => n.Path))}]");
        allItems.Should().Contain(n => n.Path == "ACME/_Memex/Hidden",
            "the dotfile rule is scoped to the search context — direct reads still resolve the node");
    }
}
