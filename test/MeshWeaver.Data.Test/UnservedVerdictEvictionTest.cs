using System;
using System.Reactive.Linq;
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
/// <para>🚨 <b>Why this test exists NOW.</b> That verdict used to have exactly one producer — the
/// stream leg's no-live-subscriber refusal, which stamps it beside
/// <see cref="ErrorType.NotFound"/> — so the handler could afford a redundant
/// <c>ErrorType == NotFound</c> test beside the stamp. Since
/// <c>RoutingGrain.AnswerPodHubNotHere</c> the router reaches the same conclusion ONE HOP EARLIER,
/// through Orleans' grain directory, and reports it as the TRANSIENT
/// <see cref="ErrorType.ShuttingDown"/> — because a hub whose owner is mid-roll must not have its
/// subscriber's mirrors torn down. Had the ErrorType test survived, that verdict would have been
/// inert here and every dead circuit in the fleet would have leaked its server-side streams again.
/// The two facts are complementary, not contradictory: the SUBSCRIBER rides the transient verdict
/// out and re-asks; the OWNER drops the half it can no longer push to.</para>
///
/// <para><b>Fails on unfixed code:</b>
/// <see cref="ATransientUnservedVerdict_EvictsTheOwnersServerSideStream"/> — with the
/// <c>ErrorType == NotFound</c> test in place the eviction counter never leaves zero.</para>
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

    private static Task<int> EvictedAsync(Workspace workspace) =>
        Observable.Interval(50.Milliseconds())
            .StartWith(0L)
            .Select(_ => workspace.ClientSubscriptionsEvicted)
            .Where(n => n >= 1)
            .FirstAsync()
            .Timeout(20.Seconds())
            .Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// 🚨 THE PIN. The transient verdict the retired stream leg's replacement produces must still
    /// end the fan-out at its source.
    /// </summary>
    [HubFact]
    public async Task ATransientUnservedVerdict_EvictsTheOwnersServerSideStream()
    {
        var (host, client, hostWorkspace) = await ServingClientAsync();

        host.Post(RouterVerdict(host, client.Address, ErrorType.ShuttingDown),
            o => o.WithTarget(host.Address));

        (await EvictedAsync(hostWorkspace)).Should().BeGreaterThanOrEqualTo(1,
            "the eviction gate is the router's TargetUnserved STAMP — the only component that asks "
            + "the cluster — and never the ErrorType beside it. Gating on NotFound made the pod-hub "
            + "refusal inert and left every dead circuit fanning out forever (#2426/#2546)");
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
