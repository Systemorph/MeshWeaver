#pragma warning disable CS1591

using System;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pure tests for <see cref="AgentPickerProjection.BuildModelQueries"/>'s
/// selection-aware overload — the query strings the provider-selection picker
/// + credential resolver fan out over. No mesh required.
/// </summary>
public class AgentPickerQueriesTest
{
    // ─── BuildAgentQuery: the SINGLE canonical agent-registry query ───
    // Agents live in a dedicated /Agent sub-namespace PER PARTITION; the query lists the platform
    // default + the current space's + the user's DIRECTLY (exact membership, no graph search).

    private const string BuiltInAgentQuery = "namespace:Agent nodeType:Agent";

    [Fact]
    public void BuildAgentQuery_UserAndSpace_ListsBothPartitionsPlusPlatform()
    {
        var query = AgentPickerProjection.BuildAgentQuery(userPath: "rbuergi", spacePath: "AgenticPension");

        query.Should().Be(
            "namespace:rbuergi/Agent|AgenticPension/Agent|Agent nodeType:Agent",
            "ONE mesh-node search lists the user's, the space's and the platform /Agent namespaces "
            + "directly — exact membership, no ancestor/graph walk.");
        AgentPickerProjection.BuildAgentQueries("rbuergi", "AgenticPension")
            .Should().ContainSingle().Which.Should().Be(query);
    }

    [Fact]
    public void BuildAgentQuery_OnlyUser_ListsUserPlusPlatform()
    {
        AgentPickerProjection.BuildAgentQuery(userPath: "rbuergi")
            .Should().Be("namespace:rbuergi/Agent|Agent nodeType:Agent");
    }

    [Fact]
    public void BuildAgentQuery_OnlySpace_ListsSpacePlusPlatform()
    {
        AgentPickerProjection.BuildAgentQuery(spacePath: "AgenticPension")
            .Should().Be("namespace:AgenticPension/Agent|Agent nodeType:Agent");
    }

    [Fact]
    public void BuildAgentQuery_NeitherSet_PlatformDefaultsOnly()
    {
        // No partition context → just the platform default namespace (Children → n.namespace = Agent).
        AgentPickerProjection.BuildAgentQuery().Should().Be(BuiltInAgentQuery);
        AgentPickerProjection.BuildAgentQueries()
            .Should().ContainSingle().Which.Should().Be(BuiltInAgentQuery);
    }

    [Fact]
    public void BuildAgentQuery_UserEqualsSpace_DedupedInAlternation()
    {
        // When the user IS the space partition (a user's own space), the /Agent namespace isn't doubled.
        AgentPickerProjection.BuildAgentQuery(userPath: "rbuergi", spacePath: "rbuergi")
            .Should().Be("namespace:rbuergi/Agent|Agent nodeType:Agent");
    }

    [Fact]
    public void BuildAgentQuery_ExcludeUtility_AppendsUtilityFilter()
    {
        // The conversational combobox (bound directly to the query, no projection filter) drops
        // generator/utility agents at the query level; the engine callers leave it false.
        AgentPickerProjection.BuildAgentQuery(spacePath: "AgenticPension", excludeUtility: true)
            .Should().Be("namespace:AgenticPension/Agent|Agent nodeType:Agent -content.modelTier:utility");
    }

    [Fact]
    public void PartitionOf_ReturnsTopLevelSegment()
    {
        AgentPickerProjection.PartitionOf("AgenticPension/Foo/_Thread/x").Should().Be("AgenticPension");
        AgentPickerProjection.PartitionOf("rbuergi").Should().Be("rbuergi");
        AgentPickerProjection.PartitionOf(null).Should().BeNull();
        AgentPickerProjection.PartitionOf("").Should().BeNull();
    }

    // ─── DerivePickerContext: the timing-safe (currentPath, nodeTypePath) derivation ───
    // OpenPicker feeds BuildAgentQueries/BuildModelQueries with the pair this returns,
    // sourced from the RESOLVED INavigationService context. These pin that a resolved
    // context yields BOTH tokens (→ all 3 queries) and that an unresolved context falls
    // back to initialContext (never collapsing to built-in-only just because of timing).

    private static NavigationContext NavContext(string path, string? nodeType)
        => new()
        {
            Path = path,
            Resolution = new AddressResolution(path, Remainder: null),
            Node = MeshNode.FromPath(path) with { NodeType = nodeType },
        };

    [Fact]
    public void DerivePickerContext_ResolvedNode_YieldsContextPathAndNodeType()
    {
        var pc = AgentPickerProjection.DerivePickerContext(
            NavContext("ACME/ProductLaunch", "ACME/Project"), fallbackContextPath: null);

        pc.ContextPath.Should().Be("ACME/ProductLaunch");
        pc.NodeTypePath.Should().Be("ACME/Project");

        // The space partition derived from the resolved context drives the per-partition /Agent query.
        AgentPickerProjection.BuildAgentQuery(spacePath: AgentPickerProjection.PartitionOf(pc.ContextPath))
            .Should().Be("namespace:ACME/Agent|Agent nodeType:Agent");
    }

    [Fact]
    public void DerivePickerContext_ResolvedSatellitePath_NormalizesToMainNode()
    {
        // A satellite path (…/_Thread/<slug>) must collapse to the main node so the
        // path-ancestors query reasons about the content node, not the satellite.
        var pc = AgentPickerProjection.DerivePickerContext(
            NavContext("ACME/ProductLaunch/_Thread/abc123", "ACME/Project"), fallbackContextPath: null);

        // PrimaryPath is the node's MainNode; NormalizeContextPath strips any _ segment.
        pc.ContextPath.Should().Be("ACME/ProductLaunch");
    }

