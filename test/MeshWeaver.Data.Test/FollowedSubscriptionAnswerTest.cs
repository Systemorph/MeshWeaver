using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A FOLLOWED client subscription (MeshWeaver#4306, follow-up 2; review of #7016, thread 4336475680):
/// its revisions wait for a read verdict before they are forwarded, so the answer to a re-subscribe
/// must ride the snapshot the owner actually POSTS, never the frame merely being set. An ack that
/// overtakes a snapshot still waiting for its verdict is the #3058 miscount: "acknowledged and
/// unanswered" true of an ask the moment it is made.
/// </summary>
public class FollowedSubscriptionAnswerTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Open: every verdict admits at once. Held: a verdict waits for <see cref="release"/>.</summary>
    private volatile bool holdVerdicts;
    private readonly Subject<bool> release = new();

    /// <summary>The subscriber every recording is scoped to.</summary>
    private volatile Address? subscriber;

    /// <summary>The mirror's stream, learned from its initial Full.</summary>
    private volatile string? mirrorStreamId;

    /// <summary>Set once the re-subscribe is posted: only what the owner posts after it is recorded.</summary>
    private volatile bool recording;

    /// <summary>What the owner posted to the subscriber after the re-subscribe, in order: "full" or "ack".</summary>
    private ImmutableList<string> posted = ImmutableList<string>.Empty;
    private readonly ReplaySubject<ImmutableList<string>> postedChanged = new(1);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds.WithType<MyData>(t => t.WithKey(d => d.Id))))
            .WithClientSubscriptionReadAdmission((_, _) => new ClientReadFollow(
                _ => holdVerdicts ? release.Take(1) : Observable.Return(true),
                TestTimeouts.Convergence))
            .AddPostPipeline(p => p.AddPipeline((d, next) =>
            {
                if (subscriber is null || !Equals(d.Target, subscriber))
                    return next.Invoke(d);
                if (d.Message is DataChangedEvent { ChangeType: ChangeType.Full } evt)
                {
                    mirrorStreamId ??= evt.StreamId;
                    if (recording && evt.StreamId == mirrorStreamId)
                        Record("full");
                }
                else if (d.Message is SubscribeAck && recording)
                    Record("ack");
                return next.Invoke(d);
            }));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds.WithType<MyData>(t => t.WithKey(d => d.Id))));

    private void Record(string what)
    {
        ImmutableInterlocked.Update(ref posted, list => list.Add(what));
        postedChanged.OnNext(Volatile.Read(ref posted));
    }

    /// <summary>
    /// The re-subscribe of a followed stream is acknowledged only AFTER the snapshot answering it has
    /// been posted — and the snapshot waits for its verdict, so the ack waits too. Pre-fix the ack
    /// was posted from the re-assert's <c>applied</c> callback while the Full was still held behind
    /// its verdict: "ack" came first.
    /// </summary>
    [HubFact]
    public async Task AReSubscribeOfAFollowedStream_IsAcknowledgedOnlyAfterItsSnapshotIsPosted()
    {
        var host = GetHost();
        var client = GetClient();
        subscriber = client.Address;

        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var reference = new CollectionsReference(collectionName);
        var mirror = client.GetWorkspace().GetRemoteStream<EntityStore>(CreateHostAddress(), reference);
        await mirror.Should().Within(TestTimeouts.Convergence).Emit("the followed subscription serves its first snapshot");
        Assert.NotNull(mirrorStreamId);

        // Every verdict from here on waits for the release: the re-assert Full is HELD by its verdict.
        holdVerdicts = true;
        recording = true;
        client.Post(new SubscribeRequest(mirrorStreamId!, reference), o => o.WithTarget(CreateHostAddress()));

        await postedChanged.Where(list => list.Contains("ack")).Take(1)
            .Should().NotEmit(TestTimeouts.Quick,
                "the re-subscribe's snapshot is still waiting for its verdict, so nothing answers it yet");

        holdVerdicts = false;
        release.OnNext(true);
        var final = await postedChanged.Where(list => list.Contains("ack") && list.Contains("full")).Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("once the verdict admits it, the snapshot is posted and the re-subscribe is answered");
        Assert.True(final.IndexOf("full") < final.IndexOf("ack"),
            $"the ack must follow the snapshot it acknowledges — posted: {string.Join(", ", final)}");
    }
}

/// <summary>
/// <see cref="RevisionAdmission.AdmitEachRevision{T}"/>'s hand-over (review of #7016, thread
/// 4336475629): an admitted revision stays followed until a NEWER revision is admitted, so a
/// withdrawal while the next revision's first verdict is still pending ends the sequence at once.
/// </summary>
public class RevisionAdmissionHandOverTest
{
    [Fact]
    public void AWithdrawalWhileTheNextVerdictIsPending_EndsTheSequenceAtOnce()
    {
        var revisions = new Subject<int>();
        var verdicts = new[] { new BehaviorSubject<bool>(true), new BehaviorSubject<bool>(false) };
        var pending = new Subject<bool>(); // revision 1's first verdict: never answers within the test
        var forwarded = ImmutableList<int>.Empty;
        Exception? ended = null;

        using var _ = revisions
            .AdmitEachRevision(
                revision => revision == 0 ? verdicts[0] : pending,
                TimeSpan.FromHours(1),
                () => new UnauthorizedAccessException("refused"))
            .Subscribe(r => forwarded = forwarded.Add(r), ex => ended = ex);

        revisions.OnNext(0);
        Assert.Equal([0], forwarded);

        revisions.OnNext(1); // its first verdict is pending
        Assert.Null(ended);

        verdicts[0].OnNext(false); // revision 0's admission is withdrawn while revision 1 waits
        Assert.IsType<UnauthorizedAccessException>(ended);
        Assert.Equal([0], forwarded);
    }

    [Fact]
    public void ARevisionAdmittedInTurn_HandsTheFollowOver()
    {
        var revisions = new Subject<int>();
        var first = new BehaviorSubject<bool>(true);
        var second = new BehaviorSubject<bool>(true);
        var forwarded = ImmutableList<int>.Empty;
        Exception? ended = null;

        using var _ = revisions
            .AdmitEachRevision(
                revision => revision == 0 ? first : second,
                TimeSpan.FromHours(1),
                () => new UnauthorizedAccessException("refused"))
            .Subscribe(r => forwarded = forwarded.Add(r), ex => ended = ex);

        revisions.OnNext(0);
        revisions.OnNext(1);
        Assert.Equal([0, 1], forwarded);

        first.OnNext(false); // superseded: revision 0 is no longer followed
        Assert.Null(ended);
        second.OnNext(false);
        Assert.IsType<UnauthorizedAccessException>(ended);
    }
}
