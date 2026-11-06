using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>#7842 — a REPLY the router refuses because no silo serves its target must end in a NAMED
/// <see cref="DeliveryFailure"/> at the hub WAITING for it, never in silence.</b>
///
/// <para><b>What production measured.</b> During a roll a cache hub's <c>SubscribeRequest</c>s were
/// handled by their owners, and the replies were refused on the way back: "Directed delivery to pod hub
/// 'cache/…' was refused: no silo in this cluster is currently serving that hub". The router told only
/// the REPLIER (<c>RoutingGrain.AnswerPodHubNotHere</c>), which has nothing to retry; the cache hub that
/// had asked was alive throughout and timed out after 60 s with no name for what happened.</para>
///
/// <para><b>The fault, through a real seam.</b> Two multi-silo silos (no memory stream). A responder on
/// silo 1 answers a <see cref="PingRequest"/> with a CORRELATED reply (<c>ResponseFor</c>). An asker on
/// silo 0 registers while its pod-hub claim (<c>IPodHubGrain.Attach</c>) is HELD by
/// <see cref="ClaimHold"/>, an outgoing Orleans grain-call filter — so the grain directory holds no
/// entry for the asker, exactly the state a lost entry leaves. The ping is handed straight to the
/// routing grain (the ungated shape), the reply meets an <c>IPodHubGrain</c> activation with no local
/// route on silo 1, and the router refuses it. The claim stays held for the whole case, so nothing but
/// the refusal path can reach the asker.</para>
///
/// <para><b>Assertion.</b> The asker's own route receives a <see cref="DeliveryFailure"/> correlated by
/// the ORIGINAL request's id — the key a <c>hub.Observe(...)</c> callback matches on — classified
/// <see cref="ErrorType.ShuttingDown"/> (outcome unconfirmed, ask again) and naming the refusal, within
/// <see cref="Prompt"/>, far inside the 60 s budget production ran out.</para>
///
/// <para><b>Negative controls.</b> (1) Without the fix this case fails: nothing reaches the asker
/// (measured by removing the router's report call; recorded in <c>Doc/Architecture/PodHubClaimReassertion</c>).
/// (2) <see cref="AnUncorrelatedMessageRefusedOnItsWayIsNotReportedTest"/>: the same refusal of a message
/// that is NOT a reply (no request id) mints no failure at its target — the report is for a hub that is
/// waiting, and an uncorrelated push has nobody waiting for it.</para>
/// </summary>
public abstract class ARefusedDeliveryAndItsAsker(FaultInjectionCluster mesh)
{
    private const int Asker = 0;
    private const int Responder = 1;

    /// <summary>Far inside the 30 s / 60 s budgets production ran out; the report is a few grain calls.</summary>
    protected static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    /// <summary>The catalog key of the asker's sentence (<c>RoutingGrain.RefusedReplyMessageKey</c>, internal there).</summary>
    internal const string RefusedReplyMessageKey = "error.replyRefusedOnTheWay";

    /// <summary>Whether the responder answers with a correlated REPLY (true) or an uncorrelated message.</summary>
    protected abstract bool AnswersWithACorrelatedReply { get; }

    [Fact(Timeout = 120_000)]
    public async Task ARefusedDelivery_ToAnAskerWhoseClaimIsMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var askerRouting = mesh.Hub(Asker).ServiceProvider.GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        var responderRouting = mesh.Hub(Responder).ServiceProvider.GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        askerRouting.HasMemoryStream.Should().BeFalse(
            "with a memory stream a refused pod-hub delivery falls back to the stream and this case would prove nothing");

