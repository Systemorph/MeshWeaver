using System;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Deterministic repro for the Autofac "LifetimeScope … has already been disposed"
/// CATASTROPHICS during Orleans TestCluster / silo teardown (task #18, defect 2 — the
/// second straggler adjacent to #228).
///
/// <para><b>The disposal-ordering defect.</b> A host built through the legacy
/// <see cref="MeshHostBuilder"/> (the <c>IHostBuilder</c> path — used by
/// <c>UseOrleansMeshServer(this IHostBuilder)</c> / <c>UseOrleansMeshClient</c>, i.e. every
/// Orleans TestCluster silo and client, and any prod silo on the legacy builder) had NO
/// ordered mesh drain on shutdown: <c>host.StopAsync()</c> stopped the hosted services and
/// then <c>host.Dispose()</c> disposed the root <c>IServiceProvider</c> — which IS the mesh
/// root hub's Autofac container (<see cref="MessageHubServiceProviderFactory"/>) — while the
/// hub's action blocks, offloaded <c>IIoPool</c> work and the <c>AsyncDisposeQueue</c> were
/// still draining. A late continuation then resolves a service (or begins a nested hub
/// scope) from the disposed container → <see cref="ObjectDisposedException"/>
/// ("Instances cannot be resolved and nested lifetimes cannot be created from this
/// LifetimeScope …") on a pooled task nobody observes → xUnit v3 escalates the
/// UnobservedTaskException to a "Catastrophic failure" (the pre-existing FATAL in CI run
/// 28646145008 shard 2 that <c>OrleansShutdownRaceSuppressor.SetObserved()</c> cannot
/// beat xUnit's reporter to).</para>
///
/// <para><b>The contract under test</b> — dispose the USERS before the SCOPE: by the time
/// <c>StopAsync</c> returns (i.e. BEFORE the host disposes the container), the mesh root
/// hub must be fully drained. <see cref="MeshHostApplicationBuilder"/> (the
/// <c>IHostApplicationBuilder</c> path) has carried exactly this guarantee via
/// <see cref="MeshTeardownHostedService"/> since it was introduced; this test pins the
/// SAME guarantee onto <see cref="MeshHostBuilder"/>. RED before the fix: the hub is
/// still fully alive (<c>RunLevel == Started</c>) when <c>StopAsync</c> returns.</para>
/// </summary>
public class MeshHostBuilderTeardownOrderingTest
{
    [Fact(Timeout = 60000)]
    public async Task StopAsync_DrainsTheMeshRootHub_BeforeTheHostDisposesItsAutofacScope()
    {
        var hostBuilder = new HostBuilder();
        _ = new MeshHostBuilder(hostBuilder, new Address("mesh", "teardown-ordering"));
        using var host = hostBuilder.Build();
        await host.StartAsync();

        var mesh = host.Services.GetRequiredService<IMessageHub>();
        mesh.IsDisposing.Should().BeFalse("the mesh must be alive while the host runs");

        await host.StopAsync();

        // The ordering contract: StopAsync (which runs every IHostedService.StopAsync,
        // including the mesh drain) must have FULLY drained the mesh root hub before the
        // caller goes on to dispose the host — because host.Dispose() tears down the
        // Autofac container the hub's continuations resolve from. A hub still running
        // here is the armed LifetimeScope catastrophic.
        mesh.IsDisposing.Should().BeTrue(
            "host shutdown must dispose the mesh root hub BEFORE the Autofac scope goes away — "
            + "otherwise late continuations resolve from a disposed LifetimeScope (the CI catastrophic)");
        mesh.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "the drain must be COMPLETE (action blocks + IoPool + AsyncDisposeQueue) when StopAsync returns, "
            + "not merely started — the container disposal follows immediately");
    }
}
