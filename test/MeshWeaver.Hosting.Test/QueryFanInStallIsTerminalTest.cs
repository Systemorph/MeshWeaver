using System;
using System.Collections.Generic;
using System.Linq;
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
/// 🚨 <b>The query fan-in's Initial gate is a BOUND, not a wait</b> — policy
/// <c>query-fanin-stall-terminal</c>.
///
/// <para><c>MeshQuery.MergeProviderObservables</c> gates its merged <c>Initial</c> on EVERY
/// registered provider emitting one, because the frame is the union of their slices and a missing
/// slice is indistinguishable from an empty one. A provider that COMPLETES without an Initial is
/// counted as empty and NAMED on the frame (<see cref="MeshQueryMergeContractTest"/>). A provider
/// that neither emits, completes NOR errors had no representation at all: it starved the gate for
/// ever, and the consumer hung with no error, no consumer-side log line and nothing to grep.
/// Measured on <c>memex</c> over the 400 minutes to 2026-09-21T04:12Z: 200+ warnings from the probe
/// that detected exactly this, and nothing acted on any of them.</para>
///
/// <para>The merge now TERMINATES such a query with
/// <see cref="QueryProviderStalledException"/>, naming the laggards. These tests drive the merge
/// through the public <see cref="MeshQuery"/> surface with fake providers, and every one has its
/// control on the other side of the change: a stalled provider faults, a provider that ANSWERS does
/// not — at the same budget, on the same merge.</para>
/// </summary>
public class QueryFanInStallIsTerminalTest
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>
    /// The ladder the fan-in's rung is DERIVED from — the one value a caller configures. Four
    /// seconds contracts to 2 s / 1 s / 500 ms, so the stall terminal fires in half a second here
    /// instead of the production default's 15 s. It is written as a literal for the one reason a
    /// bound may be: it is the SUBJECT of the test, not a wait inside it. Every wait below is
    /// <see cref="TestTimeouts"/>-derived.
    /// </summary>
    private static MeshOperationOptions ShortLadder => new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>The rung under test, read from the ladder rather than restated.</summary>
    private static TimeSpan StallBudget => ShortLadder.QueryInitialBudget;

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

    private static IObservable<QueryResultChange<MeshNode>> Merged(MeshQuery query) =>
        ((IMeshQueryCore)query).Query<MeshNode>(
            new MeshQueryRequest { Query = "nodeType:Markdown", Limit = 10, UserId = "reader" }, Options);

    // ────────────────────────────────────────────────────────────────────────────────
    // The terminal
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE CHANGE. One provider answers, one is subscribed and never heard from again. The merged
    /// query used to hang for ever; it now faults, and the fault NAMES the provider that starved —
    /// which is the whole reason the terminal belongs at this level rather than at a consumer's.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AStalledProvider_FaultsTheMergedQuery_AndNamesIt()
    {
        var healthy = new FakeProvider("healthy",
            () => Observable.Return(Initial(Node("a/one"))).Concat(Observable.Never<QueryResultChange<MeshNode>>()));
        var stalled = new FakeProvider("stalled", Observable.Never<QueryResultChange<MeshNode>>);

        var query = new MeshQuery([healthy, stalled], hub: null!, ShortLadder);

        var fault = await Observed(Merged(query), TestContext.Current.CancellationToken);

        fault.Should().BeOfType<QueryProviderStalledException>(
            "a provider that neither emits, completes nor errors leaves the merged Initial with no "
            + "snapshot to union — the honest terminal is an availability failure, not silence");
        var stall = (QueryProviderStalledException)fault!;
        stall.Providers.Should().Contain("stalled")
            .And.NotContain("healthy",
                "only the laggards are named — naming a provider that answered would send the next "
                + "reader after the wrong one, which is the whole cost the attribution exists to avoid");
        stall.Budget.Should().Be(StallBudget,
            "the budget reported is the FAN-IN's own rung of the ladder, never a consumer's bound");
        stall.Message.Should().Contain("never bump the consumer's timeout");
    }

    /// <summary>
    /// THE CONTROL, and it is the one that proves the test above is not vacuous: the SAME merge, the
    /// SAME budget, two providers that both answer — no fault, and the merged Initial carries both
    /// slices. Without this case a terminal that fired unconditionally would pass the test above.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task EveryProviderAnswers_NoFault_EvenPastTheBudget()
    {
        var a = new FakeProvider("a",
            () => Observable.Return(Initial(Node("a/one"))).Concat(Observable.Never<QueryResultChange<MeshNode>>()));
        var b = new FakeProvider("b",
            () => Observable.Return(Initial(Node("b/two"))).Concat(Observable.Never<QueryResultChange<MeshNode>>()));

        var query = new MeshQuery([a, b], hub: null!, ShortLadder);

        // A LIVE subscription held well past the budget, not a Take(1): the arm is released on the
        // answer, so a healthy live query must never fault — and a Take(1) would unsubscribe before
        // the budget and prove nothing about it.
        var changes = new List<QueryResultChange<MeshNode>>();
        Exception? fault = null;
        using var sub = Merged(query).Subscribe(changes.Add, ex => fault = ex);

        var settled = new AsyncSubject<System.Reactive.Unit>();
        using (Observable.Timer(StallBudget + StallBudget).Subscribe(_ =>
        {
            settled.OnNext(System.Reactive.Unit.Default);
            settled.OnCompleted();
        }))
        {
            await settled.Should().Within(TestTimeouts.Convergence)
                .Emit("the observation window has to actually elapse for this control to mean anything",
                    TestContext.Current.CancellationToken);
        }

        fault.Should().BeNull(
            "both providers delivered an Initial, so nothing was ever stalled — the terminal must "
            + "not fire on a query that has its answer and is merely staying open for deltas");
        changes.Should().HaveCount(1);
        changes[0].Items.Select(n => n.Path).Should().HaveCount(2).And.Contain("a/one").And.Contain("b/two");
    }

    /// <summary>
    /// The single-provider branch takes a DIFFERENT code path through the merge (it skips the
    /// aggregation entirely), and a lone provider that never answers hangs its consumer just as
    /// silently. Same terminal, same naming.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ALoneStalledProvider_FaultsToo()
    {
        var stalled = new FakeProvider("lonely", Observable.Never<QueryResultChange<MeshNode>>);

        var query = new MeshQuery([stalled], hub: null!, ShortLadder);

        var fault = await Observed(Merged(query), TestContext.Current.CancellationToken);

        fault.Should().BeOfType<QueryProviderStalledException>();
        ((QueryProviderStalledException)fault!).Providers.Should().Contain("lonely");
    }

    /// <summary>The single-provider control: a lone provider that answers still answers.</summary>
    [Fact(Timeout = 240_000)]
    public async Task ALoneAnsweringProvider_StillAnswers()
    {
        var healthy = new FakeProvider("lonely",
            () => Observable.Return(Initial(Node("a/one"))).Concat(Observable.Never<QueryResultChange<MeshNode>>()));

        var query = new MeshQuery([healthy], hub: null!, ShortLadder);

        var change = await Merged(query).Should().Within(TestTimeouts.Convergence)
            .Emit("a provider that delivers its Initial is never stalled",
                TestContext.Current.CancellationToken);

        change.ChangeType.Should().Be(QueryChangeType.Initial);
        change.Items.Select(n => n.Path).Should().HaveCount(1).And.Contain("a/one");
    }

    /// <summary>
    /// 🚨 A provider that COMPLETES without an Initial is NOT stalled, and the two must stay apart:
    /// the completion has a terminal the merge can account for (counted empty, named on the frame —
    /// MeshWeaver#4557), the stall does not. Collapsing them would turn a diagnosable contract
    /// violation into an availability failure and lose the <c>SilentProviders</c> report that
    /// <c>MeshNodeStreamCache</c> uses to refuse caching the frame.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AProviderThatCOMPLETESWithoutAnInitial_IsNamedNotFaulted()
    {
        var healthy = new FakeProvider("healthy", () => Observable.Return(Initial(Node("a/one"))));
        var silent = new FakeProvider("silent", Observable.Empty<QueryResultChange<MeshNode>>);

        var query = new MeshQuery([healthy, silent], hub: null!, ShortLadder);

        var change = await Merged(query).Should().Within(TestTimeouts.Convergence)
            .Emit("a completed provider is counted empty so the merge proceeds — it does not stall",
                TestContext.Current.CancellationToken);

        change.ChangeType.Should().Be(QueryChangeType.Initial);
        change.SilentProviders.Should().NotBeNull().And.Contain("silent");
        change.Items.Select(n => n.Path).Should().HaveCount(1).And.Contain("a/one");
    }

    /// <summary>
    /// The LADDER, asserted where it is consumed rather than where it is declared: the fan-in's rung
    /// has to be strictly inside the permission fold's, or the attribution this whole terminal exists
    /// to produce is lost to whichever clock happens to win (issue #1198 — "equal is not an
    /// ordering"). The stall probe's old diagnostic delay was a hard-coded 20 s, which is EXACTLY
    /// <c>PermissionEstablishmentBudget</c> at the production default; promoting that constant to a
    /// terminal as-is would have recreated that defect.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(4)]
    [InlineData(1)]
    public void TheFanInsRung_IsStrictlyInsideTheFoldsBudget(int outerSeconds)
    {
        var options = new MeshOperationOptions { Timeout = TimeSpan.FromSeconds(outerSeconds) };

        options.QueryInitialBudget.Should().BeLessThan(options.PermissionEstablishmentBudget,
            "the fan-in read is what the permission fold is WAITING on, so it must be able to fire "
            + "first — it is the only level that can name WHICH provider starved");
        options.QueryInitialBudget.Should().BePositive(
            "a non-positive rung would terminate every query the instant it opened");
    }

    /// <summary>
    /// Observes a query's TERMINAL rather than its emissions, and does it through the sanctioned
    /// bridge. Returns the fault, or null when the sequence completed without one.
    /// </summary>
    private static async Task<Exception?> Observed(
        IObservable<QueryResultChange<MeshNode>> source, CancellationToken cancellationToken)
    {
        Exception? fault = null;
        var settled = new AsyncSubject<System.Reactive.Unit>();
        using var sub = source.Subscribe(
            _ => { },
            ex =>
            {
                fault = ex;
                settled.OnNext(System.Reactive.Unit.Default);
                settled.OnCompleted();
            },
            () =>
            {
                settled.OnNext(System.Reactive.Unit.Default);
                settled.OnCompleted();
            });
        await settled.Should().Within(TestTimeouts.Convergence)
            .Emit("the merged query has to reach a terminal at all — that IS the change under test",
                cancellationToken);
        return fault;
    }
}
