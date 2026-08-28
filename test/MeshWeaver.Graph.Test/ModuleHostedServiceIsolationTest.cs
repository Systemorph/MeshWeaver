using System;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A hosted service a MODULE contributed must not be able to kill the portal (#2449).
///
/// <para>🚨 Per-module isolation (#2234) protects INSTALLATION, not ACTIVATION. Registering an
/// <c>IHostedService</c> only leaves a descriptor; its constructor runs later, when the generic
/// host resolves <c>IHostedService[]</c> — outside the install path entirely, and all-or-nothing.
/// On memex-cloud (2026-08-26) one landed module built against a newer core registered a service
/// whose constructor could not be satisfied; every replacement pod aborted at boot with SIGABRT and
/// the rollout wedged while the old ReplicaSet kept serving. A binary skew between image and landed
/// modules is EXPECTED transiently — that is the entire reason isolation exists — so the blast
/// radius has to be one feature, not the process.</para>
/// </summary>
public class ModuleHostedServiceIsolationTest
{
    /// <summary>A module service whose constructor can never be satisfied — the shape that aborted
    /// the host (`None of the constructors found … can be invoked with the available services`).</summary>
    private sealed class UnsatisfiableModuleService : IHostedService
    {
        public UnsatisfiableModuleService(IIsNotRegistered missing) => _ = missing;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    public interface IIsNotRegistered;

    private sealed class ThrowsOnStart : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_module_service_that_cannot_be_ACTIVATED_does_not_fail_startup()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        // CONTROL: the unwrapped activation really does throw. Without this the test could pass
        // over a scenario that never reproduced the defect at all.
        Record.Exception(() => ActivatorUtilities.CreateInstance(sp, typeof(UnsatisfiableModuleService)))
            .Should().NotBeNull(
                "the premise of this test is that this activation fails — if it stopped failing, "
                + "the isolation below would be proving nothing");

        var isolated = new IsolatedModuleHostedService(
            "SomeModule",
            p => ActivatorUtilities.CreateInstance(p, typeof(UnsatisfiableModuleService)),
            sp,
            logger: null);

        var thrown = await Record.ExceptionAsync(() => isolated.StartAsync(CancellationToken.None));

        thrown.Should().BeNull(
            "an unsatisfiable constructor in a module's hosted service is what aborted every "
            + "replacement pod — it must cost that module's contribution and nothing else");
    }

    /// <summary>
    /// The other half of the same boundary: a service that activates but throws while STARTING.
    /// Catching only the activation half would leave the host abortable by the other, which is the
    /// same outcome this issue is about.
    /// </summary>
    [Fact]
    public async Task A_module_service_that_throws_while_STARTING_does_not_fail_startup()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var isolated = new IsolatedModuleHostedService(
            "SomeModule", _ => new ThrowsOnStart(), sp, logger: null);

        (await Record.ExceptionAsync(() => isolated.StartAsync(CancellationToken.None)))
            .Should().BeNull("a module's start failure is that module's problem, not the portal's");
    }

    /// <summary>A module that never activated has nothing to stop, and must not make shutdown fail
    /// either — a teardown that throws here would turn a skipped feature into a dirty exit.</summary>
    [Fact]
    public async Task Stopping_a_service_that_never_activated_is_a_no_op()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var isolated = new IsolatedModuleHostedService(
            "SomeModule",
            p => ActivatorUtilities.CreateInstance(p, typeof(UnsatisfiableModuleService)),
            sp,
            logger: null);

        await isolated.StartAsync(CancellationToken.None);
        (await Record.ExceptionAsync(() => isolated.StopAsync(CancellationToken.None)))
            .Should().BeNull();
    }

    /// <summary>The healthy path is untouched: a service that activates and starts still runs.</summary>
    [Fact]
    public async Task A_healthy_module_service_still_starts()
    {
        var started = false;
        var sp = new ServiceCollection().BuildServiceProvider();
        var isolated = new IsolatedModuleHostedService(
            "SomeModule", _ => new Healthy(() => started = true), sp, logger: null);

        await isolated.StartAsync(CancellationToken.None);
        started.Should().BeTrue("isolation must not change what a working module does");
    }

    private sealed class Healthy(Action onStart) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) { onStart(); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