        // ── The responder on silo 1: answers the ping, and hears its own refusal ─────────────────────
        var responder = new Address(OrleansTestMeshExtensions.SiloHostedStreamRoutedType, $"answer-{Guid.NewGuid():N}");
        var responderHeard = new ReplaySubject<IMessageDelivery>();
        var dispatchErrors = new ReplaySubject<Exception>();
        using var responderRegistration = responderRouting.RegisterStream(responder, (d, _) =>
        {
            responderHeard.OnNext(d);
            if (d.Message is PingRequest && d.Sender is { } back)
            {
                var options = AnswersWithACorrelatedReply
                    ? new PostOptions(responder).ResponseFor(d)
                    : new PostOptions(responder).WithTarget(back);
                // PACKAGED, as every production delivery reaches the router: the payload is RawJson.
                responderRouting.DeliverMessage(new MessageDelivery<PingResponse>(
                        new PingResponse(), options, System.Text.Json.JsonSerializerOptions.Default).Package())
                    .Subscribe(_ => { }, dispatchErrors.OnNext);
            }
            return Observable.Return(d.Processed());
        });
        await (responderRouting.PodHubClaimSettled(responder) ?? Observable.Return(Unit.Default))
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit("the responder's claim lands", ct);

        // ── The asker on silo 0, its claim HELD for the whole case ──────────────────────────────────
        var asker = new Address(OrleansTestMeshExtensions.SiloHostedStreamRoutedType, $"asker-{Guid.NewGuid():N}");
        using var claim = mesh.Silo(Asker).GetRequiredService<ClaimHold>().Hold(asker.ToString());
        var heard = new ReplaySubject<IMessageDelivery>();
        using var registration = askerRouting.RegisterStream(asker, (d, _) =>
        {
            heard.OnNext(d);
            return Observable.Return(d.Processed());
        });
        try
        {
            await claim.Arrivals.Should().Within(TestTimeouts.Convergence)
                .Emit("the asker's claim must be in flight and held — otherwise this case injected nothing", ct);

            // The asker reads German: the language the reply carries back (ResponseFor copies it).
            var ping = new MessageDelivery<PingRequest>(
                new PingRequest(), new PostOptions(asker).WithTarget(responder),
                System.Text.Json.JsonSerializerOptions.Default)
                 with { AccessContext = new AccessContext { ObjectId = "asker", Locale = "de" } };
            // Straight to the routing grain: the ungated shape, so the answer overtakes the held claim.
            await mesh.Silo(Asker).GetRequiredService<IGrainFactory>().GetGrain<IRoutingGrain>("default")
                .RouteMessage(ping).WaitAsync(TestTimeouts.Convergence, ct);

            var answer = heard.Where(d => d.Message is PingResponse or RawJson && d.Sender == responder);
            var failure = heard.Where(d => d.Message is DeliveryFailure);

            // POSITIVE: the refusal happened — the router NACKed the responder for its answer. Without
            // this the asker-side assertions below could pass on an answer that never left.
            var refusal = await responderHeard.Where(d => d.Message is DeliveryFailure).Take(1)
                .Should().Within(Prompt).Emit(
                    $"the answer to '{asker}' must be refused on silo {Responder} and the responder NACKed", ct);
            refusal.Message.Should().BeOfType<DeliveryFailure>().Which.TargetUnserved.Should().BeTrue(
                "it is the router's 'no silo serves that hub' verdict, not some other failure");
            await dispatchErrors.Should().NotEmit(TimeSpan.FromMilliseconds(1),
                "the answer's dispatch must not have faulted — a faulted dispatch is not a refusal", ct);

            if (AnswersWithACorrelatedReply)
            {
                var told = await failure.Take(1).Should().Within(Prompt).Emit(
                    $"the reply to request {ping.Id} was refused on silo {Responder} because no silo serves '{asker}' "
                    + "— the asker that waits for it must be told at once, by name, instead of waiting out its budget (#7842)",
                    ct);
                told.Properties.TryGetValue(PostOptions.RequestId, out var correlation).Should().BeTrue(
                    "the failure must carry a request id, or no callback can match it");
                correlation?.ToString().Should().Be(ping.Id,
                    "the failure must be correlated by the ORIGINAL request's id — the key the asker's callback matches on");
                var verdict = told.Message.Should().BeOfType<DeliveryFailure>().Subject;
                verdict.ErrorType.Should().Be(ErrorType.ShuttingDown,
                    "the reply's work may have happened: the outcome is unconfirmed, and the asker's own recovery re-asks on it");
                verdict.Message.Should().Be(LocalizationCatalog.Get(RefusedReplyMessageKey, "de"),
                    "the asker's error is the catalog's named sentence, in the asker's language, not a timeout");
            }
            else
            {
                await failure.Should().NotEmit(Prompt,
                    "an uncorrelated message has nobody waiting for it: the refusal is reported to its sender only", ct);
            }

            await answer.Should().NotEmit(TimeSpan.FromMilliseconds(500),
                "the claim is still held, so the answer itself was refused — the case measured the refusal path", ct);
        }
        finally
        {
            claim.Release();
        }
    }
}

