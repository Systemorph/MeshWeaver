using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the second half of the shutdown-ordering contract: the mesh root hub must stay ALIVE
/// for every other hosted service's <c>StopAsync</c>, and drain only afterwards.
///
/// <para><b>The defect this pins.</b> <see cref="MeshTeardownHostedService"/> used to drain in
/// <c>StopAsync</c>, relying on "registered first ⇒ stops last". That reasoning holds only
/// against services registered LATER. It does not hold on an ASP.NET Core host, where
/// <c>GenericWebHostService</c> is registered by <c>WebApplication.CreateBuilder</c> — strictly
/// before <see cref="MeshHostApplicationBuilder"/>'s ctor can register anything — and therefore
/// stops LAST. The portal consequently tore the mesh down while Kestrel was still serving:
/// late HTTP requests and live Blazor circuits re-entered the disposed root hub's delivery
/// pipeline, re-created hosted hubs, and re-subscribed Orleans streams on a stopped silo. When
/// the host then disposed the root container, all of it threw <c>ObjectDisposedException</c>
/// from a disposed Autofac <c>LifetimeScope</c> (#1540/#1541/#1542/#1544/#1545/#1546/#1547/
/// #1548/#1560 — one root, eight filings).</para>
///
/// <para><see cref="EarlyRegisteredProbe"/> stands in for the web host: it is registered BEFORE
/// the mesh builder, so it stops LAST, exactly like Kestrel. RED before the fix — it observed a
/// hub at <c>RunLevel.Dead</c>.</para>
/// </summary>
public class MeshTeardownRunsAfterEveryOtherHostedServiceTest
{
    [Fact(Timeout = 60000)]
    public async Task AHostedServiceThatStopsLast_StillSeesALiveMesh()
    {
        var hostBuilder = new HostBuilder();

        // Registered BEFORE the mesh builder — so it stops LAST. This is the position
        // GenericWebHostService occupies on every portal host.
        hostBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<EarlyRegisteredProbe>();
            services.AddHostedService(sp => sp.GetRequiredService<EarlyRegisteredProbe>());
        });

        _ = new MeshHostBuilder(hostBuilder, new Address("mesh", "teardown-after-all"));
        using var host = hostBuilder.Build();
        await host.StartAsync();

        var mesh = host.Services.GetRequiredService<IMessageHub>();
        mesh.IsDisposing.Should().BeFalse("the mesh must be alive while the host runs");

        await host.StopAsync();

        var probe = host.Services.GetRequiredService<EarlyRegisteredProbe>();

        probe.StopAsyncRan.Should().BeTrue("the probe's StopAsync must have run during shutdown");
        probe.MeshWasDisposingAtStop.Should().BeFalse(
            "a hosted service that stops LAST — Kestrel, and with it every in-flight HTTP request "
            + "and Blazor circuit — must still find a LIVE mesh; draining before it is what let "
            + "late traffic re-enter a disposed hub and resolve from a dying Autofac scope");
        probe.MeshRunLevelAtStop.Should().Be(MessageHubRunLevel.Started,
            "the mesh drain belongs in IHostedLifecycleService.StoppedAsync, which runs only after "
            + "EVERY hosted service's StopAsync has returned");

        // …and the original contract still holds: fully drained before the caller disposes the host.
        mesh.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "the drain must still be COMPLETE when StopAsync returns — the container disposal follows");
    }

    /// <summary>
    /// Stands in for <c>GenericWebHostService</c>: registered before the mesh, therefore stopped
    /// after it. Records what it saw of the mesh at that moment. Registered as a singleton in its
    /// own right so the test can read the recording back off the container.
    /// </summary>
    private sealed class EarlyRegisteredProbe(IServiceProvider services) : IHostedService
    {
        public bool StopAsyncRan { get; private set; }
        public bool MeshWasDisposingAtStop { get; private set; }
        public MessageHubRunLevel MeshRunLevelAtStop { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            var mesh = services.GetRequiredService<IMessageHub>();
            StopAsyncRan = true;
            MeshWasDisposingAtStop = mesh.IsDisposing;
            MeshRunLevelAtStop = mesh.RunLevel;
            return Task.CompletedTask;
        }
    }
}
