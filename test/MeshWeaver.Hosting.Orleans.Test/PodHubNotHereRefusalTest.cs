using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>The Orleans memory-stream publish leaves the routing leg — issues #2320 / #2322 / #2406,
/// made UNREACHABLE rather than fixed.</b>
///
/// <para><b>The defect those three tickets name is Orleans-internal, and there is no MeshWeaver
/// line to change on that path.</b> A publish into a stream with no live subscriber SUCCEEDS and
/// discards (the subscriber probe narrows this, but fails open by design); a publish into a stream
/// whose queue grain is wedged, or whose producer never registered, STALLS for the sender's whole
/// 30–60 s budget. Durability cannot fix a request that should not have been queued.</para>
///
/// <para><b>So the leg goes.</b> <c>RoutingGrain.BuildPodHubRoute</c> used to take the stream
/// fallback whenever the pod-hub grain answered <see cref="PodHubNotHereException"/> — a condition
/// that covers BOTH "the owner is an Orleans client, which cannot host a grain" and "the owner is a
/// silo whose claim has not landed yet", and the exception cannot tell them apart. The fallback is
/// now taken ONLY for an address type DECLARED client-hosted
/// (<see cref="MeshConfiguration.ClientHostedAddressTypes"/>); production declares none, so the
/// publish is unreachable there. Everything else is answered in ONE hop with a transient NACK the
/// requester's own recovery rides out, inside the directed call's budget instead of the stream's.
/// See <c>Doc/Architecture/DurableStreamsViaMeshNodes</c>.</para>
///
/// <para><b>The decision is the subject.</b> <see cref="RoutingGrain.AnswerPodHubNotHere"/> is
/// <c>internal static</c> and takes the stream leg as a thunk, so "no stream publish happened" is
/// asserted directly — the thunk was never invoked — rather than inferred from the absence of a
/// side effect. Pure: no cluster, no host, no clock.</para>
///
/// <para><b>Fails on unfixed code:</b>
/// <see cref="AnUndeclaredType_IsNackedTransiently_AndNeverReachesTheStream"/> — the method does not
/// exist, and the arm it replaces published unconditionally.</para>
/// </summary>
public class PodHubNotHereRefusalTest
{
    private static readonly Address Sender = new("client", "pod-hub-sender");

    private static MeshConfiguration Declaring(params string[] clientHostedTypes) =>
        new(Array.Empty<MeshNode>())
        {
            ClientHostedAddressTypes = new HashSet<string>(clientHostedTypes, StringComparer.Ordinal),
        };

    private static IMessageDelivery Delivery(string id, string targetType = "portal") =>
        new MessageDelivery<RawJson>(
            Sender, new Address(targetType, "unclaimed"),
            new RawJson("{\"$type\":\"DataChangedEvent\"}"),
            JsonSerializerOptions.Default) with
        { Id = id };

    private sealed record Nack(string Message, ErrorType Type, bool TargetUnserved);

    /// <summary>
    /// 🚨 THE PIN. An address type nobody declared client-hosted gets a TRANSIENT verdict and NO
    /// publish — a publish that would have succeeded and discarded, or stalled for the sender's
    /// whole budget.
    /// </summary>
    [Fact]
    public async Task AnUndeclaredType_IsNackedTransiently_AndNeverReachesTheStream()
    {
        var nacks = new List<Nack>();
        var published = 0;

        await RoutingGrain.AnswerPodHubNotHere(
                Delivery("d-2320"), "portal/unclaimed", "portal",
                Declaring(),
                fallBackToStream: () => { published++; return Observable.Return(Unit.Default); },
                postFailureToSender: (m, t, u) => nacks.Add(new Nack(m, t, u)),
                logger: new RecordingLogger())
            .Await();

        published.Should().Be(0,
            "the stream publish is the leg #2320/#2322/#2406 live on — it must be UNREACHABLE for a "
            + "type nobody declared client-hosted, not merely unlikely");

        var nack = nacks.Should().ContainSingle().Subject;
        nack.Type.Should().Be(ErrorType.ShuttingDown,
            "SynchronizationStream's resubscribe latch and MeshNodeStreamCache.IsTransientOwnerFailure "
            + "RIDE OUT ShuttingDown and TEAR DOWN on a terminal verdict — and 'no silo serves this hub "
            + "right now' is a lifecycle transition, resolved by the owner's own indefinite claim");
        nack.TargetUnserved.Should().BeTrue(
            "this is the router's authoritative 'nobody serves that address' verdict, the same stamp "
            + "the subscriber probe produces — it is what lets an OWNER drop the server-side stream it "
            + "can no longer push to, ending the #2426 fan-out-to-a-corpse loop");
        nack.Message.Should().Contain("portal/unclaimed");
    }

