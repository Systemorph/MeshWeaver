using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Drains the mesh root hub during host shutdown — AFTER every other hosted service has
/// stopped feeding it, and BEFORE the host disposes the root <see cref="IServiceProvider"/>
/// (which IS the hub's Autofac container, via <c>MessageHubServiceProviderFactory</c>).
///
/// <para>Disposing a hub is reactive and returns immediately; the action blocks, offloaded
/// <c>IIoPool</c> work, and the <c>AsyncDisposeQueue</c> drain asynchronously afterwards (see
/// <see cref="MeshTeardownExtensions"/>). If the host tears the scope down while any of that is
/// still in flight, a late continuation resolves a service from the already-disposed scope and
/// throws an unobserved <see cref="ObjectDisposedException"/> ("LifetimeScope … has already been
/// disposed") — the "catastrophic" teardown class. <c>MonolithMeshTestBase</c>-style tests
/// already do this drain between <c>[Fact]</c>s; this hosted service brings the SAME ordered drain
/// to the production hosts (Monolith + Orleans-distributed).</para>
///
/// <para>🚨 <b>The drain runs in <see cref="StoppedAsync"/>, never in
/// <see cref="StopAsync"/> — and that is the whole point of this type.</b> Hosted services stop
/// in REVERSE registration order, so "register first ⇒ stop last" only holds against services
/// registered later. It does <b>not</b> hold in an ASP.NET Core host: <c>GenericWebHostService</c>
/// is registered by <c>WebApplication.CreateBuilder</c>, i.e. strictly BEFORE any
/// <see cref="MeshHostApplicationBuilder"/> can register anything, so Kestrel — and with it every
/// live HTTP request and Blazor circuit — stops <i>after</i> this service. Draining in
/// <c>StopAsync</c> therefore tore the mesh down while the portal was still serving: late requests
/// re-entered the disposed root hub's delivery pipeline, re-created hosted hubs
/// (<c>Admin/PlatformVersion</c>), and re-subscribed Orleans streams on an already-stopped silo.
/// Once the host then disposed the container, every one of those in-flight paths threw
/// <see cref="ObjectDisposedException"/> from a disposed <c>LifetimeScope</c> — issues #1540,
/// #1541, #1542, #1544, #1545, #1546, #1547, #1548 and #1560, all one root.</para>
///
/// <para><see cref="IHostedLifecycleService.StoppedAsync"/> is invoked only after EVERY
/// <see cref="IHostedService.StopAsync"/> has returned — the web host's included — and still
/// strictly before the host disposes the root provider. That is the exact window this drain
/// needs, and unlike registration position it cannot be perturbed by what a consumer registers.
/// Pinned by <c>MeshHostBuilderTeardownOrderingTest</c> and
/// <c>MeshTeardownRunsAfterEveryOtherHostedServiceTest</c>.</para>
/// </summary>
public sealed class MeshTeardownHostedService(
    IServiceProvider services,
    ILogger<MeshTeardownHostedService> logger) : IHostedLifecycleService
{
    /// <summary>Bounded drain budget — a stuck action block or leaked I/O slot completes the wait
    /// rather than hanging shutdown; the underlying defect surfaces via the hub's own diagnostics.</summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Deliberately a no-op: the mesh must stay alive for every other hosted service's
    /// <c>StopAsync</c> — most importantly the web host's, which is what stops Kestrel and the
    /// Blazor circuits. The drain happens in <see cref="StoppedAsync"/>. See the type remarks.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        // Resolve the mesh root hub lazily here (the scope is still alive during StoppedAsync).
        var mesh = services.GetService<IMessageHub>();
        if (mesh is null)
            return;

        logger.LogInformation(
            "MeshTeardownHostedService: draining mesh {Address} before the host disposes its ServiceProvider",
            mesh.Address);
        try
        {
            // TeardownAsync captures the mesh-scoped IoPoolRegistry + AsyncDisposeQueue while the
            // scope is still alive, disposes the hub, awaits all drain phases, and fires the
            // MeshTeardownSignal with the terminal report — the "all is done" notification.
            var report = await mesh.TeardownAsync(TeardownTimeout);
            if (report.Clean)
                logger.LogInformation("MeshTeardownHostedService: mesh {Address} drained cleanly", mesh.Address);
            else
                // A dirty report means live work survives into scope disposal — the
                // use-after-unload class. The host still exits (shutdown must not hang), but
                // this is an ERROR: the leaked leaf is a real defect in whatever ignored its
                // cancellation token, and this line is the only attribution the crash will get.
                logger.LogError(
                    "MeshTeardownHostedService: mesh {Address} teardown DIRTY — {Report}. " +
                    "A pooled I/O leaf or async cleanup ignored cancellation and is still running; " +
                    "the scope is about to dispose over it.",
                    mesh.Address, report);
        }
        catch (Exception ex)
        {
            // Never let a teardown drain failure escape into host shutdown — log and continue so the
            // host still exits. A genuine leak surfaces via AnyHubQuiescingTimedOut / IoPool in-flight.
            logger.LogWarning(ex,
                "MeshTeardownHostedService: mesh drain did not complete cleanly within {Timeout}", TeardownTimeout);
        }
    }
}
