using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the host behaviour every teardown in this repo is built on top of, and which the
/// shutdown-race family (#1573, #715, #967, #1330, #1334) had only ever exercised on the ORDERLY
/// path: <b>when host STARTUP is aborted, the ordered shutdown does not run at all.</b>
///
/// <para><c>Host.StartAsync</c> throws on the first hosted service that faults, and
/// <c>HostingAbstractionsHostExtensions.RunAsync</c>'s <c>finally</c> then goes straight to
/// <c>host.Dispose()</c> — so no <c>IHostedService.StopAsync</c> runs, no
/// <c>IHostedLifecycleService.StoppedAsync</c> runs, and the root <see cref="IServiceProvider"/>
/// is disposed while services that DID start are still winding down. That is why
/// <c>MeshTeardownHostedService</c>'s window ("the scope is alive during StoppedAsync") is not
/// available to a silo lifecycle observer, and why <c>IoPoolSiloTeardown</c> must capture what its
/// stop needs at START rather than resolve it at stop (#1898 / #1899, triggered by exactly this
/// path in #1897).</para>
///
/// <para>This is a REGRESSION pin, not framework documentation: if a future .NET starts unwinding
/// already-started services on a failed start, this goes red and the reasoning above should be
/// re-read before anything relies on the ordered shutdown again.</para>
/// </summary>
public class AbortedStartupSkipsOrderedShutdownTest
{
    /// <summary>Records whether the ordered shutdown ever reached it.</summary>
    private sealed class Probe : IHostedService
    {
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Stands in for a startup gate whose query is cancelled by a rollout — the #1897
    /// shape. It honours the <see cref="IHostedService"/> cancellation contract and rethrows.</summary>
    private sealed class CancelledGate : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
            => throw new OperationCanceledException("startup aborted by shutdown");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact(Timeout = 30000)]
    public async Task AnAbortedStartup_DisposesTheContainer_WithoutRunningAnyStopAsync()
    {
        var probe = new Probe();
        var hostBuilder = new HostBuilder();
        hostBuilder.ConfigureServices(services =>
        {
            // Registered FIRST, so it starts first and would stop last on the orderly path.
            services.AddSingleton<IHostedService>(probe);
            services.AddSingleton<IHostedService>(new CancelledGate());
        });

        var host = hostBuilder.Build();
        var services = host.Services;

        var thrown = await Record.ExceptionAsync(() => host.StartAsync(TestContext.Current.CancellationToken));

        thrown.Should().NotBeNull("the gate propagates the cancellation, which aborts host startup");
        probe.Started.Should().BeTrue(
            "precondition: the service registered before the gate DID start — it is the one with "
            + "live work that a shutdown would have to unwind");

        // Exactly what HostingAbstractionsHostExtensions.RunAsync's finally does when StartAsync throws.
        host.Dispose();

        probe.Stopped.Should().BeFalse(
            "🚨 an aborted startup skips the ordered shutdown entirely — so teardown work must never "
            + "assume it runs inside one, and must not resolve anything from DI once it does run");

        Action resolve = () => services.GetService(typeof(Probe));
        resolve.Should().Throw<ObjectDisposedException>(
            "the container is gone by the time the already-started services are winding down — "
            + "this is the disposed scope IoPoolSiloTeardown used to resolve from (#1898)");
    }
}
