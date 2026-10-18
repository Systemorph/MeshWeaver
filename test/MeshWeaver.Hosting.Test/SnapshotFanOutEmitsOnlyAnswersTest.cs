using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 MeshWeaver#6827 — "autocomplete does not persist, shows up and disappears". The progressive
/// snapshot fan-out behind <see cref="MeshQuery.Query(MeshQueryRequest)"/> and
/// <see cref="MeshQuery.Autocomplete"/> seeded every provider with an EMPTY snapshot, so until the
/// slowest provider answered it emitted frames that were byte-identical to a definitive "nothing
/// matches". The search bar settled on that frame and closed its dropdown while a mesh-wide fan-out
/// was still running; a suggest widget fed it was closed by it. These tests pin that every frame the
/// fan-out emits is an ANSWER: rows as soon as any provider has rows, an empty frame only once every
/// provider has answered empty — and, so that withholding the seed can never become a hang, a
/// stalled provider with nothing to show faults the query naming it (policy
/// <c>query-fanin-stall-terminal</c>).
/// </summary>
public class SnapshotFanOutEmitsOnlyAnswersTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>Contracts the fan-in's rung to half a second — the SUBJECT of the stall tests, not a
    /// wait inside them (see <see cref="QueryFanInStallIsTerminalTest"/>).</summary>
    private static MeshOperationOptions ShortLadder => new() { Timeout = TimeSpan.FromSeconds(4) };

    private static readonly IReadOnlyCollection<QueryResult> NoRows = Array.Empty<QueryResult>();

    /// <summary>A provider whose unified snapshot surfaces (query AND autocomplete) are the given
    /// stream — the shape every concrete provider has from the fan-out's side.</summary>
    private sealed class SnapshotProvider(string name, Func<IObservable<IReadOnlyCollection<QueryResult>>> snapshots)
        : IMeshQueryProvider
    {
        public string Name => name;

        public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
            => Observable.Never<QueryResultChange<T>>();

        public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request, JsonSerializerOptions options)
            => snapshots();

        public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
            string basePath, string prefix, JsonSerializerOptions options,
            AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
            string? contextPath = null, string? context = null)
            => snapshots();

        public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
            => Observable.Return<T?>(default);
    }

    private static IReadOnlyCollection<QueryResult> Rows(params string[] paths)
        => paths.Select(p => new QueryResult
        {
            Path = p,
            Name = p.Split('/').Last(),
            NodeType = "Markdown",
            Score = 1,
        }).ToList();

    private static MeshQueryRequest TextQuery
        => new() { Query = "*alpha* scope:descendants partitions:all", UserId = "reader" };

    private static IObservable<IReadOnlyCollection<QueryResult>> Search(MeshQuery mesh) => mesh.Query(TextQuery);

    private static IObservable<IReadOnlyCollection<QueryResult>> Complete(MeshQuery mesh)
        => mesh.Autocomplete("", "alpha");

    public static TheoryData<string> Surfaces => new() { "query", "autocomplete" };

    private static IObservable<IReadOnlyCollection<QueryResult>> Surface(MeshQuery mesh, string surface)
        => surface == "query" ? Search(mesh) : Complete(mesh);

    /// <summary>
    /// THE REPRO. A fast provider answers "nothing here", a slow one (the mesh-wide partition fan-out)
    /// answers with the match a moment later. The first frame the consumer sees must be the match —
    /// on the seeded fan-out it was the empty seed, which the search bar bound as "no results".
    /// </summary>
    [Theory(Timeout = 240_000)]
    [MemberData(nameof(Surfaces))]
    public async Task TheFirstFrame_IsTheSlowProvidersAnswer_NeverTheSeed(string surface)
    {
        var slow = new ReplaySubject<IReadOnlyCollection<QueryResult>>(1);
        var mesh = new MeshQuery(
            [
                new SnapshotProvider("fast-empty", () => Observable.Return(NoRows)),
                new SnapshotProvider("slow", () => slow),
            ],
            hub: Mesh, ShortLadder);

        var first = Surface(mesh, surface).Should().Within(TestTimeouts.Convergence)
            .Emit("the slow provider's answer is the fan-out's answer", TestContext.Current.CancellationToken);
        slow.OnNext(Rows("acme/alpha"));
        var frame = await first;

        frame.Select(r => r.Path).Should().Equal(new[] { "acme/alpha" },
            "a frame emitted before the slow provider answered is not an answer — the seeded empty "
            + "frame closed the search dropdown while the real match was still on its way");
    }

    /// <summary>
    /// THE CONTROL on the other side of the change: when every provider HAS answered and none has a
    /// match, the empty answer is still delivered (and the query ends) — the fix withholds only the
    /// seed, never a real empty result.
    /// </summary>
    [Theory(Timeout = 240_000)]
    [MemberData(nameof(Surfaces))]
    public async Task EveryProviderAnswersEmpty_TheEmptyAnswerIsDelivered(string surface)
    {
        var mesh = new MeshQuery(
            [
                new SnapshotProvider("a", () => Observable.Return(NoRows)),
                new SnapshotProvider("b", () => Observable.Return(NoRows)),
            ],
            hub: Mesh, ShortLadder);

        var frame = await Surface(mesh, surface).Should().Within(TestTimeouts.Convergence)
            .Emit("a definitive empty answer must reach the consumer, or its spinner never stops",
                TestContext.Current.CancellationToken);

        frame.Should().BeEmpty();
    }

    /// <summary>
    /// A provider that COMPLETES without emitting has answered (empty): it must not hold the definitive
    /// empty frame back.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AProviderThatCompletesSilently_CountsAsAnEmptyAnswer()
    {
        var mesh = new MeshQuery(
            [
                new SnapshotProvider("answers", () => Observable.Return(NoRows)),
                new SnapshotProvider("silent", Observable.Empty<IReadOnlyCollection<QueryResult>>),
            ],
            hub: Mesh, ShortLadder);

        var frame = await Search(mesh).Should().Within(TestTimeouts.Convergence)
            .Emit("a completed provider has nothing more to say — the empty answer is final",
                TestContext.Current.CancellationToken);

        frame.Should().BeEmpty();
    }

    /// <summary>
    /// Progressive results stay progressive: rows from the fast provider are shown at once, before
    /// the slow one answers, and the slow one's rows merge in after.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task RowsFromAFastProvider_ShowBeforeTheSlowOneAnswers_ThenMerge()
    {
        var slow = new ReplaySubject<IReadOnlyCollection<QueryResult>>(1);
        var mesh = new MeshQuery(
            [
                new SnapshotProvider("fast", () => Observable.Return(Rows("a/alpha-one"))),
                new SnapshotProvider("slow", () => slow),
            ],
            hub: Mesh, ShortLadder);
        var stream = Search(mesh).Replay();
        using var connection = stream.Connect();

        var first = await stream.Should().Within(TestTimeouts.Convergence)
            .Emit("the fast provider's rows need not wait for the slow one", TestContext.Current.CancellationToken);
        first.Select(r => r.Path).Should().Equal("a/alpha-one");

        slow.OnNext(Rows("b/alpha-two"));
        var merged = await stream.Where(rows => rows.Count == 2).Should().Within(TestTimeouts.Convergence)
            .Emit("the slow provider's rows merge into the snapshot", TestContext.Current.CancellationToken);
        merged.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal).Should().Equal("a/alpha-one", "b/alpha-two");
    }

    /// <summary>
    /// Withholding the seed must never become a hang: a provider that neither answers nor completes,
    /// with nothing else to show, faults the query at the fan-in's rung — naming it.
    /// </summary>
    [Theory(Timeout = 240_000)]
    [MemberData(nameof(Surfaces))]
    public async Task AStalledProvider_WithNothingToShow_FaultsNamingIt(string surface)
    {
        var mesh = new MeshQuery(
            [
                new SnapshotProvider("answers", () => Observable.Return(NoRows)),
                new SnapshotProvider("stalled", Observable.Never<IReadOnlyCollection<QueryResult>>),
            ],
            hub: Mesh, ShortLadder);

        var fault = await Terminal(Surface(mesh, surface), TestContext.Current.CancellationToken);

        fault.Should().BeOfType<QueryProviderStalledException>(
            "with no answer to show, silence would leave the consumer spinning for ever");
        ((QueryProviderStalledException)fault!).Providers.Should().Contain("stalled").And.NotContain("answers");
    }

    /// <summary>
    /// The control for the terminal: once rows have been shown, a laggard does not fault the query —
    /// the consumer holds a real answer, and the laggard's rows simply join when they come. Asserted
    /// past the budget the terminal fires at in the test above.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AStalledProvider_AfterRowsWereShown_DoesNotFault()
    {
        var mesh = new MeshQuery(
            [
                new SnapshotProvider("rows", () => Observable.Return(Rows("a/alpha")).Concat(Observable.Never<IReadOnlyCollection<QueryResult>>())),
                new SnapshotProvider("stalled", Observable.Never<IReadOnlyCollection<QueryResult>>),
            ],
            hub: Mesh, ShortLadder);
        var notifications = Search(mesh).Materialize().Replay();
        using var connection = notifications.Connect();

        var first = await notifications.Should().Within(TestTimeouts.Convergence)
            .Emit("the rows are shown at once", TestContext.Current.CancellationToken);
        first.Kind.Should().Be(NotificationKind.OnNext);

        await notifications.Where(n => n.Kind != NotificationKind.OnNext).Should()
            .NotEmit(ShortLadder.QueryInitialBudget * 3,
                "the stall terminal is for a query with NO answer; this one has shown rows",
                TestContext.Current.CancellationToken);
    }

    /// <summary>Observes a query's TERMINAL: the fault, or null when it completed.</summary>
    private static async Task<Exception?> Terminal(
        IObservable<IReadOnlyCollection<QueryResult>> source, CancellationToken cancellationToken)
    {
        var terminal = await source.Materialize()
            .Where(n => n.Kind != NotificationKind.OnNext)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the query must reach a terminal", cancellationToken);
        return terminal.Exception;
    }
}
