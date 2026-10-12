using MeshWeaver.Fixture;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6432 / #6391 — <b>a module asking for a SCOPED root service must never have it built,
/// and shared, in the root.</b>
///
/// <para>Read off the FailFast dump of the evicted memex-cloud pod (control instance,
/// <c>Ops/Actions/analyzedump-memex-cloud-20261011-ltlwc-3</c>). Two threads, two Autofac
/// shared-instance locks, taken in opposite orders:</para>
/// <list type="bullet">
///   <item>OS thread <c>0x1e</c>, the host's <c>ApplicationStarted</c> callback: the WhatsApp module's
///     reply sender, from a scope of its OWN container, asks for <c>PortalApplication</c> (a scoped
///     root service). The module container's fallback resolved it in the ROOT scope, so it was built
///     as a root-shared instance under the root container's lock — and its constructor reads the mesh
///     hub's scope (<c>hub.ServiceProvider.GetRequiredService&lt;ILayoutClient&gt;()</c>), whose lock
///     it waits for.</item>
///   <item>OS thread <c>0xf1</c>, a grain activation (<c>MessageHubGrain.ResolveHubConfigurationObservable</c>):
///     builds <c>IMeshNodeHubFactory</c> in the mesh hub's scope (that scope's lock), whose
///     constructor argument is a root singleton not built yet — the root lock.</item>
/// </list>
/// <para>Everything else that needed a root singleton queued behind the root lock — Orleans' codec
/// for <c>IndirectProbeResponse</c> among them, so the silo answered no probe and was voted dead.</para>
///
/// <para>The shape here is exactly that one, with real containers and nothing mocked: a root, a
/// sibling child scope standing for the mesh hub's, and a module container built by
/// <see cref="ModuleServiceProvider"/>. The two constructions are held at the point where each owns
/// its own lock, so the opposite-order requests are forced to overlap.</para>
/// </summary>
public sealed class AScopedRootServiceIsNeverBuiltInTheRootTest
{
    private static readonly TimeSpan JoinBound = TimeSpanBound.Meet + TimeSpan.FromSeconds(10);

    [Fact]
    public void AScopedRootService_AskedFromTwoModuleScopes_IsTwoInstances_NeitherTheRoots()
    {
        var mesh = new Mesh();

        using var first = mesh.Module.CreateScope();
        using var second = mesh.Module.CreateScope();
        var fromFirst = first.ServiceProvider.GetRequiredService<ScopedRootService>();
        var fromSecond = second.ServiceProvider.GetRequiredService<ScopedRootService>();
        using var rootChild = mesh.Root.CreateScope();
        var fromRootChild = rootChild.ServiceProvider.GetRequiredService<ScopedRootService>();

        fromFirst.Should().BeSameAs(first.ServiceProvider.GetRequiredService<ScopedRootService>(),
            "within one module scope a scoped root service is that scope's one instance");
        fromFirst.Should().NotBeSameAs(fromSecond,
            "two module scopes are two scopes: a scoped root service shared between them was built in the root");
        fromFirst.Should().NotBeSameAs(fromRootChild);
        mesh.Module.GetRequiredService<RootSingleton>().Should().BeSameAs(mesh.Root.GetRequiredService<RootSingleton>(),
            "a singleton is still the root's one instance");
    }

    [Fact]
    public void ScopedRootServiceReadingAHubScope_AndAHubScopeServiceNeedingARootSingleton_BothComplete()
    {
        var mesh = new Mesh();
        mesh.Gate.Arm();
        // As 0x1e: the module's own scope asks for the scoped root service.
        var moduleSide = new Worker("module-scope-asks-for-a-scoped-root-service", () =>
        {
            using var scope = mesh.Module.CreateScope();
            scope.ServiceProvider.GetRequiredService<ScopedRootService>();
        });
        // As 0xf1: the hub scope builds its service, whose constructor argument is a root singleton.
        var hubSide = new Worker("hub-scope-service-needs-a-root-singleton",
            () => mesh.HubScope.GetRequiredService<HubScopeService>());
        try
        {
            moduleSide.Start();
            hubSide.Start();
            SpinWait.SpinUntil(() => mesh.Gate.ScopedEntered && mesh.Gate.HubEntered, TestTimeouts.Convergence)
                .Should().BeTrue("both constructions must be inside their own lock at the same time");

            moduleSide.Join(JoinBound).Should().BeTrue(
                "the scoped root service is built in the module scope's own root child scope, so building it "
                + "holds no lock the hub scope's construction waits for");
            hubSide.Join(JoinBound).Should().BeTrue("the hub scope's construction gets the root singleton");
            moduleSide.Fault.Should().BeNull();
            hubSide.Fault.Should().BeNull();
        }
        finally
        {
            moduleSide.Abandon();
            hubSide.Abandon();
        }
    }

