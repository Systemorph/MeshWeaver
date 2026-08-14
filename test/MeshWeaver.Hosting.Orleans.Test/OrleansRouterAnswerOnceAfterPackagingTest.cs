using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the answer-once contract ACROSS THE PACKAGING BOUNDARY (issue #1485).
///
/// <para><b>The defect.</b> <c>MeshBuilder</c> hands the router
/// <c>delivery.Package(hub.JsonSerializerOptions)</c>, which replaces the payload with
/// <see cref="RawJson"/>. Every answer-once guard in the routing layer was written as a CLR-type
/// test on <c>delivery.Message</c> — <c>is DeliveryFailure</c> / <c>HasAttribute&lt;CanBeIgnored&gt;</c>
/// — so on the routed path it inspects <c>RawJson</c> and can never match. The router therefore
/// NACKed heartbeats to a departed owner (the NotFound storm its own comments name) and could NACK
/// a NACK.</para>
///
/// <para><b>The invariant.</b> A delivery's answerability is a property of the payload it was
/// posted with, not of the envelope's current CLR type — so it must survive the erasure. The first
/// test below is the mechanism made explicit (packaging really does erase the type), the rest are
/// the behaviour.</para>
///
/// <para><b>No cluster, no mocks, no timing.</b> Same harness as
/// <see cref="OrleansRoutingShutdownClassificationTest"/>: a real
/// <see cref="IHostApplicationLifetime"/> drives the router into its shutdown branch (the branch
/// that consults the guard), and a real <see cref="IMessageHub"/> sits at the sender's address so
/// every assertion is made on the actual <see cref="DeliveryFailure"/> the router posts.</para>
///
/// <para>🚨 <b>Nothing here polls or samples.</b> The NACKs the router posts are pushed onto a
/// <see cref="ReplaySubject{T}"/>, and the assertions SUBSCRIBE to a running fold of that stream via
/// <c>.Should().Within(...).Match(...)</c> — the repo's reactive assertion idiom, which settles on
/// the first emission satisfying the predicate. No <c>FirstAsync</c>, no <c>Take(1)</c>, no
/// <c>TaskCompletionSource</c>, no sleep-then-inspect.</para>
/// </summary>
public class OrleansRouterAnswerOnceAfterPackagingTest : TestBase
{
    private static readonly Address SenderAddress = new("portal", "answer-once-sender");
    private static readonly Address TargetAddress = new("SomeNamespace", "SomeNode");

    /// <summary>
    /// Every delivery id the router answered with a <see cref="DeliveryFailure"/>, in order.
    /// A <see cref="ReplaySubject{T}"/> so an assertion that subscribes AFTER the dispatch still
    /// sees the whole history — the alternative (subscribe first, then dispatch) would make the
    /// test's own ordering load-bearing for no benefit.
    /// </summary>
    private readonly ReplaySubject<string> nacks = new();

    private readonly IHostApplicationLifetime lifetime;

    public OrleansRouterAnswerOnceAfterPackagingTest(ITestOutputHelper output) : base(output)
    {
        lifetime = new HostBuilder().Build().Services.GetRequiredService<IHostApplicationLifetime>();
        Services.AddSingleton(lifetime);
        Services.AddSingleton<AccessService>();
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(SenderAddress, conf => conf
            .WithHandler<DeliveryFailure>((_, d) =>
            {
                nacks.OnNext(d.Message.Delivery.Id);
                return d.Processed();
            })
            .WithPostingIdentity(PostingIdentity.System)));
    }

    private IMessageHub Hub => ServiceProvider.GetRequiredService<IMessageHub>();

    /// <summary>
    /// The running list of everything answered so far. Subscribing to THIS and matching on
    /// "the control has been answered" is what makes the assertion a positive signal rather than a
    /// wait: the control is dispatched last down the same serial path, so the fold at the moment it
    /// appears is the complete set of answers.
    /// </summary>
    private IObservable<ImmutableList<string>> AnsweredSoFar =>
        nacks.Scan(ImmutableList<string>.Empty, (answered, id) => answered.Add(id));

    private OrleansRoutingService CreateRouter() =>
        // grainFactory: null — the shutdown branch never reaches placement (see the shutdown test).
        new(null!, ServiceProvider, ServiceProvider.GetRequiredService<ILogger<OrleansRoutingService>>());

    /// <summary>
    /// Packages exactly the way <c>MeshBuilder</c> does before calling
    /// <c>IRoutingService.DeliverMessage</c> — this is the boundary under test.
    /// </summary>
    private IMessageDelivery Packaged<TMessage>(TMessage message)
    {
        var packaged = new MessageDelivery<TMessage>(SenderAddress, TargetAddress, message,
                Hub.JsonSerializerOptions)
            .Package(Hub.JsonSerializerOptions);
        // Fail LOUD if packaging did not erase the type: every assertion below would otherwise pass
        // for the wrong reason (a serialization error leaves the typed payload in place).
        packaged.Message.Should().BeOfType<RawJson>(
            "MeshBuilder packages every delivery before the router sees it — that erasure IS the defect");
        return packaged;
    }

    private IMessageDelivery PackagedInnerFailure() => Packaged(new DeliveryFailure(
        new MessageDelivery<string>(SenderAddress, TargetAddress, "inner", Hub.JsonSerializerOptions),
        "inner failure"));

    /// <summary>
    /// The mechanism, stated as an assertion so it cannot silently stop being true: after packaging
    /// the CLR-type guards the routing layer was written with are unreachable. Nothing in the fix
    /// changes this — the payload really is gone; what changes is that the routers no longer ask
    /// the envelope's CLR type.
    /// </summary>
    [Fact]
    public void Packaging_ErasesTheGuardsInput()
    {
        var heartBeat = Packaged(new HeartBeatEvent());
        var failure = PackagedInnerFailure();

        (heartBeat.Message is DeliveryFailure).Should().BeFalse();
        heartBeat.Message.GetType().HasAttribute<CanBeIgnoredAttribute>().Should().BeFalse(
            "RawJson carries no [CanBeIgnored] — the attribute belonged to the payload that was erased");
        (failure.Message is DeliveryFailure).Should().BeFalse(
            "a packaged DeliveryFailure is a RawJson, so `is DeliveryFailure` can never match on the routed path");
    }

    /// <summary>
    /// The regression. A fire-and-forget <see cref="HeartBeatEvent"/> and a
    /// <see cref="DeliveryFailure"/> must NOT be answered, while ordinary traffic still is.
    ///
    /// <para>Order is the whole determinism story: all three go through the same synchronous
    /// shutdown branch and land on the same single-threaded hub, and the control goes LAST — so the
    /// fold at the moment the control is answered already contains any answer the two before it
    /// produced. Nothing waits for time to pass; the assertion settles on a real emission.</para>
    /// </summary>
    [Fact]
    public async Task PackagedFireAndForgetAndNacks_AreNotAnswered_WhileOrdinaryTrafficIs()
    {
        var routing = CreateRouter();
        lifetime.StopApplication();

        var heartBeat = Packaged(new HeartBeatEvent());
        var failure = PackagedInnerFailure();
        var control = Packaged("ordinary-payload");

        // Cold observables: the subscribe IS the dispatch, and it runs inline here, so the three
        // reach the hub in this order.
        routing.DeliverMessage(heartBeat).Subscribe(_ => { });
        routing.DeliverMessage(failure).Subscribe(_ => { });
        routing.DeliverMessage(control).Subscribe(_ => { });

        var answered = await AnsweredSoFar.Should().Within(10.Seconds())
            .Match(a => a.Contains(control.Id),
                "ordinary traffic must still be answered — the fix must not silence real failures");

        answered.Should().ContainSingle(
            "a [CanBeIgnored] control message has nobody awaiting it — for a permanently-gone owner "
            + "heart-beaten every interval, answering it IS the NotFound storm; and answering a "
            + "DeliveryFailure with a DeliveryFailure loops").Subject
            .Should().Be(control.Id);
        answered.Should().NotContain(heartBeat.Id);
        answered.Should().NotContain(failure.Id);
    }

    /// <summary>
    /// The delivery the router hands back must agree with what it actually did: a suppressed
    /// delivery was NOT answered, so it must not claim <c>SenderWasNacked</c> — that flag is what
    /// stops <c>MessageService</c> reporting the failure, and claiming it without posting converts
    /// a routing failure into a silent one.
    /// </summary>
    [Fact]
    public async Task SuppressedDelivery_DoesNotClaimTheSenderWasNacked()
    {
        var routing = CreateRouter();
        lifetime.StopApplication();

        var routed = new ReplaySubject<IMessageDelivery>();
        routing.DeliverMessage(Packaged(new HeartBeatEvent())).Subscribe(routed.OnNext);

        var result = await routed.Should().Within(10.Seconds())
            .Match(d => d.State == MessageDeliveryState.Failed);

        result.SenderWasNacked.Should().BeFalse(
            "nothing was posted, so the sender has NOT been answered — claiming otherwise suppresses "
            + "the only remaining report of the failure");
    }
}
