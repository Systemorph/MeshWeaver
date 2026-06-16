using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Drains the mesh root hub during host shutdown — BEFORE the host disposes the root
/// <see cref="IServiceProvider"/> (which IS the hub's Autofac container, via
/// <c>MessageHubServiceProviderFactory</c>).
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
/// <para>An <see cref="IHostedService.StopAsync"/> runs for every hosted service BEFORE the root
/// provider is disposed, and it may legitimately <c>await</c> — so the drain happens at exactly the
/// right point with no action-block deadlock. Registered FIRST (in the
/// <c>MeshHostApplicationBuilder</c> ctor) so it stops LAST: the mesh drains only after the
/// dependent hosted services (PG subscriptions, scheduled jobs) have stopped feeding it.</para>
/// </summary>
public sealed class MeshTeardownHostedService(
    IServiceProvider services,
    ILogger<MeshTeardownHostedService> logger) : IHostedService
{
    /// <summary>Bounded drain budget — a stuck action block or leaked I/O slot completes the wait
    /// rather than hanging shutdown; the underlying defect surfaces via the hub's own diagnostics.</summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(30);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Resolve the mesh root hub lazily here (the scope is still alive during StopAsync).
        var mesh = services.GetService<IMessageHub>();
        if (mesh is null)
            return;

        logger.LogInformation(
            "MeshTeardownHostedService: draining mesh {Address} before the host disposes its ServiceProvider",
            mesh.Address);
        try
        {
            // TeardownAsync captures the mesh-scoped IoPoolRegistry + AsyncDisposeQueue while the
            // scope is still alive, disposes the hub, then awaits all three drain phases.
            await mesh.TeardownAsync(TeardownTimeout);
            logger.LogInformation("MeshTeardownHostedService: mesh {Address} drained cleanly", mesh.Address);
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
