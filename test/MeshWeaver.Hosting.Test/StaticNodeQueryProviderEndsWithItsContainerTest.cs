using System;
using System.Linq;
using System.Runtime.CompilerServices;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#7373 — <see cref="StaticNodeQueryProvider"/> subscribed to the module registry's
/// <see cref="ModuleContexts.VersionChanged"/> and discarded the subscription. A subject holds every
/// observer until it unsubscribes, so the registry kept the provider — and, through its validators, a
/// hub (heap dump: <c>StaticNodeQueryProvider → Lazy&lt;INodeValidator&gt;[] → ServicePrincipalAdminGuard →
/// MessageHub</c>) — reachable for as long as the registry lived, after the provider's own mesh was gone.
///
/// <para>The provider is registered exactly as the mesh registers it — a singleton built by a factory in
/// a MeshWeaver container — and the registry is held ALIVE past the container on purpose: that is the
/// state in which the leak shows (a registry that outlives a mesh built over it). Only the provider's own
/// subscription is under test here; the registry is deliberately not disposed.</para>
/// </summary>
public class StaticNodeQueryProviderEndsWithItsContainerTest
{
    [Fact]
    public void DisposingTheContainer_EndsTheProvidersRegistrySubscription_SoTheRegistryNoLongerHoldsIt()
    {
        using var registry = new ModuleContexts();

        var provider = BuildAndDispose(registry);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        provider.IsAlive.Should().BeFalse(
            "once the container that built the provider has ended, nothing may keep the provider reachable — "
            + "the live registry's VersionChanged subject in particular, which held it through the discarded subscription");
        GC.KeepAlive(registry);
    }

    [Fact]
    public void TheContainer_DisposesTheProvider_WhichIsIdempotentAcrossBothRegistrations()
    {
        using var registry = new ModuleContexts();
        var root = Container(registry);
        var provider = root.GetRequiredService<StaticNodeQueryProvider>();
        root.GetServices<IMeshQueryProvider>().OfType<StaticNodeQueryProvider>().Single()
            .Should().BeSameAs(provider, "the IMeshQueryProvider registration forwards to the one provider");
        provider.IsDisposed.Should().BeFalse();

        ((IDisposable)root).Dispose();

        provider.IsDisposed.Should().BeTrue("the container built it, so the container ends it with the mesh");
        provider.Dispose(); // handed back by both registrations — a second dispose is a no-op
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BuildAndDispose(ModuleContexts registry)
    {
        var root = Container(registry);
        var provider = root.GetRequiredService<StaticNodeQueryProvider>();
        var weak = new WeakReference(provider);
        ((IDisposable)root).Dispose();
        return weak;
    }

    // The production shape (PersistenceExtensions.AddCoreAndWrapperServices): the provider is a factory-built
    // singleton, forwarded as an IMeshQueryProvider; the registry is PROVIDED (not owned by this container).
    private static IServiceProvider Container(ModuleContexts registry) =>
        new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton<StaticNodeQueryProvider>(_ =>
                new StaticNodeQueryProvider([], _ => false, null, null, registry))
            .AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<StaticNodeQueryProvider>())
            .CreateMeshWeaverServiceProvider();
}
