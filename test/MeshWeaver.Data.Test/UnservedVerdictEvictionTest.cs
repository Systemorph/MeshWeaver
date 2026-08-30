using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 <b>The owner-side eviction keys on the router's STAMP, not on the ErrorType it happens to
/// arrive with</b> — the coupling that would otherwise have silently re-opened issues #2426/#2546
/// when the routing leg stopped publishing to the Orleans memory stream.
///
/// <para><b>What the eviction is for.</b> An owner hub serving a data stream to a subscriber whose
/// PROCESS died keeps that server-side stream forever — only an <c>UnsubscribeRequest</c> disposes
/// one, and a corpse sends none. So the owner fans every change out to the dead address and the
/// router refuses each one. The one signal that can end the loop is the router's authoritative
/// <see cref="DeliveryFailure.TargetUnserved"/> verdict reaching the owner, which disposes that
/// subscriber's server-side stream (20,718 error lines in 3 h on memex-cloud before it did).</para>
///
/// <para>🚨 <b>The stamp says WHOSE delivery this is; the ErrorType says WHAT TO DO — #2756.</b>
/// The verdict used to have exactly one producer — the stream leg's no-live-subscriber refusal,
/// which stamps it beside <see cref="ErrorType.NotFound"/>. Since
/// <c>RoutingGrain.AnswerPodHubNotHere</c> the router reaches a similar conclusion ONE HOP EARLIER,
/// through Orleans' grain directory, but reports it as the TRANSIENT
/// <see cref="ErrorType.ShuttingDown"/> — the owner may simply be mid-roll, or waiting for its
/// pod-hub claim to land.</para>
///
/// <para>#2745 gated the eviction on the STAMP ALONE, reasoning the two verdicts were
/// complementary: the subscriber rides the transient one out while the owner drops its half. They
/// are not. <c>JsonSynchronizationStream</c> rides <c>ShuttingDown</c> out by RE-ARMING rather than
/// re-subscribing, so an owner that evicts on it disposes the server-side half of a subscription
/// whose other half is deliberately sitting still. That turned main RED on
/// <c>ObservableQueryTests.ObserveQuery_EmitsRemovedOnDeletedNode</c>, where both halves share one
/// process and the eviction wins the race. The leak this handler closes (#2426/#2546) is a
/// subscriber whose PROCESS IS GONE — the terminal verdict — and only that one may evict.</para>
///
/// <para><b>Fails on unfixed code:</b>
/// <see cref="ATransientUnservedVerdict_LeavesTheOwnersServerSideStreamAlone"/> — with the
/// stamp-alone gate the transient verdict evicts and the subscription is destroyed.</para>
///
/// <para>See <c>Doc/Architecture/DurableStreamsViaMeshNodes</c> and
/// <c>Doc/Architecture/ErrorPropagationAndWedges</c>.</para>
/// </summary>
public class UnservedVerdictEvictionTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(source => source
                .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        base.ConfigureClient(configuration)
            .AddData(data => data.AddHubSource(
                CreateHostAddress(),
                source => source.WithType<BusinessUnit>()));

    /// <summary>
    /// Drives a real subscription so the host genuinely holds a server-side stream for the client,
    /// then hands the host the router's verdict about it.
    /// </summary>
    private async Task<(IMessageHub Host, IMessageHub Client, Workspace HostWorkspace)> ServingClientAsync()
    {
        var client = GetClient();
        var clientWorkspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        // Data arrived ⇒ the owner built and is serving a stream for this subscriber. Waiting on
        // the DATA rather than on a delay is what makes "the owner had something to evict" a fact.
        var units = await clientWorkspace.GetObservable<BusinessUnit>()
            .Should().Within(10.Seconds()).Emit();
        units.Should().NotBeEmpty("the subscription must be live before the verdict arrives");

        var host = GetHost();
        var hostWorkspace = (Workspace)host.GetWorkspace();
        hostWorkspace.ClientSubscriptionsEvicted.Should().Be(0,
            "nothing has told the owner that this subscriber is unreachable yet");
        return (host, client, hostWorkspace);
    }

    /// <summary>
    /// The router's verdict, in exactly the shape <c>RoutingGrain.PostFailure</c> stamps it: the
    /// fanned-out delivery it could not carry, the <c>TargetUnserved</c> stamp, and the transient
    /// ErrorType that keeps the SUBSCRIBER's own recovery armed.
    /// </summary>
    private static DeliveryFailure RouterVerdict(IMessageHub host, Address subscriber, ErrorType errorType) =>
        new(new MessageDelivery<RawJson>(
                host.Address, subscriber,
                new RawJson("{\"$type\":\"DataChangedEvent\"}"),
                JsonSerializerOptions.Default),
            $"Directed delivery to pod hub '{subscriber}' was refused: no silo in this cluster is "
            + "currently serving that hub.")
        {
            ErrorType = errorType,
            TargetUnserved = true,
        };

    /// <summary>Marker that the owner writes after the transient verdict — its arrival on the
    /// client is the proof that the server-side stream was not evicted.</summary>
    private const string SurvivedTheVerdict = "survived-the-transient-verdict";

    private static Task<int> EvictedAsync(Workspace workspace) =>
        Observable.Interval(50.Milliseconds())
            .StartWith(0L)
            .Select(_ => workspace.ClientSubscriptionsEvicted)
            .Where(n => n >= 1)
            .FirstAsync()
            .Timeout(20.Seconds())
            .ToTask(TestContext.Current.CancellationToken);

    /// <summary>
    /// 🚨 THE PIN (#2756). A TRANSIENT unserved verdict must leave the server-side stream intact:
    /// the subscriber rides <c>ShuttingDown</c> out by re-arming, not by re-subscribing, so an
    /// eviction here destroys the half it is waiting for.
    /// </summary>
    [HubFact]
    public async Task ATransientUnservedVerdict_LeavesTheOwnersServerSideStreamAlone()
    {
        var (host, client, hostWorkspace) = await ServingClientAsync();

        host.Post(RouterVerdict(host, client.Address, ErrorType.ShuttingDown),
            o => o.WithTarget(host.Address));

        // 🚨 The assertion is a POSITIVE signal, not an elapsed window: change the data on the
        // OWNER and require the change to reach the client's mirror. That can only happen over the
        // server-side stream this verdict must not have evicted — so the wait is on the property
        // itself, and a regression fails as a timeout rather than passing before the verdict was
        // even handled. (A counter check alone would do the latter: it can be read as zero long
        // before the delivery reaches the handler.)
        var hostUnits = await hostWorkspace.GetStream<BusinessUnit>()!
            .Should().Within(10.Seconds()).Emit();
        var updated = hostUnits!.First() with { DisplayName = SurvivedTheVerdict };
        host.Post(new DataChangeRequest { Updates = [updated] });

        var clientWorkspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        var mirrored = await clientWorkspace
            .GetObservable<BusinessUnit>(updated.SystemName)
            .Should().Within(10.Seconds())
            .Match(x => x!.DisplayName == SurvivedTheVerdict);
        mirrored.Should().Be(updated,
            "ShuttingDown is the platform's transient verdict — JsonSynchronizationStream rides it "
            + "out and RE-ARMS rather than re-subscribing. Evicting on it disposes the server-side "
            + "half of a live subscription whose other half is deliberately waiting, so the owner's "
            + "next change never arrives (#2756, which turned main red on "
            + "ObservableQueryTests.ObserveQuery_EmitsRemovedOnDeletedNode)");

        // Only now is the counter meaningful: the update above proves the verdict was delivered
        // and handled, so a zero here is a decision, not a race.
        hostWorkspace.ClientSubscriptionsEvicted.Should().Be(0,
            "no eviction may be recorded for a transient verdict");
    }

    /// <summary>
    /// The verdict that has always worked, unchanged: the stream leg's no-live-subscriber refusal
    /// stamps the same verdict beside <see cref="ErrorType.NotFound"/>, and it must keep evicting.
    /// </summary>
    [HubFact]
    public async Task ATerminalUnservedVerdict_StillEvicts()
    {
        var (host, client, hostWorkspace) = await ServingClientAsync();

        host.Post(RouterVerdict(host, client.Address, ErrorType.NotFound),
            o => o.WithTarget(host.Address));

        (await EvictedAsync(hostWorkspace)).Should().BeGreaterThanOrEqualTo(1,
            "widening the gate must not narrow it anywhere — the original producer's verdict is "
            + "untouched");
    }

    /// <summary>
    /// 🚨 NEGATIVE CONTROL, and the reason the gate is the stamp at all. A LIVE hub also answers
    /// NotFound — an unhandled request — and evicting on that would tear down a healthy
    /// subscriber's stream. Only the router stamps <c>TargetUnserved</c>; without the stamp the
    /// failure is not ours to act on, whatever its ErrorType says.
    /// </summary>
    [HubFact]
    public async Task AnUnstampedFailure_NeverEvicts_WhateverItsErrorType()
    {
        var (host, client, hostWorkspace) = await ServingClientAsync();

        foreach (var errorType in new[] { ErrorType.NotFound, ErrorType.ShuttingDown, ErrorType.Failed })
            host.Post(
                RouterVerdict(host, client.Address, errorType) with { TargetUnserved = false },
                o => o.WithTarget(host.Address));

        // A negative assertion with no positive signal to filter for: give the hub's action block
        // time to process all three, then assert nothing was evicted.
        await Task.Delay(1_000, TestContext.Current.CancellationToken);

        hostWorkspace.ClientSubscriptionsEvicted.Should().Be(0,
            "an application-level NACK from a LIVE hub must never cost a healthy subscriber its "
            + "server-side stream — absence of the router's stamp means 'do not evict'");
    }
}
