using System;
using System.Linq;
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
    private interface IFillerA;
    private interface IFillerB;
    private interface IFillerC;
    private sealed class Filler : IFillerA, IFillerB, IFillerC;

    /// <summary>A hosted service the PLATFORM registered. Isolation must never reach it: a platform
    /// service failing to start is fatal on purpose, and downgrading that to a logged skip is the
    /// one outcome this whole mechanism must not be able to produce.</summary>
    private sealed class PlatformService : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// 🚨 The scope is decided by descriptor IDENTITY, never by an index snapshot.
    ///
    /// <para>An index mark ("everything past position N is this module's") silently assumes the
    /// configuration only APPENDS. A configuration that INSERTS ahead of the mark shifts every
    /// later descriptor forward, and the mark then points into descriptors that were already
    /// there — so the loop wraps a PLATFORM hosted service and converts its fatal startup failure
    /// into a logged skip. Nothing about that outcome is visible: the portal comes up "healthy"
    /// with a platform service silently absent, which is strictly worse than the crash this
    /// isolation exists to prevent.</para>
    ///
    /// <para>Both halves are asserted because they fail in opposite directions — the platform's
    /// must stay untouched, and the module's must still get wrapped.</para>
    /// </summary>
    [Fact]
    public void Scoping_survives_a_module_configuration_that_INSERTS_ahead_of_the_mark()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFillerA, Filler>();
        services.AddSingleton<IHostedService, PlatformService>();   // platform — must stay fatal

        var platformDescriptor = services.Single(d => d.ServiceType == typeof(IHostedService));

        var result = MeshBuilder.IsolateModuleHostedServices(
            new MeshNode("SomeModule"),
            s =>
            {
                // The shape an index snapshot cannot survive: two inserts AHEAD of everything,
                // pushing the platform's descriptor past where the mark was taken.
                s.Insert(0, ServiceDescriptor.Singleton<IFillerB, Filler>());
                s.Insert(0, ServiceDescriptor.Singleton<IFillerC, Filler>());
                s.AddSingleton<IHostedService, ThrowsOnStart>();
                return s;
            },
            services);

        var hosted = result.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        hosted.Should().HaveCount(2);

        hosted.Should().Contain(
            d => ReferenceEquals(d, platformDescriptor),
            "the PLATFORM's hosted service must come out of this untouched — wrapping it would "
            + "turn a deliberately fatal platform failure into a silent skip");

        var moduleDescriptor = hosted.Single(d => !ReferenceEquals(d, platformDescriptor));
        moduleDescriptor.ImplementationFactory.Should().NotBeNull(
            "the MODULE's hosted service must still be wrapped — an index snapshot that gave up "
            + "here would leave exactly the unprotected activation that aborted every pod");
        moduleDescriptor.ImplementationType.Should().BeNull(
            "a wrapped registration is produced through the isolating factory, not activated "
            + "directly by the host");
    }

}
