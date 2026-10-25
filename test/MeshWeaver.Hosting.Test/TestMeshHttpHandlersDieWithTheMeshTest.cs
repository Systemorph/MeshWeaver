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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAClientAndDisposeTheContainer(bool scopeHandlersToTheMesh)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(ClientName);
        if (scopeHandlersToTheMesh)
            services.WithHandlersScopedToTheMesh();
        var provider = services.BuildServiceProvider();

        // The handler, and with it the factory's tracking entry, is created here. That is what
        // DeploymentReportService does in its constructor for every mesh.
        using (var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName))
            client.Should().NotBeNull();

        provider.Dispose();
        return new WeakReference(provider);
    }

    private static void FullCollection()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
