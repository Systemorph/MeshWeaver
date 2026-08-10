using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the prod 2026-08-10 memex pod-shutdown burst (issue #1107).
///
/// <para><b>Trigger.</b> A silo that has begun graceful shutdown has already left the ACTIVE
/// silo set. <c>IRoutingGrain</c> is <c>[StatelessWorker(1)]</c>, so it is placed through
/// <c>StatelessWorkerDirector</c> → <c>PlacementService.GetCompatibleSilos</c>, which
/// intersects candidates with the active silos — from that instant every placement throws
/// <c>OrleansException: No active nodes are compatible with grain routing</c>, while the mesh
/// hubs are still alive and still routing ordinary live traffic at it. Loki put the first such
/// exception 409 ms after the host logged "Application is shutting down..." (11:44:31.341 →
/// 11:44:31.750), and the bursts reached 944 occurrences on a single dying pod while a healthy
/// pod logged ZERO in its entire 2 h life.</para>
///
/// <para><b>Invariant.</b> Once the host is stopping the router does not attempt a placement it
/// knows cannot succeed, and the sender is answered with the TRANSIENT
/// <see cref="ErrorType.ShuttingDown"/> — never the terminal <see cref="ErrorType.Failed"/>.
/// The classification is the functional half: <c>SynchronizationStream</c>'s keep-alive +
/// resubscribe latch and <c>JsonSynchronizationStream</c> ride <c>ShuttingDown</c> out and tear
/// down on a terminal verdict (CI 30003419841).</para>
///
/// <para><b>No cluster, no mocks, no timing.</b> The stopping signal is a REAL
/// <see cref="IHostApplicationLifetime"/> from a real host, cancelled through the same
/// <see cref="IHostApplicationLifetime.StopApplication"/> the runtime calls on SIGTERM. The
/// receiving hub is a REAL <see cref="IMessageHub"/> sitting at the sender's address, so the
/// NACK asserted below is the actual <see cref="DeliveryFailure"/> the router posts.
/// <see cref="IGrainFactory"/> is deliberately <c>null</c> — it is the PROBE: reaching grain
/// placement is observable as the throw it raises, so "did not touch the grain" and "did touch
/// the grain" are both directly assertable.</para>
/// </summary>
public class OrleansRoutingShutdownClassificationTest : TestBase
{
    private static readonly Address SenderAddress = new("portal", "shutdown-test-sender");
    private static readonly Address TargetAddress = new("SomeNamespace", "SomeNode");

    private readonly TaskCompletionSource<DeliveryFailure> nack = new();
    private readonly IHostApplicationLifetime lifetime;

    public OrleansRoutingShutdownClassificationTest(ITestOutputHelper output) : base(output)
    {
        // A real host purely for its real ApplicationLifetime — the concrete type the runtime
        // cancels on shutdown. Nothing is started; StopApplication fires ApplicationStopping
        // regardless, and that token is the signal under test.
        lifetime = new HostBuilder().Build().Services.GetRequiredService<IHostApplicationLifetime>();
        Services.AddSingleton(lifetime);
        Services.AddSingleton<AccessService>();
        // The hub sits AT the sender address, so the DeliveryFailure the router posts back to
        // delivery.Sender lands on this handler — the real NACK, not a stand-in.
        Services.AddSingleton<IMessageHub>(sp => sp.CreateMessageHub(SenderAddress, conf => conf
            .WithHandler<DeliveryFailure>((_, d) => { nack.TrySetResult(d.Message); return d.Processed(); })
            .WithPostingIdentity(PostingIdentity.System)));
    }

    private OrleansRoutingService CreateRouter() =>
        // grainFactory: null is the probe — see the class remarks.
        new(null!, ServiceProvider, ServiceProvider.GetRequiredService<ILogger<OrleansRoutingService>>());

    private static IMessageDelivery NewDelivery() =>
        new MessageDelivery<string>(SenderAddress, TargetAddress, "payload", JsonSerializerOptions.Default);

    /// <summary>
    /// The regression itself. While the host is stopping the sender must be NACKed with the
    /// transient <see cref="ErrorType.ShuttingDown"/>; a terminal <see cref="ErrorType.Failed"/>
    /// is what tears down the sync streams' resubscribe latch.
    /// </summary>
    [Fact]
    public async Task HostStopping_SenderIsNackedAsShuttingDown_NotFailed()
    {
        var routing = CreateRouter();
        lifetime.StopApplication();

        await routing.DeliverMessage(NewDelivery()).FirstAsync().ToTask();

        var failure = await nack.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // A reject caused by THIS process going away is transient: consumers with recovery
        // machinery ride ShuttingDown out, and tear down on the terminal Failed the Orleans
        // router used to send.
        Assert.Equal(ErrorType.ShuttingDown, failure.ErrorType);
    }

    /// <summary>
    /// The router must not attempt a placement it knows cannot succeed: that attempt is what makes
    /// Orleans log its own <c>Orleans.Messaging[100071]</c> error per message, which is the bulk of
    /// the 894 lines that auto-filed the incident. With the null grain factory as the probe,
    /// "never reached placement" is observable as the absence of its throw.
    /// </summary>
    [Fact]
    public async Task HostStopping_DoesNotReachGrainPlacement()
    {
        var routing = CreateRouter();
        lifetime.StopApplication();

        var result = await routing.DeliverMessage(NewDelivery()).FirstAsync().ToTask();

        Assert.Equal(MessageDeliveryState.Failed, result.State);
        // This path posts its own DeliveryFailure, so it must declare that — otherwise whoever
        // finishes the delivery NACKs the sender a second time and a shutdown burst doubles.
        Assert.True(result.SenderWasNacked);
    }

    /// <summary>
    /// The control for the guard above: with the host RUNNING the very same delivery still goes to
    /// grain placement, so the short-circuit is scoped to the shutdown window and is not quietly
    /// swallowing ordinary routing. Reaching placement is observable as the null probe's throw.
    /// </summary>
    [Fact]
    public async Task HostRunning_StillReachesGrainPlacement()
    {
        var routing = CreateRouter();

        // With the host running the router must still place IRoutingGrain — the null factory is
        // the probe that proves placement was reached.
        await Assert.ThrowsAsync<NullReferenceException>(
            () => routing.DeliverMessage(NewDelivery()).FirstAsync().ToTask());
    }
}