    /// <summary>
    /// The negative control: the same two constructions in the same forced order, with the scoped
    /// service resolved IN THE ROOT — where the module fallback used to resolve it. They deadlock, so
    /// the test above can tell a deadlock from a pass.
    /// </summary>
    [Fact]
    public void TheSameShape_WithTheScopedServiceBuiltInTheRoot_Deadlocks()
    {
        var mesh = new Mesh();
        mesh.Gate.Arm();
        var rootSide = new Worker("root-builds-the-scoped-service",
            () => mesh.Root.GetRequiredService<ScopedRootService>());
        var hubSide = new Worker("hub-scope-service-needs-a-root-singleton",
            () => mesh.HubScope.GetRequiredService<HubScopeService>());
        try
        {
            rootSide.Start();
            hubSide.Start();
            SpinWait.SpinUntil(() => mesh.Gate.ScopedEntered && mesh.Gate.HubEntered, TestTimeouts.Convergence)
                .Should().BeTrue("both constructions must be inside their own lock at the same time");

            rootSide.Join(JoinBound).Should().BeFalse("the root lock is held while the hub scope's lock is awaited");
            hubSide.Join(TimeSpan.Zero).Should().BeFalse("the hub scope's lock is held while the root lock is awaited");
        }
        finally
        {
            rootSide.Abandon();
            hubSide.Abandon();
        }
    }

    /// <summary>A root, a sibling scope standing for the mesh hub's, and a module container.</summary>
    private sealed class Mesh
    {
        public LockMeeting Gate { get; } = new();
        public IServiceProvider Root { get; }
        public IServiceProvider HubScope { get; }
        public IServiceProvider Module { get; }

        public Mesh()
        {
            var hubScopeHolder = new HubScopeHolder();
            var services = new ServiceCollection()
                .AddSingleton(Gate)
                .AddSingleton(hubScopeHolder)
                .AddSingleton<RootSingleton>()
                // The PortalApplication shape: scoped, and its construction reads the hub's scope.
                .AddScoped(sp =>
                {
                    sp.GetRequiredService<LockMeeting>().ScopedHolds();
                    sp.GetRequiredService<HubScopeHolder>().Scope!.GetRequiredService<HubScopeService>();
                    return new ScopedRootService();
                });
            Root = services.CreateMeshWeaverServiceProvider();
            // The mesh hub's scope: a child scope of the root with registrations of its own, shared there.
            HubScope = new ServiceCollection()
                .AddSingleton(sp =>
                {
                    sp.GetRequiredService<LockMeeting>().HubHolds();
                    return new HubScopeService(sp.GetRequiredService<RootSingleton>());
                })
                .SetupModules(Root);
            hubScopeHolder.Scope = HubScope;
            Module = ModuleServiceProvider.Create(new ServiceCollection(), Root, _ => false, []);
        }
    }

    private sealed class HubScopeHolder
    {
        public IServiceProvider? Scope { get; set; }
    }

    /// <summary>A root singleton built on first resolve.</summary>
    private sealed class RootSingleton;

    /// <summary>A scoped root service.</summary>
    private sealed class ScopedRootService;

    /// <summary>A service shared in the hub's scope, taking a root singleton.</summary>
    private sealed class HubScopeService(RootSingleton root)
    {
        public RootSingleton Root { get; } = root;
    }

    private static class TimeSpanBound
    {
        public static readonly TimeSpan Meet = TimeSpan.FromSeconds(10);
    }

    /// <summary>Holds each construction where it owns its own lock until the other has arrived.</summary>
    private sealed class LockMeeting
    {
        private int armed;
        private int scopedEntered;
        private int hubEntered;

        public bool ScopedEntered => Volatile.Read(ref scopedEntered) == 1;
        public bool HubEntered => Volatile.Read(ref hubEntered) == 1;

        public void Arm() => Volatile.Write(ref armed, 1);

        public void ScopedHolds() => Meet(ref scopedEntered, () => HubEntered);

        public void HubHolds() => Meet(ref hubEntered, () => ScopedEntered);

        private void Meet(ref int entered, Func<bool> other)
        {
            if (Volatile.Read(ref armed) == 0)
                return;
            Volatile.Write(ref entered, 1);
            SpinWait.SpinUntil(other, TimeSpanBound.Meet);
        }
    }

    /// <summary>A background thread that records its fault, and can be freed from a monitor wait.</summary>
    private sealed class Worker
    {
        private readonly Thread thread;
        private Exception? fault;

        public Worker(string name, Action body)
        {
            thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref fault, exception);
                }
            })
            { IsBackground = true, Name = name };
        }

        public Exception? Fault => Volatile.Read(ref fault);

        public void Start() => thread.Start();

        public bool Join(TimeSpan bound) => thread.Join(bound);

        /// <summary>A thread parked in <c>Monitor.Enter</c> is released by an interrupt, so a deadlock —
        /// the negative control's, or a regression's — ends with the test instead of outliving it.</summary>
        public void Abandon()
        {
            if (thread.IsAlive)
                thread.Interrupt();
            thread.Join(TimeSpan.FromSeconds(10));
        }
    }
}
