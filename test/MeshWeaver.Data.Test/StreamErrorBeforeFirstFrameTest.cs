using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// An owner that ends a stream BEFORE its first frame must reach the subscriber (MeshWeaver#7919).
///
/// <para>The subscriber's sync sub-hub holds every delivery behind its <c>SynchronizationGate</c>
/// until the first frame arrives, except the ones that can populate or end the stream. The owner's
/// <see cref="StreamErrorEvent"/> was missing from that list. A followed read whose FIRST verdict
/// refuses posts the error and never a frame, so the error waited behind a gate only a frame could
/// open: the mirror never faulted, and the error was discarded when the sub-hub was finally
/// disposed — the <c>[DISPOSE-DISCARD]</c> of a <see cref="StreamErrorEvent"/> behind
/// <c>[SynchronizationGate]</c> that memex-cloud logged on 2026-11-06.</para>
/// </summary>
public class StreamErrorBeforeFirstFrameTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>The followed read's verdict; false from the start in the pin.</summary>
    private BehaviorSubject<bool> verdict = new(false);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds.WithType<MyData>(t => t.WithKey(d => d.Id))))
            .WithClientSubscriptionReadAdmission((_, _) => new ClientReadFollow(_ => verdict, TestTimeouts.Convergence));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddSource(ds => ds.WithType<MyData>(t => t.WithKey(d => d.Id))));

    private (ISynchronizationStream<EntityStore> Mirror, IConnectableObservable<Exception> Ended) Subscribe()
    {
        var host = GetHost();
        var client = GetClient();
        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var mirror = client.GetWorkspace()
            .GetRemoteStream<EntityStore>(CreateHostAddress(), new CollectionsReference(collectionName));
        var ended = mirror.Materialize()
            .Where(n => n.Kind == NotificationKind.OnError)
            .Select(n => n.Exception!)
            .Replay(1);
        return (mirror, ended);
    }

    /// <summary>
    /// 🚨 THE PIN. The first verdict refuses, so the owner posts a <see cref="StreamErrorEvent"/>
    /// carrying the denial and no frame at all. The subscriber must see the denial.
    ///
    /// <para><b>Fail-without:</b> the error is deferred behind <c>SynchronizationGate</c> and the
    /// mirror never faults; the assertion times out.</para>
    /// </summary>
    [HubFact]
    public async Task AReadRefusedBeforeItsFirstFrame_ReachesTheSubscriberAsADenial()
    {
        var (_, ended) = Subscribe();
        using var connection = ended.Connect();

        var error = await ended.Should().Within(TestTimeouts.Convergence)
            .Emit("the owner ended the read before any frame, and the subscriber must be told");

        Assert.IsType<UnauthorizedAccessException>(error);
    }

    /// <summary>
    /// NEGATIVE CONTROL: the same subscription with an admitting verdict serves its first snapshot
    /// and does NOT fault — so the fault in the pin is the refusal's, not the harness's.
    ///
    /// <para>🚨 "Does not fault" is PROVEN, not waited out: after the first snapshot the owner
    /// changes the data, and the mirror must SERVE that later revision. A faulted mirror can emit
    /// nothing more (its error is terminal and replayed to every subscriber), so a mirror that
    /// delivers a revision the owner produced after the first frame has demonstrably not faulted
    /// in between — and the error signal is then checked at once, with no window to sit out.</para>
    /// </summary>
    [HubFact]
    public async Task NegativeControl_AnAdmittedRead_ServesItsSnapshotAndDoesNotFault()
    {
        verdict = new BehaviorSubject<bool>(true);
        var (mirror, ended) = Subscribe();
        using var connection = ended.Connect();

        await mirror.Should().Within(TestTimeouts.Convergence).Emit("an admitted read serves its first snapshot");

        var host = GetHost();
        var access = host.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });
        var collectionName = host.GetWorkspace().DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        host.Post(new DataChangeRequest().WithUpdates(new MyData("after-first-frame", "later")),
            o => o.WithAccessContext(access.Context!));

        await mirror
            .Where(ci => ci.Value?.Collections.GetValueOrDefault(collectionName) is { } coll
                && coll.Instances.Values.OfType<MyData>().Any(x => x.Id == "after-first-frame"))
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("an admitted read keeps serving: the revision after its first snapshot reaches the mirror");
        await ended.Should().NotEmitBefore(Observable.Return(Unit.Default), "nothing refused this read");
    }
}
