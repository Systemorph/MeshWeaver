using System;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// One worked example per single-process injector, on a real monolith mesh — the examples
/// Doc/Architecture/FaultInjectionHarness quotes. Each proves its injector both ways: the fault is in
/// force while the switch is closed (a positive signal, never an absence alone), and gone once it is
/// released.
/// </summary>
public class InjectorExamplesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string HeldPartition = TestPartition;

    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();
    private HeldFirstFrameQueryProvider FirstFrame => Mesh.ServiceProvider.GetRequiredService<HeldFirstFrameQueryProvider>();

    /// <summary>The injectors go in BEFORE the base adds in-memory persistence.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder
            .AddFaultInjectingStorage()
            .AddHeldFirstFrame(HeldPartition));

    private static MeshNode Page(string id, string name)
        => new(id, TestPartition) { Name = name, NodeType = "Markdown", State = MeshNodeState.Active };

    /// <summary>
    /// <b>Storage flush hold.</b> The owner commits in memory; storage does not hold the commit until
    /// the switch is released. The held write is a positive signal, so nothing waits on a guess.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task HoldWrites_TheOwnerCommits_StorageHoldsTheOldState_UntilReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = $"flush-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(Page(id, "stored")).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        using (var flush = Storage.HoldWrites(path))
        {
            await Mesh.GetMeshNodeStream(path).Update(n => n with { Name = "committed" })
                .Should().Within(TestTimeouts.Convergence).Emit("the owner commits in memory", ct);
            await Storage.HeldWrites(path).Where(n => n.Name == "committed")
                .Should().Within(TestTimeouts.Convergence).Emit("the commit's flush reached the hold", ct);
            var stored = await Storage.Inner.Read(path, Mesh.JsonSerializerOptions)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
            stored?.Name.Should().Be("stored", "while the flush is held, storage still has the previous state");
        }

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Storage.Inner.Read(path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => n?.Name == "committed")
            .Should().Within(TestTimeouts.Convergence).Emit("released, the flush lands", ct);
    }

    /// <summary>
    /// <b>Hidden path.</b> Storage answers as if the path did not exist; the switch records every
    /// read it answered that way.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task HidePath_StorageAnswersAbsent_UntilReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = $"hidden-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(Page(id, "exists")).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        using (var hidden = Storage.HidePath(path))
        {
            (await Storage.Read(path, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: ct)).Should().BeNull("a hidden path reads as absent");
            (await Storage.Exists(path).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct))
                .Should().BeFalse();
            await hidden.Arrivals.Should().Within(TestTimeouts.Convergence).Emit("the reads met the fault", ct);
        }

        (await Storage.Read(path, Mesh.JsonSerializerOptions).Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: ct))?.Name.Should().Be("exists");
    }

    /// <summary>
    /// <b>Change-feed hold.</b> Notifications queue in order while held and are delivered on release.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task HoldChangeFeed_NotificationsQueue_AndArriveOnRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = $"feed-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        var seen = Storage.Changes.Where(c => c.Path == path).Replay();
        using var watching = seen.Connect();

        using (var feed = Storage.HoldChangeFeed())
        {
            await NodeFactory.CreateNode(Page(id, "created")).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
            await feed.Arrivals.Where(a => a.Contains(path)).Should().Within(TestTimeouts.Convergence)
                .Emit("the create's notification reached the held feed", ct);
            await seen.Should().NotEmit(TimeSpan.FromMilliseconds(300), "a held feed delivers nothing", ct);
        }

        await seen.Should().Within(TestTimeouts.Convergence).Emit("released, the queued notification arrives", ct);
    }

    /// <summary>
    /// <b>First-frame delay.</b> A query into the held partition gets no first frame until release.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task HoldFirstFrame_AQueryHasNoInitialFrame_UntilReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = $"frame-{Guid.NewGuid():N}";
        await NodeFactory.CreateNode(Page(id, "listed")).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var query = MeshQueryRequest.FromQuery($"path:{TestPartition}/{id}");

        IConnectableObservable<QueryResultChange<MeshNode>> frames;
        using (var hold = FirstFrame.Hold())
        {
            frames = MeshQuery.Query<MeshNode>(query).Replay();
            using var subscribed = frames.Connect();
            await hold.Arrivals.Should().Within(TestTimeouts.Convergence).Emit("the query's first frame met the hold", ct);
            await frames.Should().NotEmit(TimeSpan.FromMilliseconds(500), "no first frame while held", ct);
            hold.Release();
            var first = await frames.Should().Within(TestTimeouts.Convergence)
                .Emit("released, the fan-in emits its first frame", ct);
            first.Items.Should().Contain(n => n.Path == $"{TestPartition}/{id}");
        }
    }

    /// <summary>
    /// <b>Webhook loss</b>, function form: a refused delivery never runs the inbox and faults the
    /// sender; a released inbox delivers.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Inbox_Refuse_LosesTheDelivery_UntilReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var handled = 0;
        var inbox = new FaultInjectingInbox<string, string>(m =>
            Observable.Defer(() => { Interlocked.Increment(ref handled); return Observable.Return($"ok:{m}"); }));

        using (inbox.Refuse())
        {
            var lost = await inbox.Deliver("pr-1").Materialize().Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: ct);
            lost.Exception.Should().BeOfType<InboxDeliveryFailedException>();
            Volatile.Read(ref handled).Should().Be(0, "a refused delivery never reached the inbox");
        }

        (await inbox.Deliver("pr-2").Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct))
            .Should().Be("ok:pr-2");
        await inbox.Faulted.Should().Within(TestTimeouts.Convergence)
            .Match(f => f.Fault == InboxFault.Refused && f.What == "pr-1", cancellationToken: ct);
    }

    /// <summary><b>Webhook loss</b>, HTTP form: the sender sees 500 and the inbox never saw the request.</summary>
    [Fact(Timeout = 60_000)]
    public async Task HttpInbox_Refuse_AnswersFiveHundred_WithoutForwarding()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new CountingInbox();
        using var handler = new FaultInjectingHttpHandler(inner);
        using var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://inbox.test/") };

        using (handler.Refuse())
        {
            using var refused = await client.PostAsync("webhook", new StringContent("{}"), ct);
            refused.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            inner.Calls.Should().Be(0, "a refused delivery never reached the inbox");
        }

        using var delivered = await client.PostAsync("webhook", new StringContent("{}"), ct);
        delivered.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Calls.Should().Be(1);
    }

    /// <summary>The inbox endpoint: answers 200 and counts what reached it.</summary>
    private sealed class CountingInbox : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        }
    }
}
