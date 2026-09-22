using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>Keyed record served by the owner hub in <see cref="PerRequestRegistrantsLeaveTheOwnerHubTest"/>.</summary>
/// <param name="Id">The key.</param>
/// <param name="Name">A value the requests below change.</param>
public record RegistrantProbeRecord(string Id, string Name);

/// <summary>
/// 🚨 <b>Every request an owner hub served used to leave a registrant on it for the rest of its
/// life.</b> Systemorph/MeshWeaver#3432.
///
/// <para><b>The root.</b> A hub's registered cleanups live in one append-only composite
/// (<see cref="MessageHub.DisposalRegistrantCount"/>): <c>CompositeDisposable.Add</c> never prunes and
/// nothing removed an entry. The data handlers handed it one or more cleanups PER REQUEST — the
/// validator subscription and the change subscription of every <see cref="DataChangeRequest"/>, the
/// read subscription (with its teardown NACK) of every <see cref="GetDataRequest"/>, the ack watcher
/// and the owner-disposing NACK of every <see cref="PatchDataRequest"/>, the answer subscription of
/// every unified-reference update — and each was held, with the request delivery and everything its
/// closures captured, long after the request had been answered.</para>
///
/// <para><b>The fix</b> detaches each registrant at the moment its own disposal can no longer do
/// anything: a subscription at its terminal (<see cref="HubHeldSubscriptionExtensions.SubscribeHeldUntilTerminal{T}"/>),
/// a NACK once its once-only answer gate is claimed
/// (<see cref="IMessageHub.RegisterForDisposalDetachable"/>). A request still in flight when the hub
/// goes down is torn down — and NACKed — exactly as before.</para>
///
/// <para><b>Controls.</b> Each arm first proves the requests were ANSWERED (so a flat count is not
/// read off requests that never ran), then waits for the owner's count to return to its floor. The
/// growing-direction control that shows the counter can move is
/// <see cref="StreamRegistrantsLeaveTheSubscribingHubTest.TheRegistrantCounter_MovesWhenSomethingIsActuallyRegistered"/>;
/// the unfixed-build readings are reported in the pull request.</para>
/// </summary>
public class PerRequestRegistrantsLeaveTheOwnerHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const int Requests = 10;

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(source =>
                source.WithType<RegistrantProbeRecord>(type => type
                    .WithKey(instance => instance.Id)
                    .WithInitialData(_ => Observable.Return<IEnumerable<RegistrantProbeRecord>>(
                        [new RegistrantProbeRecord("1", "First")])))));

    /// <summary>
    /// Ten answered <see cref="DataChangeRequest"/>s. Before the fix: +2 permanent registrants per
    /// write on the owner.
    /// </summary>
    [HubFact]
    public async Task AnsweredDataChangeRequests_LeaveNoRegistrantOnTheOwner()
    {
        var host = await StartedHost();
        var floor = RegistrantCount(host);
        var client = GetClient();

        for (var i = 0; i < Requests; i++)
        {
            var response = await client
                .Observe(new DataChangeRequest { Updates = [new RegistrantProbeRecord("1", $"v{i}")] },
                    o => o.WithTarget(CreateHostAddress()))
                .Should().Within(10.Seconds()).Emit($"data change {i} must be answered");
            response.Message.Should().BeOfType<DataChangeResponse>()
                .Which.Status.Should().Be(DataChangeStatus.Committed);
        }

        await AssertReturnsToFloor(host, floor, "DataChangeRequest");
    }

    /// <summary>
    /// Ten answered <see cref="GetDataRequest"/>s over a reference whose answer COMPLETES. Before
    /// the fix: +1 permanent registrant per read, with every read long answered.
    /// </summary>
    [HubFact]
    public async Task AnsweredOneShotGetDataRequests_LeaveNoRegistrantOnTheOwner()
    {
        var host = await StartedHost();
        var floor = RegistrantCount(host);
        var client = GetClient();

        for (var i = 0; i < Requests; i++)
        {
            var response = await client
                .Observe(new GetDataRequest(new DataModelReference()), o => o.WithTarget(CreateHostAddress()))
                .Should().Within(10.Seconds()).Emit($"read {i} must be answered");
            response.Message.Should().BeOfType<GetDataResponse>()
                .Which.Error.Should().BeNull("the data model read must succeed, or this measures a failure path");
        }

        await AssertReturnsToFloor(host, floor, "GetDataRequest");
    }

    /// <summary>
    /// Ten answered <see cref="PatchDataRequest"/>s on the generic (non-MeshNode) path. Before the
    /// fix: the ack watcher and the owner-disposing NACK stayed registered per patch.
    /// </summary>
    [HubFact]
    public async Task AnsweredPatchDataRequests_LeaveNoRegistrantOnTheOwner()
    {
        var host = await StartedHost();
        var floor = RegistrantCount(host);
        var client = GetClient();

        for (var i = 0; i < Requests; i++)
        {
            var patch = JsonSerializer.Serialize(new { name = $"patched{i}" });
            var response = await client
                .Observe(new PatchDataRequest(new EntityReference(nameof(RegistrantProbeRecord), "1"), new RawJson(patch)),
                    o => o.WithTarget(CreateHostAddress()))
                .Should().Within(10.Seconds()).Emit($"patch {i} must be answered");
            var patchResponse = response.Message.Should().BeOfType<PatchDataResponse>().Subject;
            patchResponse.Success.Should().BeTrue(
                $"patch {i} must commit, or this measures a failure path ({patchResponse.Error})");
        }

        await AssertReturnsToFloor(host, floor, "PatchDataRequest");
    }

    /// <summary>
    /// Ten answered <see cref="UpdateUnifiedReferenceRequest"/>s. Before the fix: +1 permanent
    /// registrant per update.
    /// </summary>
    [HubFact]
    public async Task AnsweredUnifiedReferenceUpdates_LeaveNoRegistrantOnTheOwner()
    {
        var host = await StartedHost();
        var floor = RegistrantCount(host);
        var client = GetClient();

        for (var i = 0; i < Requests; i++)
        {
            var response = await client
                .Observe(new UpdateUnifiedReferenceRequest("unknownprefix:x", "x"),
                    o => o.WithTarget(CreateHostAddress()))
                .Should().Within(10.Seconds()).Emit($"update {i} must be answered");
            response.Message.Should().BeOfType<UpdateUnifiedReferenceResponse>();
        }

        await AssertReturnsToFloor(host, floor, "UpdateUnifiedReferenceRequest");
    }

    /// <summary>
    /// 🚨 <b>The coverage control — a request still in flight when the owner goes down is still
    /// torn down by it.</b> A registrant detached too eagerly would leave exactly this case without a
    /// cleanup, so it is measured directly: a subscription held through the new surface and never
    /// terminated must be disposed by the hub's teardown, and one that has terminated must not be
    /// counted.
    /// </summary>
    [HubFact]
    public async Task AHeldSubscription_IsDisposedByTheTeardownWhileLive_AndLeavesAtItsTerminal()
    {
        var host = await StartedHost();
        var floor = RegistrantCount(host);

        var terminated = host.SubscribeHeldUntilTerminal(Observable.Return(1), s => s.Subscribe(_ => { }));
        RegistrantCount(host).Should().Be(floor,
            "a subscription that terminated inside Subscribe must not stay registered — the detach "
            + "runs before the handle exists and must not be lost to that ordering");

        var disposed = false;
        var live = host.SubscribeHeldUntilTerminal(
            Observable.Never<int>().Finally(() => disposed = true), s => s.Subscribe(_ => { }));
        RegistrantCount(host).Should().Be(floor + 1, "a live subscription IS held by the hub");

        host.Dispose();
        await host.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("the owner must finish its teardown", cancellationToken: TestContext.Current.CancellationToken);

        disposed.Should().BeTrue(
            "the hub's teardown must still cancel a subscription that was live when it went down — "
            + "detaching is for terminated subscriptions only");
        terminated.Dispose();
        live.Dispose();
    }

    private async Task<IMessageHub> StartedHost()
    {
        var host = GetHost();
        await host.GetWorkspace().GetObservable<RegistrantProbeRecord>()
            .Should().Within(10.Seconds()).Match(x => x.Any(r => r.Id == "1"),
                cancellationToken: TestContext.Current.CancellationToken);
        return host;
    }

    /// <summary>
    /// The detach runs on the owner after its answer has been posted, so the client can hold the
    /// answer a moment before the owner's count settles — wait on the count itself.
    /// </summary>
    private async Task AssertReturnsToFloor(IMessageHub host, int floor, string what)
    {
        var settled = await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Select(_ => RegistrantCount(host))
            .Where(count => count <= floor)
            .Should().Within(5.Seconds())
            .Emit($"{Requests} answered {what}s must leave the owner's registrant count at its floor "
                + $"({floor}); it read {RegistrantCount(host)} when the wait began — one retained registrant graph per "
                + "request served, for the owner's whole life (#3432)",
                cancellationToken: TestContext.Current.CancellationToken);
        Output.WriteLine($"DIAG {what}: floor={floor} settled={settled}");
    }

    private static int RegistrantCount(IMessageHub hub) => ((MessageHub)hub).DisposalRegistrantCount;
}