    /// <summary>
    /// 🚨 THE VERDICT THE EVICTION WAS WRITTEN FOR. When the activation that answered is the
    /// owner's RELEASE tombstone (<see cref="PodHubNotHereException.Released"/> — the registration
    /// that claimed the address was disposed: a closed Blazor circuit, a torn-down hub), "nobody
    /// serves this hub" is a fact, not a roll window, and the router must say so with
    /// <see cref="ErrorType.NotFound"/> beside the stamp. <c>DataExtensions.HandleTargetUnservedFailure</c>
    /// evicts on exactly that pair and — deliberately, #2756 — on nothing that says ShuttingDown; so
    /// before this branch every dead circuit was answered transiently, the eviction never fired, and
    /// the owner fanned every change out to the corpse until the stream's idle release (memex,
    /// 2026-09-03: 46 minutes, 300–1,169 refusals per minute, zero evictions).
    ///
    /// <para><b>Fails on unfixed code:</b> the parameter does not exist and the only verdict the
    /// method can produce is ShuttingDown.</para>
    /// </summary>
    [Fact]
    public async Task AReleasedAddress_IsNackedTerminally_SoTheOwnerEvicts_AndNeverReachesTheStream()
    {
        var nacks = new List<Nack>();
        var published = 0;

        await RoutingGrain.AnswerPodHubNotHere(
                Delivery("d-released"), "portal/closed-circuit", "portal",
                Declaring(),
                fallBackToStream: () => { published++; return Observable.Return(Unit.Default); },
                postFailureToSender: (m, t, u) => nacks.Add(new Nack(m, t, u)),
                logger: new RecordingLogger(),
                respondingSilo: "10.0.0.2:11111@1",
                released: true)
            .Await();

        published.Should().Be(0, "a released address is as unreachable over the stream as over the grain");

        var nack = nacks.Should().ContainSingle().Subject;
        nack.TargetUnserved.Should().BeTrue("the stamp is what makes the verdict the OWNER's to act on");
        nack.Type.Should().Be(ErrorType.NotFound,
            "the owner released the address, so nothing will re-claim it — this is the TERMINAL shape "
            + "HandleTargetUnservedFailure evicts on, and the one shape #2756's ShuttingDown guard must "
            + "never see here, or the fan-out-to-a-corpse storm (#2426/#2546) is back for every closed tab");
        nack.Message.Should().Contain("portal/closed-circuit").And.Contain("RELEASED");
    }

    /// <summary>
    /// The transient shape is UNCHANGED by the release branch: a refusal that is not a release still
    /// rides out as ShuttingDown, so the #2756 guard keeps protecting a subscriber that is merely
    /// mid-roll. Pinned beside the terminal case so the two can never be confused again.
    /// </summary>
    [Fact]
    public async Task AnUnreleasedRefusal_IsStillTransient()
    {
        var nacks = new List<Nack>();
        await RoutingGrain.AnswerPodHubNotHere(
                Delivery("d-unreleased"), "portal/mid-roll", "portal", Declaring(),
                fallBackToStream: () => throw new InvalidOperationException("must not publish"),
                postFailureToSender: (m, t, u) => nacks.Add(new Nack(m, t, u)),
                logger: new RecordingLogger(),
                respondingSilo: "10.0.0.2:11111@1",
                released: false)
            .Await();

        nacks.Should().ContainSingle().Which.Should().Be(nacks[0] with { Type = ErrorType.ShuttingDown, TargetUnserved = true },
            "a claim that has not landed yet must keep the ride-out verdict");
    }

    /// <summary>
    /// The other direction, and the reason the gate is a DECLARATION: for a type whose hubs live in
    /// an Orleans client the stream is not a fallback, it is the only transport there is. NACKing
    /// those would make every such hub permanently unreachable.
    /// </summary>
    [Fact]
    public async Task ADeclaredClientHostedType_StillFallsBackToTheStream()
    {
        var nacks = new List<Nack>();
        var published = 0;

        await RoutingGrain.AnswerPodHubNotHere(
                Delivery("d-client", targetType: "client"), "client/unclaimed", "client",
                Declaring("client"),
                fallBackToStream: () => { published++; return Observable.Return(Unit.Default); },
                postFailureToSender: (m, t, u) => nacks.Add(new Nack(m, t, u)),
                logger: new RecordingLogger())
            .Await();

        published.Should().Be(1,
            "an Orleans client cannot host a grain, so a directed call can never reach it — refusing "
            + "here would take the hub's only transport away");
        nacks.Should().BeEmpty("the delivery was carried, so there is nothing to report");
    }

