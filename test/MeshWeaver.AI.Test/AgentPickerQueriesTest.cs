#pragma warning disable CS1591

using System;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pure tests for <see cref="AgentPickerProjection.BuildModelQueries"/>'s
/// selection-aware overload — the query strings the provider-selection picker
/// + credential resolver fan out over. No mesh required.
/// </summary>
public class AgentPickerQueriesTest
{
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