    [Fact]
    public void DerivePickerContext_NullContext_FallsBackToInitialContext()
    {
        // Navigation context still resolving (null) — fall back to the seeded path so the
        // path-ancestors query is still issued. No NodeType is available from a bare string.
        var pc = AgentPickerProjection.DerivePickerContext(resolved: null, fallbackContextPath: "ACME/ProductLaunch");

        pc.ContextPath.Should().Be("ACME/ProductLaunch");
        pc.NodeTypePath.Should().BeNull();

        // Context present ⇒ the space's /Agent namespace + the platform default.
        AgentPickerProjection.BuildAgentQuery(spacePath: AgentPickerProjection.PartitionOf(pc.ContextPath))
            .Should().Be("namespace:ACME/Agent|Agent nodeType:Agent");
    }

    [Fact]
    public void DerivePickerContext_ChatRoute_IgnoredInFavourOfFallback()
    {
        // The bare /chat route carries no content node — DerivePickerContext must skip it
        // (it would otherwise resolve PrimaryPath to "chat") and use the fallback instead.
        var chat = NavContext("chat", nodeType: null);
        var pc = AgentPickerProjection.DerivePickerContext(chat, fallbackContextPath: "ACME/ProductLaunch");

        pc.ContextPath.Should().Be("ACME/ProductLaunch");
        pc.NodeTypePath.Should().BeNull();
    }

    [Fact]
    public void DerivePickerContext_BothNull_YieldsBuiltInOnlyUnion()
    {
        var pc = AgentPickerProjection.DerivePickerContext(resolved: null, fallbackContextPath: null);
        pc.ContextPath.Should().BeNull();
        pc.NodeTypePath.Should().BeNull();
        AgentPickerProjection.BuildAgentQueries(pc.ContextPath, pc.NodeTypePath)
            .Should().ContainSingle().Which.Should().Be(BuiltInAgentQuery);
        AgentPickerProjection.BuildAgentQuery(pc.ContextPath, pc.NodeTypePath)
            .Should().Be(BuiltInAgentQuery);
    }

    [Fact]
    public void BuildModelQueries_NoSelection_IncludesRootCatalogOnly()
    {
        var queries = AgentPickerProjection.BuildModelQueries();
        queries.Should().ContainSingle()
            .Which.Should().Be("namespace:_Provider nodeType:LanguageModel|ModelProvider scope:descendants");
    }

    [Fact]
    public void BuildModelQueries_EmptySelection_EqualsDefault()
    {
        var def = AgentPickerProjection.BuildModelQueries();
        var withEmpty = AgentPickerProjection.BuildModelQueries(selectedProviderPaths: Array.Empty<string>());
        withEmpty.Should().Equal(def);
    }

    [Fact]
    public void BuildModelQueries_WithSelectedProviders_AddsSelfAndDescendantsPerPath()
    {
        var queries = AgentPickerProjection.BuildModelQueries(
            selectedProviderPaths: new[] { "acme/_Provider/Anthropic", "rbuergi/_Provider/OpenAI" });

        // Root catalog query is always present.
        queries.Should().Contain("namespace:_Provider nodeType:LanguageModel|ModelProvider scope:descendants");
        // One selfAndDescendants query per selected provider path (provider node + its models).
        queries.Should().Contain("namespace:acme/_Provider/Anthropic nodeType:LanguageModel|ModelProvider scope:selfAndDescendants");
        queries.Should().Contain("namespace:rbuergi/_Provider/OpenAI nodeType:LanguageModel|ModelProvider scope:selfAndDescendants");
    }

    [Fact]
    public void BuildModelQueries_AllQueriesShareSameNodeTypeFilter()
    {
        // The synced collection's all-Initial gating breaks if nodeType filters
        // differ across queries — every query must carry the same union filter.
        var queries = AgentPickerProjection.BuildModelQueries(
            currentPath: "ctx", nodeTypePath: "nt",
            selectedProviderPaths: new[] { "acme/_Provider/Anthropic" });

        queries.Should().OnlyContain(q => q.Contains("nodeType:LanguageModel|ModelProvider"));
        queries.Should().HaveCount(4); // root + currentPath + nodeTypePath + 1 selection
    }

    [Fact]
    public void BuildModelQueries_SkipsNullOrEmptySelectionEntries()
    {
        var queries = AgentPickerProjection.BuildModelQueries(
            selectedProviderPaths: new[] { "acme/_Provider/Anthropic", "", null! });
        queries.Should().HaveCount(2); // root + the one non-empty selection
    }

    [Fact]
    public void BuildModelQueries_WithUserPath_AddsUserMemexSubtreeQuery()
    {
        // The chatting user's own providers/models live in their dotfile
        // namespace ({user}/_Memex/{provider}/{model}); the picker unions a
        // descendants query over it so they appear alongside the catalog.
        var queries = AgentPickerProjection.BuildModelQueries(userPath: "rbuergi");

        queries.Should().Contain("namespace:_Provider nodeType:LanguageModel|ModelProvider scope:descendants");
        queries.Should().Contain($"namespace:{ModelProviderNodeType.UserNamespacePath("rbuergi")} nodeType:LanguageModel|ModelProvider scope:descendants");
        queries.Should().HaveCount(2); // root + user _Memex
    }

    [Fact]
    public void BuildModelQueries_NoUserPath_OmitsUserMemexQuery()
    {
        // userPath defaults to null — no _Memex query, byte-for-byte the prior
        // behaviour so existing (non-user-scoped) callers are unaffected.
        var queries = AgentPickerProjection.BuildModelQueries();
        queries.Should().NotContain(q => q.Contains("/_Memex"));
    }
}
