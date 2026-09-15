using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the <c>MergeProviderObservables</c> contract enforcement: the merged query's Initial
/// gates on EVERY provider emitting one, so a provider whose observable COMPLETES without an
/// Initial (an <c>Observable.Empty</c>-shaped branch, a swallowed fault, an early-disposed inner
/// chain) used to starve the gate FOREVER — the consumer's <c>Take(1)</c>/<c>FirstAsync</c> never
/// fired. Prod repro (2026-07-03): every real-user unpinned structured search hung the full
/// 300s MCP window with PostgreSQL idle and no error logged anywhere.
///
/// <para>The merge now counts a silent completion as an EMPTY Initial (loud warning naming the
/// provider) so the healthy providers' results flow. These tests drive the merge through the
/// public <see cref="MeshQuery"/> surface with fake providers.</para>
/// </summary>
public class MeshQueryMergeContractTest
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>Fake provider returning a canned observable for every query.</summary>
    private sealed class FakeProvider(string name, Func<IObservable<QueryResultChange<MeshNode>>> factory)
        : IMeshQueryProvider
    {
        public string Name => name;

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
            => (IObservable<QueryResultChange<T>>)factory();

        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }

    private static QueryResultChange<MeshNode> Initial(params MeshNode[] nodes) => new()
    {
        ChangeType = QueryChangeType.Initial,
        Items = nodes,
        Timestamp = DateTimeOffset.UtcNow,
    };

    private static MeshNode Node(string path) => new(path.Split('/').Last(),
        path.Contains('/') ? path[..path.LastIndexOf('/')] : null)
    {
        Name = path,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
    };

    [Fact(Timeout = 30_000)]
    public async Task ProviderCompletingWithoutInitial_DoesNotStarveTheMerge()
    {
        var healthy = new FakeProvider("healthy",
            () => Observable.Return(Initial(Node("a/one"), Node("b/two"))));
        // The defect shape: completes WITHOUT emitting an Initial.
        var silent = new FakeProvider("silent", Observable.Empty<QueryResultChange<MeshNode>>);

        var query = new MeshQuery([healthy, silent], hub: null!);

        var change = await ((IMeshQueryCore)query)
            .Query<MeshNode>(new MeshQueryRequest { Query = "nodeType:Markdown", Limit = 10 }, Options)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .Await(TestContext.Current.CancellationToken);

        change.ChangeType.Should().Be(QueryChangeType.Initial);
        var paths = change.Items.Select(n => n.Path).ToList();
        paths.Should().HaveCount(2);
        paths.Should().Contain("a/one");
        paths.Should().Contain("b/two");
    }

    [Fact(Timeout = 30_000)]
    public async Task AllProvidersSilent_EmitsEmptyInitial()
    {
        var silentA = new FakeProvider("silentA", Observable.Empty<QueryResultChange<MeshNode>>);
        var silentB = new FakeProvider("silentB", Observable.Empty<QueryResultChange<MeshNode>>);

        var query = new MeshQuery([silentA, silentB], hub: null!);

        var change = await ((IMeshQueryCore)query)
            .Query<MeshNode>(new MeshQueryRequest { Query = "nodeType:Markdown", Limit = 10 }, Options)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .Await(TestContext.Current.CancellationToken);

        change.ChangeType.Should().Be(QueryChangeType.Initial);
        change.Items.Should().BeEmpty();
    }

    /// <summary>
    /// The denominator rides the merge (MeshWeaver #4274): the partitions each provider says it
    /// READ FROM are unioned onto the merged Initial, in first-seen order, deduplicated. A merge
    /// where one provider reports and one does not carries the reporting one's set — a partial
    /// denominator a caller can still read a zero against.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ReportedPartitions_AreUnionedOntoTheMergedInitial()
    {
        var reporting = new FakeProvider("reporting",
            () => Observable.Return(Initial(Node("acme/one")) with { Partitions = ["acme", "shared"] }));
        var alsoReporting = new FakeProvider("alsoReporting",
            () => Observable.Return(Initial(Node("shared/two")) with { Partitions = ["shared", "docs"] }));
        var silent = new FakeProvider("silent", () => Observable.Return(Initial()));

        var query = new MeshQuery([reporting, alsoReporting, silent], hub: null!);

        var change = await ((IMeshQueryCore)query)
            .Query<MeshNode>(new MeshQueryRequest { Query = "nodeType:Markdown partitions:all", Limit = 10 }, Options)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        change.Partitions.Should().Equal(new[] { "acme", "shared", "docs" },
            "the union of what the reporting providers read, deduplicated, and a silent provider adds nothing");
    }

    /// <summary>
    /// The negative control that keeps the union honest: when NO provider reports, the merged
    /// Initial's <see cref="QueryResultChange{T}.Partitions"/> is null — unknown — never an empty
    /// list that would read as "read from no partition" or be mistaken for a complete denominator.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task NoProviderReporting_LeavesPartitionsNull()
    {
        var a = new FakeProvider("a", () => Observable.Return(Initial(Node("p/one"))));
        var b = new FakeProvider("b", () => Observable.Return(Initial(Node("q/two"))));

        var query = new MeshQuery([a, b], hub: null!);

        var change = await ((IMeshQueryCore)query)
            .Query<MeshNode>(new MeshQueryRequest { Query = "nodeType:Markdown partitions:all", Limit = 10 }, Options)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        change.Partitions.Should().BeNull("an unreported denominator is unknown, not empty");
    }

    /// <summary>
    /// The single-provider fast path clips through <c>ClipMergedInitial</c> with a <c>with</c>
    /// expression, so the provider's report must survive it unchanged.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SingleProvider_ReportedPartitionsSurviveTheClip()
    {
        var a = new FakeProvider("a",
            () => Observable.Return(Initial(Node("p/one"), Node("p/two")) with { Partitions = ["p"] }));

        var query = new MeshQuery([a], hub: null!);

        var change = await ((IMeshQueryCore)query)
            .Query<MeshNode>(new MeshQueryRequest { Query = "namespace:p", Limit = 1 }, Options)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        change.Items.Should().HaveCount(1, "the Limit clip applied");
        change.Partitions.Should().Equal("p");
    }

    [Fact(Timeout = 30_000)]
    public async Task HealthyProviders_MergeUnchanged()
    {
        var a = new FakeProvider("a", () => Observable.Return(Initial(Node("p/one"))));
        var b = new FakeProvider("b", () => Observable.Return(Initial(Node("q/two"))));

        var query = new MeshQuery([a, b], hub: null!);

        var change = await ((IMeshQueryCore)query)
            .Query<MeshNode>(new MeshQueryRequest { Query = "nodeType:Markdown", Limit = 10 }, Options)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .Await(TestContext.Current.CancellationToken);

        var paths = change.Items.Select(n => n.Path).ToList();
        paths.Should().HaveCount(2);
        paths.Should().Contain("p/one");
        paths.Should().Contain("q/two");
    }

    /// <summary>
    /// The single-provider fast path is wrapped by the Initial stall probe (a diagnostic timer
    /// that names a provider which never delivers its Initial). The wrapper must be TRANSPARENT:
    /// the Limit clip still applies and the emission flows unchanged.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SingleProvider_ClipStillApplies_ThroughStallProbeWrapper()
    {
        var a = new FakeProvider("a",
            () => Observable.Return(Initial(Node("p/one"), Node("p/two"), Node("p/three"))));

        var query = new MeshQuery([a], hub: null!);

        var change = await ((IMeshQueryCore)query)
            .Query<MeshNode>(new MeshQueryRequest { Query = "nodeType:Markdown", Limit = 2 }, Options)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .Await(TestContext.Current.CancellationToken);

        change.ChangeType.Should().Be(QueryChangeType.Initial);
        change.Items.Should().HaveCount(2, "the Limit clip must still apply through the probe wrapper");
    }

    /// <summary>
    /// A single provider's error must surface on the consumer's OnError unchanged — the stall
    /// probe wrapper may only observe, never swallow.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SingleProvider_ErrorPropagates_ThroughStallProbeWrapper()
    {
        var boom = new FakeProvider("boom",
            () => Observable.Throw<QueryResultChange<MeshNode>>(
                new InvalidOperationException("backing store down")));

        var query = new MeshQuery([boom], hub: null!);

        var act = () => ((IMeshQueryCore)query)
            .Query<MeshNode>(new MeshQueryRequest { Query = "nodeType:Markdown", Limit = 10 }, Options)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(10))
            .Await(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
