using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// </summary>
public class OrleansRouterAnswerOnceAfterPackagingTest : TestBase
{
    private static readonly Address SenderAddress = new("portal", "answer-once-sender");
    private static readonly Address TargetAddress = new("SomeNamespace", "SomeNode");

    /// <summary>Delivery ids the router answered with a <see cref="DeliveryFailure"/>.</summary>
    private readonly ConcurrentQueue<string> nackedDeliveryIds = new();

    /// <summary>
    /// Completes when the POSITIVE CONTROL has been answered. It is dispatched last through the
    /// same serial path, so its NACK arriving is the proof that the two suppressed deliveries were
    /// fully processed — no sleep, no "wait and hope".
    /// </summary>
    private readonly TaskCompletionSource controlAnswered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string controlDeliveryId = string.Empty;

    private readonly IHostApplicationLifetime lifetime;

    public OrleansRouterAnswerOnceAfterPackagingTest(ITestOutputHelper output) : base(output)
    {
        lifetime = new HostBuilder().Build().Services.GetRequiredService<IHostApplicationLifetime>();
        Services.AddSingleton(lifetime);
        Services.AddSingleton<AccessService>();
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(SenderAddress, conf => conf
            .WithHandler<DeliveryFailure>((_, d) =>
            {
                nackedDeliveryIds.Enqueue(d.Message.Delivery.Id);
                if (d.Message.Delivery.Id == controlDeliveryId)
                    controlAnswered.TrySetResult();
                return d.Processed();
            })
            .WithPostingIdentity(PostingIdentity.System)));
    }

    private IMessageHub Hub => ServiceProvider.GetRequiredService<IMessageHub>();

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
        var failure = Packaged(new DeliveryFailure(
            new MessageDelivery<string>(SenderAddress, TargetAddress, "inner", Hub.JsonSerializerOptions),
            "inner failure"));

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
    /// <para>Order matters and is the whole determinism story: the control is dispatched LAST
    /// through the same synchronous branch and the same single-threaded hub, so once its NACK has
    /// been handled any NACK for the two before it would already be in the queue.</para>
    /// </summary>
    [Fact]
    public async Task PackagedFireAndForgetAndNacks_AreNotAnswered_WhileOrdinaryTrafficIs()
    {
        var routing = CreateRouter();
        lifetime.StopApplication();

        var heartBeat = Packaged(new HeartBeatEvent());
        var failure = Packaged(new DeliveryFailure(
            new MessageDelivery<string>(SenderAddress, TargetAddress, "inner", Hub.JsonSerializerOptions),
            "inner failure"));
        var control = Packaged("ordinary-payload");
        controlDeliveryId = control.Id;

        await routing.DeliverMessage(heartBeat).FirstAsync().ToTask();
        await routing.DeliverMessage(failure).FirstAsync().ToTask();
        await routing.DeliverMessage(control).FirstAsync().ToTask();

        await controlAnswered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var answered = nackedDeliveryIds.Should().ContainSingle(
            "a [CanBeIgnored] control message has nobody awaiting it — for a permanently-gone owner "
            + "heart-beaten every interval, answering it IS the NotFound storm; and answering a "
            + "DeliveryFailure with a DeliveryFailure loops. Ordinary traffic must still be answered.").Subject;
        answered.Should().Be(control.Id);
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

        var result = await routing.DeliverMessage(Packaged(new HeartBeatEvent())).FirstAsync().ToTask();

        result.State.Should().Be(MessageDeliveryState.Failed);
        result.SenderWasNacked.Should().BeFalse(
            "nothing was posted, so the sender has NOT been answered — claiming otherwise suppresses "
            + "the only remaining report of the failure");
    }
}
