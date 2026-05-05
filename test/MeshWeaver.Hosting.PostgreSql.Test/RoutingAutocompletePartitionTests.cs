using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Verifies that <see cref="RoutingMeshQueryProvider.AutocompleteAsync"/> surfaces
/// partition KEYS (schema names) themselves as suggestions when basePath is empty.
///
/// This is the chat-autocomplete behaviour for <c>@/</c> and <c>@/&lt;prefix&gt;</c>:
/// Postgres partitions are schemas with no MeshNode at the partition root, so a
/// node-only fan-out misses the partition NAME entirely. The router must emit the
/// partition key directly so users can find <c>rbuergi</c> when typing <c>@/rbu</c>.
/// </summary>
[Collection("PostgreSql")]
public class RoutingAutocompletePartitionTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public RoutingAutocompletePartitionTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Two empty partitions exist. Calling AutocompleteAsync with empty basePath
    /// and a matching prefix should return both partition keys as suggestions —
    /// even though neither partition contains any MeshNodes.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AutocompleteAsync_EmptyBasePath_PrefixMatchingPartitionKey_ReturnsPartitionSuggestions()
    {
        var ct = TestContext.Current.CancellationToken;

        var (dsP1, _) = await _fixture.CreateSchemaAdapterAsync("autop_alpha", null, ct);
        var (dsP2, _) = await _fixture.CreateSchemaAdapterAsync("autop_beta", null, ct);

        var factory = new FilteredPartitionedStoreFactory(
            new PostgreSqlPartitionedStoreFactory(
                _fixture.DataSource, _fixture.ConnectionString, _fixture.Options),
            new HashSet<string>(["autop_alpha", "autop_beta"], StringComparer.OrdinalIgnoreCase));

        var router = new RoutingPersistenceServiceCore(factory);
        await router.InitializeAsync(ct);

        var provider = new RoutingMeshQueryProvider(router);

        var suggestions = new List<QuerySuggestion>();
        await foreach (var s in provider.AutocompleteAsync("", "autop", _options, limit: 20, ct))
            suggestions.Add(s);

        suggestions.Should().Contain(s => s.Path == "autop_alpha",
            "partition key 'autop_alpha' must surface even though the schema has no MeshNodes");
        suggestions.Should().Contain(s => s.Path == "autop_beta",
            "partition key 'autop_beta' must surface even though the schema has no MeshNodes");

        await dsP1.DisposeAsync();
        await dsP2.DisposeAsync();
    }

    /// <summary>
    /// Empty prefix returns ALL searchable partition keys (the <c>@/</c> partition list).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AutocompleteAsync_EmptyBasePath_EmptyPrefix_ReturnsAllPartitionKeys()
    {
        var ct = TestContext.Current.CancellationToken;

        var (dsP1, _) = await _fixture.CreateSchemaAdapterAsync("autop_gamma", null, ct);
        var (dsP2, _) = await _fixture.CreateSchemaAdapterAsync("autop_delta", null, ct);

        var factory = new FilteredPartitionedStoreFactory(
            new PostgreSqlPartitionedStoreFactory(
                _fixture.DataSource, _fixture.ConnectionString, _fixture.Options),
            new HashSet<string>(["autop_gamma", "autop_delta"], StringComparer.OrdinalIgnoreCase));

        var router = new RoutingPersistenceServiceCore(factory);
        await router.InitializeAsync(ct);

        var provider = new RoutingMeshQueryProvider(router);

        var suggestions = new List<QuerySuggestion>();
        await foreach (var s in provider.AutocompleteAsync("", "", _options, limit: 20, ct))
            suggestions.Add(s);

        suggestions.Should().Contain(s => s.Path == "autop_gamma");
        suggestions.Should().Contain(s => s.Path == "autop_delta");
    }

    /// <summary>
    /// Prefix that doesn't match any partition key returns no partition-key suggestions
    /// (but the per-partition fan-out still runs underneath).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AutocompleteAsync_EmptyBasePath_PrefixNotMatchingAnyPartition_OmitsPartitionSuggestions()
    {
        var ct = TestContext.Current.CancellationToken;

        var (dsP1, _) = await _fixture.CreateSchemaAdapterAsync("autop_eps", null, ct);

        var factory = new FilteredPartitionedStoreFactory(
            new PostgreSqlPartitionedStoreFactory(
                _fixture.DataSource, _fixture.ConnectionString, _fixture.Options),
            new HashSet<string>(["autop_eps"], StringComparer.OrdinalIgnoreCase));

        var router = new RoutingPersistenceServiceCore(factory);
        await router.InitializeAsync(ct);

        var provider = new RoutingMeshQueryProvider(router);

        var suggestions = new List<QuerySuggestion>();
        await foreach (var s in provider.AutocompleteAsync("", "zzzzz", _options, limit: 20, ct))
            suggestions.Add(s);

        suggestions.Should().NotContain(s => s.NodeType == "Partition" && s.Path == "autop_eps",
            "prefix 'zzzzz' does not match the partition key 'autop_eps'");

        await dsP1.DisposeAsync();
    }

    /// <summary>
    /// Verifies that partition keys (from <see cref="RoutingMeshQueryProvider"/>) and
    /// static nodes (from <see cref="StaticNodeQueryProvider"/>) coexist in the autocomplete
    /// result without one shadowing the other. This is the production shape on memex-prod:
    /// Postgres-discovered partitions like <c>rbuergi</c>, <c>acme</c> alongside static
    /// nodes like <c>Doc</c> contributed via <c>IStaticNodeProvider</c>.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AutocompleteAsync_PostgresPartitionAndStaticNode_BothSurface()
    {
        var ct = TestContext.Current.CancellationToken;

        var (dsP1, _) = await _fixture.CreateSchemaAdapterAsync("mix_pg", null, ct);

        var factory = new FilteredPartitionedStoreFactory(
            new PostgreSqlPartitionedStoreFactory(
                _fixture.DataSource, _fixture.ConnectionString, _fixture.Options),
            new HashSet<string>(["mix_pg"], StringComparer.OrdinalIgnoreCase));

        var router = new RoutingPersistenceServiceCore(factory);
        await router.InitializeAsync(ct);
        var routingProvider = new RoutingMeshQueryProvider(router);

        // Static node provider — represents the Doc partition (or any IStaticNodeProvider
        // contributor like role/agent definitions). Path "DocOrgRoot" is a single segment,
        // so it's treated as a top-level partition by the orchestrator's PartitionList filter.
        var staticNode = new MeshNode("DocOrgRoot", "")
        {
            Name = "DocOrgRoot",
            NodeType = "Organization",
            State = MeshNodeState.Active
        };
        var staticProvider = new StaticNodeQueryProvider(new[] { new TestStaticNodeProvider(staticNode) });

        // Mimic MeshQuery's fan-out: collect from both providers and merge by Path.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<QuerySuggestion>();

        await foreach (var s in routingProvider.AutocompleteAsync("", "", _options, 30, ct))
            if (seen.Add(s.Path)) all.Add(s);
        await foreach (var s in staticProvider.AutocompleteAsync("", "", _options, 30, ct))
            if (seen.Add(s.Path)) all.Add(s);

        all.Should().Contain(s => s.Path == "mix_pg",
            "Postgres-discovered partition key must surface (no MeshNode at the schema root)");
        all.Should().Contain(s => s.Path == "DocOrgRoot",
            "static IStaticNodeProvider contribution must surface alongside Postgres partitions");

        await dsP1.DisposeAsync();
    }

    /// <summary>
    /// When a Postgres partition's NAME collides with a static node's Path (e.g., a
    /// hypothetical static "Doc" alongside a Postgres "doc" schema), the routing provider
    /// must NOT shadow the static node. Each provider is independent at the autocomplete
    /// fan-out; the orchestrator merges them, with first-seen winning per path key.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AutocompleteAsync_PartitionKeyNameCollidesWithStaticNode_BothPathsRepresented()
    {
        var ct = TestContext.Current.CancellationToken;

        var (dsP1, _) = await _fixture.CreateSchemaAdapterAsync("mix_doc", null, ct);

        var factory = new FilteredPartitionedStoreFactory(
            new PostgreSqlPartitionedStoreFactory(
                _fixture.DataSource, _fixture.ConnectionString, _fixture.Options),
            new HashSet<string>(["mix_doc"], StringComparer.OrdinalIgnoreCase));

        var router = new RoutingPersistenceServiceCore(factory);
        await router.InitializeAsync(ct);
        var routingProvider = new RoutingMeshQueryProvider(router);

        // Static node with a DIFFERENT path than the schema — typical real-world case.
        var staticNode = new MeshNode("StaticDocRoot", "")
        {
            Name = "Static Doc",
            NodeType = "Organization",
            State = MeshNodeState.Active
        };
        var staticProvider = new StaticNodeQueryProvider(new[] { new TestStaticNodeProvider(staticNode) });

        var routingSuggestions = new List<QuerySuggestion>();
        await foreach (var s in routingProvider.AutocompleteAsync("", "", _options, 30, ct))
            routingSuggestions.Add(s);

        var staticSuggestions = new List<QuerySuggestion>();
        await foreach (var s in staticProvider.AutocompleteAsync("", "", _options, 30, ct))
            staticSuggestions.Add(s);

        routingSuggestions.Should().Contain(s => s.Path == "mix_doc",
            "RoutingMeshQueryProvider yields partition-key suggestions independently of static providers");
        staticSuggestions.Should().Contain(s => s.Path == "StaticDocRoot",
            "StaticNodeQueryProvider yields its nodes independently of routing");

        await dsP1.DisposeAsync();
    }

    private sealed class TestStaticNodeProvider(MeshNode node) : IStaticNodeProvider
    {
        public IEnumerable<MeshNode> GetStaticNodes() => [node];
    }

    /// <summary>
    /// Wraps an inner factory and restricts partition discovery to a specific set of schemas.
    /// Prevents cross-test contamination when other test methods have left schemas behind.
    /// </summary>
    private sealed class FilteredPartitionedStoreFactory(
        IPartitionedStoreFactory inner,
        HashSet<string> allowed) : IPartitionedStoreFactory
    {
        public Task<PartitionedStore> CreateStoreAsync(string firstSegment, CancellationToken ct = default)
            => inner.CreateStoreAsync(firstSegment, ct);

        public async Task<IReadOnlyList<string>> DiscoverPartitionsAsync(CancellationToken ct = default)
        {
            var all = await inner.DiscoverPartitionsAsync(ct);
            return all.Where(s => allowed.Contains(s)).ToList();
        }
    }
}
