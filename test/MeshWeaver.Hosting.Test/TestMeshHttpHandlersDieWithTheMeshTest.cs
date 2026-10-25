using System;
using System.Net.Http;
using System.Runtime.CompilerServices;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A disposed container that created an HTTP client must be collectable. The default
/// <c>IHttpClientFactory</c> configuration does not allow that. Its handler-expiry timer sits in the
/// process-wide timer queue, and through the handler's tracking entry it holds the container for
/// the whole two-minute handler lifetime. A test host creates and disposes a mesh for every test,
/// so that held about 80 disposed meshes at once in <c>Memex.Portal.Shared.Test</c> and took it
/// past the 6 GiB memory watchdog. See <see cref="MeshScopedHttpHandlers"/>.
///
/// <para>Two facts. The first is the negative control: it shows that this measurement CAN see the
/// retention, using exactly the configuration every test mesh had before. The second shows that
/// the configuration <see cref="MonolithMeshTestBase"/> now applies leaves nothing behind.</para>
/// </summary>
public class TestMeshHttpHandlersDieWithTheMeshTest
{
    private const string ClientName = "retention-probe";

    [Fact]
    public void WithTheDefaultHandlerLifetime_TheExpiryTimerKeepsTheDisposedContainerAlive()
    {
        var container = CreateAClientAndDisposeTheContainer(scopeHandlersToTheMesh: false);

        FullCollection();

        container.IsAlive.Should().BeTrue(
            "this is the control. The factory's two-minute expiry timer holds the tracking entry, "
            + "and through it the container. If this reads false, the measurement below cannot "
            + "see the retention and its pass means nothing");
    }

    [Fact]
    public void WithHandlersScopedToTheMesh_TheDisposedContainerIsCollected()
    {
        var container = CreateAClientAndDisposeTheContainer(scopeHandlersToTheMesh: true);

        FullCollection();

        container.IsAlive.Should().BeFalse(
            "with an infinite handler lifetime the factory starts no expiry timer, so nothing "
            + "outside the disposed container refers to it");
    }

    [Fact]
    public void WithHandlersScopedToTheMesh_TheContainerDisposesThePipelineAndItsScope()
    {
        var log = new DisposalLog();
        var container = CreateAClientAndDisposeTheContainer(scopeHandlersToTheMesh: true, log);

        log.HandlerDisposed.Should().BeTrue(
            "an infinite lifetime removes the factory's expiry timer, which was its only route to "
            + "disposing a handler pipeline, so the mesh must dispose the pipeline itself");
        log.ScopedServiceDisposed.Should().BeTrue(
            "the handler scope's services were disposed only by the factory's cleanup after expiry, "
            + "so the mesh must dispose the handler scope itself");

        FullCollection();

        container.IsAlive.Should().BeFalse(
            "owning the pipeline and its scope must not root the container from outside it");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAClientAndDisposeTheContainer(bool scopeHandlersToTheMesh, DisposalLog? log = null)
    {
        var services = new ServiceCollection();
        var client = services.AddHttpClient(ClientName);
        if (log is not null)
        {
            // A handler that resolves a scoped disposable service: the shape whose disposal only
            // the factory's expiry cleanup ever performed.
            services.AddSingleton(log);
            services.AddScoped<ScopedProbe>();
            client.AddHttpMessageHandler(sp => new ProbeHandler(sp.GetRequiredService<ScopedProbe>(), log));
        }
        if (scopeHandlersToTheMesh)
            services.WithHandlersScopedToTheMesh();
        var provider = services.BuildServiceProvider();

        // The handler, and with it the factory's tracking entry, is created here. That is what
        // DeploymentReportService does in its constructor for every mesh.
        using (var created = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName))
            created.Should().NotBeNull();

        provider.Dispose();
        return new WeakReference(provider);
    }

    private static void FullCollection()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private sealed class DisposalLog
    {
        public bool HandlerDisposed { get; set; }
        public bool ScopedServiceDisposed { get; set; }
    }

    private sealed class ScopedProbe(DisposalLog log) : IDisposable
    {
        public void Dispose() => log.ScopedServiceDisposed = true;
    }

    private sealed class ProbeHandler(ScopedProbe probe, DisposalLog log) : DelegatingHandler
    {
        public ScopedProbe Probe { get; } = probe;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                log.HandlerDisposed = true;
            base.Dispose(disposing);
        }
    }
}
