using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data.Completion;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 Pins that the one-shot <see cref="AutocompleteRequest"/> answers on CONVERGENCE — every
/// registered <see cref="IAutocompleteProvider"/> has settled — and never on a quiet period.
///
/// <para><b>The defect this replaces (#3094).</b> The handler used to answer when the merged
/// snapshot had been unchanged for 150 ms. <see cref="AutocompleteSnapshots.Combine"/> seeds every
/// provider with an empty snapshot so the merge can emit progressively, so a partial is ALWAYS
/// emitted first; whenever the fast in-memory providers had landed and a slower one's rows arrived
/// after the window, the timer fired and the answer went out WITHOUT them — and still carried
/// <see cref="AutocompleteResponse.IsComplete"/> = <c>true</c>, so no caller could tell. The
/// measured symptom was a cross-partition autocomplete row that appears when the suite runs alone
/// and disappears under parallel load.</para>
///
/// <para><b>Why these two tests and not a timing assertion.</b> The discriminator is not "how
/// long" but "what the answer is a function of". <see cref="TheAnswerWaitsForEveryProviderToSettle"/>
/// holds a provider silent and asserts NOTHING is answered while it is — the negative has no
/// positive signal to wait on, which is the one sanctioned fixed window — then releases it and
/// asserts the answer CONTAINS its item. Against the settle window the first half fails outright
/// (an answer arrives ~150 ms in) and the second half fails too (that answer can never contain the
/// late item). <see cref="AProviderThatNeverSettlesIsLabelledIncomplete"/> pins the other half of
/// the contract: the deadline still always answers — never a hang, which was #2276 — but says so.</para>
/// </summary>
public class AutocompleteSettlesOnConvergenceTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>The query both providers answer. Its search text is <c>item</c>.</summary>
    private const string Query = "@item";

    // Both labels CONTAIN the query's search text, and both carry a non-zero Priority. Either alone
    // would survive the handler's relevance filter (which only re-scores zero-priority items and
    // drops the ones that then score zero); together they keep this test about WHEN the answer is
    // posted rather than about scoring.
    private static readonly AutocompleteItem PromptItem =
        new(Label: "prompt-item", InsertText: "@prompt-item", Category: "Test", Priority: 10);

    private static readonly AutocompleteItem LateItem =
        new(Label: "late-item", InsertText: "@late-item", Category: "Test", Priority: 10);

    /// <summary>
    /// The window in which a premature answer would appear. Derived from the production deadline,
    /// not guessed: it must dominate the 150 ms settle window this test exists to forbid, and stay
    /// clear of <see cref="AutocompleteBounds.AnswerDeadline"/> so the deadline cannot fire inside
    /// it and make the negative assertion pass for the wrong reason.
    /// </summary>
    private static TimeSpan PrematureAnswerWindow => AutocompleteBounds.AnswerDeadline / 2;

    /// <summary>
    /// The slow provider's snapshot stream, driven by the test. Replay(1) so the handler sees the
    /// snapshot whether it subscribed before or after the test pushed it — the assertion is about
    /// WHEN the answer is posted, not about winning a subscribe race.
    /// </summary>
    private readonly ReplaySubject<IReadOnlyCollection<AutocompleteItem>> lateSnapshots = new(1);

    /// <summary>A provider that settles immediately: one snapshot, then completes.</summary>
    private sealed class PromptProvider : IAutocompleteProvider
    {
        public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(
            string query, string? contextPath = null)
            => Observable.Return<IReadOnlyCollection<AutocompleteItem>>([PromptItem]);
    }

    /// <summary>
    /// A provider that settles only when the test says so — the cross-partition query whose rows
    /// arrive after the fast providers have gone quiet.
    /// </summary>
    private sealed class LateProvider(IObservable<IReadOnlyCollection<AutocompleteItem>> snapshots)
        : IAutocompleteProvider
    {
        public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(
            string query, string? contextPath = null)
            => snapshots;
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData()
            .WithServices(services => services
                .AddSingleton<IAutocompleteProvider>(new PromptProvider())
                .AddSingleton<IAutocompleteProvider>(new LateProvider(lateSnapshots)));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddData();

    /// <summary>
    /// Posts the request ONCE and republishes the single response into a subject, so the two
    /// assertions below can both observe it. Subscribing to <c>hub.Observe</c> twice is not an
    /// option: disposing one of those subscriptions unregisters the hub's response callback.
    /// </summary>
    private IObservable<AutocompleteResponse> AnswerTo(
        IMessageHub client, IMessageHub host, out IDisposable subscription)
    {
        var answers = new ReplaySubject<AutocompleteResponse>(1);
        subscription = client
            .Observe<AutocompleteResponse>(
                new AutocompleteRequest(Query, null), o => o.WithTarget(host.Address))
            .Select(d => d.Message)
            .Subscribe(answers);
        return answers;
    }

    [HubFact]
    public async Task TheAnswerWaitsForEveryProviderToSettle()
    {
        var host = GetHost();
        var client = GetClient();

        var answers = AnswerTo(client, host, out var subscription);
        using var _ = subscription;

        // The late provider has produced nothing, so there IS no settled snapshot yet and there
        // must be no answer. This is where the old 150 ms settle window answered — with the prompt
        // provider's row only, and still labelled IsComplete = true.
        await answers.Should().NotEmit(PrematureAnswerWindow,
            "the late provider has not produced its snapshot, so no answer can be settled");

        lateSnapshots.OnNext([LateItem]);
        lateSnapshots.OnCompleted();

        var settled = await answers.Should().Within(TestTimeouts.Quick).Match(
            r => r.Items.Any(i => i.InsertText == LateItem.InsertText),
            "the answer must carry the late provider's rows once it has settled");

        settled.Items.Select(i => i.InsertText)
            .Should().Contain(PromptItem.InsertText,
                "the fast provider's rows survive the wait for the slow one");
        settled.IsComplete.Should().BeTrue(
            "every provider completed its snapshot stream, so the answer IS settled");
    }

    [HubFact]
    public async Task AProviderThatNeverSettlesIsLabelledIncomplete()
    {
        var host = GetHost();
        var client = GetClient();

        var answers = AnswerTo(client, host, out var subscription);
        using var _ = subscription;

        // lateSnapshots is never pushed: the provider violates the GetItems contract by never
        // completing. The deadline must still answer — a one-shot request that hangs is #2276 —
        // but the response must SAY the snapshot is not settled, which is the whole difference
        // between this and a silently truncated one.
        var answer = await answers.Should().Within(TestTimeouts.Quick).Match(
            _ => true, "the deadline must always produce an answer, never a hang");

        answer.IsComplete.Should().BeFalse(
            "a provider never completed, so this snapshot is explicitly NOT settled");
        answer.Items.Select(i => i.InsertText)
            .Should().Contain(PromptItem.InsertText,
                "the best snapshot so far is still the answer");
    }
}
