using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A followed read on which NO VERDICT was reached ends — fail closed — but never as a DENIAL
/// (MeshWeaver#7825, #7826–#7830). Pre-fix every undecided verdict — a stalled re-check, a faulted
/// one, one that answered nothing — ended the read with the gate's own "Access denied: user … lacks
/// Read permission on …", and the subscriber re-raised it as a bare <see cref="InvalidOperationException"/>
/// that no view could tell from an engineering fault: one stalled re-check put a confident denial,
/// at Error, on every view of a correctly entitled user's page.
/// </summary>
public class RevisionAdmissionNoVerdictTest
{
    private static IObservable<int> Admit(IObservable<int> revisions, Func<int, IObservable<bool>> verdictOf) =>
        revisions.AdmitEachRevision(verdictOf, TimeSpan.FromHours(1), () => new UnauthorizedAccessException("refused"));

    [Fact]
    public void AFaultedVerdict_EndsTheReadAsNoVerdict_NeverAsADenial()
    {
        var revisions = new Subject<int>();
        var verdict = new BehaviorSubject<bool>(true);
        var forwarded = ImmutableList<int>.Empty;
        Exception? ended = null;
        using var _ = Admit(revisions, _ => verdict).Subscribe(r => forwarded = forwarded.Add(r), ex => ended = ex);

        revisions.OnNext(0);
        verdict.OnError(new TimeoutException("the re-check did not settle"));

        Assert.Equal([0], forwarded);
        Assert.True(ReadVerdict.IsNoVerdict(ended), $"a faulted re-check reached no verdict; ended with {ended}");
        Assert.IsNotType<UnauthorizedAccessException>(ended);
        Assert.Contains("TimeoutException", ended!.Message);
    }

    [Fact]
    public void AnEmptyFirstVerdict_IsNoVerdict_AndForwardsNothing()
    {
        var revisions = new Subject<int>();
        var forwarded = ImmutableList<int>.Empty;
        Exception? ended = null;
        using var _ = Admit(revisions, _ => Observable.Empty<bool>()).Subscribe(r => forwarded = forwarded.Add(r), ex => ended = ex);

        revisions.OnNext(0);

        Assert.Empty(forwarded);
        Assert.True(ReadVerdict.IsNoVerdict(ended), $"an empty answer reached no verdict; ended with {ended}");
    }

    [Fact]
    public async Task AStalledFirstVerdict_IsNoVerdict()
    {
        var revisions = new Subject<int>();
        var forwarded = ImmutableList<int>.Empty;
        var ended = new ReplaySubject<Exception>(1);
        using var _ = revisions
            .AdmitEachRevision(_ => Observable.Never<bool>(), TimeSpan.FromMilliseconds(20), () => new UnauthorizedAccessException("refused"))
            .Subscribe(r => forwarded = forwarded.Add(r), ended.OnNext);

        revisions.OnNext(0);

        var error = await ended.Should().Within(TestTimeouts.Convergence).Emit("a first verdict past its budget ends the read");
        Assert.Empty(forwarded);
        Assert.True(ReadVerdict.IsNoVerdict(error), $"a verdict that stalled past its budget reached no verdict; ended with {error}");
    }

    [Fact]
    public void NegativeControl_ADecidedFalse_IsStillTheDenial()
    {
        var revisions = new Subject<int>();
        var verdict = new BehaviorSubject<bool>(true);
        Exception? ended = null;
        using var _ = Admit(revisions, _ => verdict).Subscribe(_ => { }, ex => ended = ex);

        revisions.OnNext(0);
        verdict.OnNext(false);

        Assert.IsType<UnauthorizedAccessException>(ended);
        Assert.False(ReadVerdict.IsNoVerdict(ended));
    }
}

/// <summary>
/// Over the wire: the KIND of a followed read's end reaches the subscriber (MeshWeaver#7825). The
/// exception type does not survive a <see cref="StreamErrorEvent"/>, so the owner names it and the
/// subscriber re-raises it: a decided refusal as an <see cref="UnauthorizedAccessException"/>, a read
/// on which no verdict was reached as a <see cref="DeliveryFailureException"/> carrying
/// <see cref="ErrorType.Unavailable"/>. Pre-fix both arrived as <see cref="InvalidOperationException"/>
/// with the denial's text.
/// </summary>
public class FollowedReadEndKindReachesTheSubscriberTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly BehaviorSubject<bool> liveVerdict = new(true);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds.WithType<MyData>(t => t.WithKey(d => d.Id))))
            .WithClientSubscriptionReadAdmission((_, _) => new ClientReadFollow(_ => liveVerdict, TestTimeouts.Convergence));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds.WithType<MyData>(t => t.WithKey(d => d.Id))));

    private async Task<Exception> EndOfAFollowedRead(Action endTheVerdict)
    {
        var host = GetHost();
        var client = GetClient();
        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var mirror = client.GetWorkspace().GetRemoteStream<EntityStore>(CreateHostAddress(), new CollectionsReference(collectionName));
        var ended = mirror.Materialize()
            .Where(n => n.Kind == NotificationKind.OnError)
            .Select(n => n.Exception!)
            .Replay(1);
        using var connection = ended.Connect();
        await mirror.Should().Within(TestTimeouts.Convergence).Emit("the followed subscription serves its first snapshot");

        endTheVerdict();

        return await ended.Should().Within(TestTimeouts.Convergence).Emit("the owner ends the followed read");
    }

    [HubFact]
    public async Task AReadWhoseReCheckFaulted_ReachesTheSubscriberAsNoVerdict()
    {
        var error = await EndOfAFollowedRead(() => liveVerdict.OnError(new TimeoutException("the re-check did not settle")));

        Assert.True(ReadVerdict.IsNoVerdict(error), $"no verdict was reached; the subscriber saw {error.GetType().Name}: {error.Message}");
        Assert.DoesNotContain("lacks Read permission", error.Message);
    }

    [HubFact]
    public async Task NegativeControl_ARefusedRead_ReachesTheSubscriberAsADenial()
    {
        var error = await EndOfAFollowedRead(() => liveVerdict.OnNext(false));

        Assert.IsType<UnauthorizedAccessException>(error);
        Assert.Contains("lacks Read permission", error.Message);
    }
}