/// <summary>The fix (#7842): the asker of a refused reply is told by name.</summary>
public class ARefusedReplyTellsItsAskerByNameTest(ARefusedReplyTellsItsAskerByNameTest.Cluster mesh)
    : ARefusedDeliveryAndItsAsker(mesh), IClassFixture<ARefusedReplyTellsItsAskerByNameTest.Cluster>
{
    /// <inheritdoc />
    protected override bool AnswersWithACorrelatedReply => true;

    /// <summary>Two multi-silo silos with the claim hold.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 2;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloClaimHoldConfigurator);
    }
}

/// <summary>Negative control: a refused message that is NOT a reply mints no failure at its target.</summary>
public class AnUncorrelatedMessageRefusedOnItsWayIsNotReportedTest(AnUncorrelatedMessageRefusedOnItsWayIsNotReportedTest.Cluster mesh)
    : ARefusedDeliveryAndItsAsker(mesh), IClassFixture<AnUncorrelatedMessageRefusedOnItsWayIsNotReportedTest.Cluster>
{
    /// <inheritdoc />
    protected override bool AnswersWithACorrelatedReply => false;

    /// <summary>Two multi-silo silos with the claim hold.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 2;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloClaimHoldConfigurator);
    }
}

/// <summary>
/// <b>#7842 — the asker of a refused reply is told even when ITS silo is <c>ShuttingDown</c>, through a
/// carrier that was never called before.</b>
///
/// <para>Orleans places no new activation on a <c>ShuttingDown</c> silo and is deactivating the ones it
/// has, so a per-silo GRAIN could not reach an asker there unless an activation happened to exist. The
/// carrier is a grain SERVICE (a per-silo system target, addressed by the silo, no placement), and this
/// case pins that it reaches a draining asker cold: silo 1 is held in <c>ShuttingDown</c> by
/// <see cref="ShutdownHold"/> while its asker's local route is still live, the asker's claim is held so no
/// registration exists, and the responder's reply is refused on silo 0.</para>
/// </summary>
public class ARefusedReplyReachesAnAskerOnAShuttingDownSiloTest(ARefusedReplyReachesAnAskerOnAShuttingDownSiloTest.Cluster mesh)
    : IClassFixture<ARefusedReplyReachesAnAskerOnAShuttingDownSiloTest.Cluster>
{
    private const int Responder = 0;
    private const int Asker = 1;
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 120_000)]
    public async Task TheAskerOnAShuttingDownSilo_IsToldByAColdCarrier()
    {
        var ct = TestContext.Current.CancellationToken;
        var askerRouting = mesh.Hub(Asker).ServiceProvider.GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        var responderRouting = mesh.Hub(Responder).ServiceProvider.GetRequiredService<IRoutingService>()
            .Should().BeOfType<OrleansRoutingService>().Subject;
        var askerSilo = mesh.Silo(Asker).GetRequiredService<ILocalSiloDetails>().SiloAddress;

        var responder = new Address(OrleansTestMeshExtensions.SiloHostedStreamRoutedType, $"answer-{Guid.NewGuid():N}");
        var responderHeard = new ReplaySubject<IMessageDelivery>();
        var dispatchErrors = new ReplaySubject<Exception>();
        using var responderRegistration = responderRouting.RegisterStream(responder, (d, _) =>
        {
            responderHeard.OnNext(d);
            if (d.Message is PingRequest)
                responderRouting.DeliverMessage(new MessageDelivery<PingResponse>(
                        new PingResponse(), new PostOptions(responder).ResponseFor(d),
                        System.Text.Json.JsonSerializerOptions.Default).Package())
                    .Subscribe(_ => { }, dispatchErrors.OnNext);
            return Observable.Return(d.Processed());
        });
        await (responderRouting.PodHubClaimSettled(responder) ?? Observable.Return(Unit.Default))
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit("the responder's claim lands", ct);

        var asker = new Address(OrleansTestMeshExtensions.SiloHostedStreamRoutedType, $"asker-{Guid.NewGuid():N}");
        using var claim = mesh.Silo(Asker).GetRequiredService<ClaimHold>().Hold(asker.ToString());
        var heard = new ReplaySubject<IMessageDelivery>();
        using var registration = askerRouting.RegisterStream(asker, (d, _) =>
        {
            heard.OnNext(d);
            return Observable.Return(d.Processed());
        });
        await claim.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the asker's claim is in flight and held, so no registration exists for it", ct);

        using var held = mesh.Silo(Asker).GetRequiredService<ShutdownHold>().Arm();
        var stopping = mesh.Drain(Asker);
        try
        {
            await held.Arrivals.Should().Within(TestTimeouts.CrossSilo).Emit(
                $"silo {Asker} has written ShuttingDown and is held there", ct);
            mesh.Silo(Responder).GetRequiredService<IClusterMembershipService>().CurrentSnapshot
                .GetSiloStatus(askerSilo).Should().Be(SiloStatus.ShuttingDown,
                    "the router's own view must have the asker's silo leaving when the reply is refused");
            askerRouting.TryGetLocalRoute(asker).Should().NotBeNull(
                "the asker must still be live on its draining silo — otherwise there is nobody to tell");

            var ping = new MessageDelivery<PingRequest>(
                new PingRequest(), new PostOptions(asker).WithTarget(responder),
                System.Text.Json.JsonSerializerOptions.Default);
            // Routed on the responder's (Active) silo: the asker's own silo is the one leaving.
            await mesh.Silo(Responder).GetRequiredService<IGrainFactory>().GetGrain<IRoutingGrain>("default")
                .RouteMessage(ping).WaitAsync(TestTimeouts.Convergence, ct);

            await responderHeard.Where(d => d.Message is DeliveryFailure).Take(1).Should().Within(Prompt)
                .Emit($"the answer to '{asker}' is refused on silo {Responder} — the positive refusal signal", ct);
            await dispatchErrors.Should().NotEmit(TimeSpan.FromMilliseconds(1), "the answer's dispatch did not fault", ct);

            var told = await heard.Where(d => d.Message is DeliveryFailure).Take(1).Should().Within(Prompt).Emit(
                $"the asker on ShuttingDown silo {Asker} must be told through its silo's carrier, which needs no placement",
                ct);
            told.Properties.TryGetValue(PostOptions.RequestId, out var correlation).Should().BeTrue();
            correlation?.ToString().Should().Be(ping.Id, "correlated by the original request's id");
        }
        finally
        {
            claim.Release();
            held.Release();
            await stopping.WaitAsync(TestTimeouts.CrossSilo, ct);
        }
    }

    /// <summary>Two multi-silo silos with the claim hold and the shutdown hold; its own: the case stops a silo.</summary>
    public class Cluster : FaultInjectionCluster
    {
        /// <inheritdoc />
        protected override int SiloCount => 2;

        /// <inheritdoc />
        protected override Type SiloConfiguratorType => typeof(MultiSiloShutdownHoldConfigurator);
    }
}