    /// <summary>
    /// The declaration is OPT-IN and EMPTY by default — which is what makes "production declares
    /// none, so the publish is unreachable in the fleet" a property of the code rather than of a
    /// deployment's configuration.
    /// </summary>
    [Fact]
    public void NothingIsClientHostedUnlessItSaysSo()
    {
        new MeshConfiguration(Array.Empty<MeshNode>()).ClientHostedAddressTypes.Should().BeEmpty(
            "no production process hosts mesh hubs as an Orleans client — UseOrleansMeshClient has "
            + "only test-fixture callers, the distributed portal is a co-hosted silo, and the "
            + "monolith / LocalMesh / bake host run no Orleans at all");

        // ...and the built-in stream-routed types are NOT client-hosted by association: being
        // reachable over the stream says nothing about who hosts the hub.
        var config = new MeshConfiguration(Array.Empty<MeshNode>());
        foreach (var streamRouted in MeshConfiguration.DefaultStreamRoutedAddressTypes)
            config.ClientHostedAddressTypes.Should().NotContain(streamRouted,
                $"'{streamRouted}' is stream-ROUTED, which is a statement about the transport, not "
                + "about whether a grain can be hosted for it");
    }

    /// <summary>
    /// Bounded per delivery: one refusal is one emission that COMPLETES. No retry, no timer, no
    /// watchdog anywhere on this path, so the refusal can never become its own storm.
    /// </summary>
    [Fact]
    public async Task ARefusalIsTerminalPerDelivery()
    {
        var notifications = await RoutingGrain.AnswerPodHubNotHere(
                Delivery("t1"), "portal/unclaimed", "portal", Declaring(),
                fallBackToStream: () => throw new InvalidOperationException("must not publish"),
                postFailureToSender: (_, _, _) => { },
                logger: new RecordingLogger(),
                refusalLog: new DeadTargetRefusalLog(TimeSpan.FromSeconds(60)))
            .Materialize()
            .ToList()
            .Timeout(TimeSpan.FromSeconds(5))
            .Await();

        notifications.Select(n => n.Kind).Should().Equal(NotificationKind.OnNext, NotificationKind.OnCompleted);
    }

    /// <summary>
    /// The log window, same contract as <c>RefuseNoSubscriber</c>: a KNOWN-unreachable address must
    /// not buy a full line per delivery (#2426/#2546 — 20,718 lines in 3 h), while EVERY delivery is
    /// still refused and still NACKed, because the NACK is each sender's terminal answer AND the
    /// owner-side eviction signal.
    /// </summary>
    [Fact]
    public async Task AHundredRefusalsOfOneAddress_ShipOneFullLine_AndAHundredNacks()
    {
        var logger = new RecordingLogger();
        var refusalLog = new DeadTargetRefusalLog(TimeSpan.FromSeconds(60));
        var nacks = new List<Nack>();
        var config = Declaring();

        for (var i = 0; i < 100; i++)
            await RoutingGrain.AnswerPodHubNotHere(
                    Delivery($"d{i}"), "portal/unclaimed", "portal", config,
                    fallBackToStream: () => throw new InvalidOperationException("must not publish"),
                    postFailureToSender: (m, t, u) => nacks.Add(new Nack(m, t, u)),
                    logger: logger, refusalLog: refusalLog)
                .Await();

        nacks.Should().HaveCount(100, "the window bounds the LOG, never the answer");
        nacks.Should().OnlyContain(n => n.Type == ErrorType.ShuttingDown && n.TargetUnserved);

        logger.Records.Count(r => r.Level == LogLevel.Warning).Should().Be(1,
            "one full line per address per window — Warning rather than Error because the verdict is "
            + "transient by construction and the owner's claim is still landing");
        logger.Records.Count(r => r.Level == LogLevel.Debug).Should().Be(99,
            "the suppressed volume still leaves per-delivery evidence");
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> records = [];
        public IReadOnlyList<(LogLevel Level, string Message)> Records => records;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => records.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
