using MeshWeaver.Fixture;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6542 — <b>the root container and a module container must take their shared-instance locks in
/// ONE order.</b> The same deadlock class as #6432 (<see cref="AScopedRootServiceIsNeverBuiltInTheRootTest"/>),
/// for the other pair of locks.
///
/// <para>Autofac holds a scope's shared-instance lock for the whole build of a shared instance, and that
/// one lock covers every shared instance of the scope. Two containers, so two locks:</para>
/// <list type="bullet">
///   <item><b>Thread A</b> builds a ROOT singleton, so it holds the root container's lock. The singleton's
///     construction reaches a service of the module the way every root forwarder does
///     (<c>ModuleOwnedRootSource</c>'s stand-ins, the <c>Current</c> forwarder,
///     <c>ModuleServiceProxy.Invoke</c>, <c>ModuleHealthCheckOptionsForwarder</c>), through
///     <c>ModuleServiceProvider.ResolveAt</c>, and that service is not built yet: A waits for the module
///     container's lock.</item>
///   <item><b>Thread B</b> builds a MODULE singleton, so it holds the module container's lock. The
///     singleton's construction needs a root singleton that is not built yet: the module's fallback
///     (<c>ModuleFallbackSource</c>) resolves it in the module scope's root child scope, a root singleton is
///     shared in the root, and B waits for the root container's lock.</item>
/// </list>
///
/// <para>The shape here is exactly that one, with real containers and nothing mocked: a root built by
/// <c>CreateMeshWeaverServiceProvider</c> and a module container built by
/// <see cref="ModuleServiceProvider"/>. Each construction is held at the point where it owns its own lock
/// until the other has arrived, so the opposite-order requests are forced to overlap. A correct fix may let
/// one of the two builds wait for the other BEFORE it owns its lock — so the test asserts that both
/// complete, never that both were inside at the same time.</para>
/// </summary>
public sealed class RootAndModuleContainersTakeTheirLocksInOneOrderTest
{
    private static readonly TimeSpan JoinBound = LockMeeting.Bound + TimeSpan.FromSeconds(10);

    [Fact]
    public void ARootSingletonReachingAModuleService_AndAModuleSingletonNeedingARootSingleton_BothComplete()
    {
        var mesh = new Mesh();
        mesh.Meeting.Arm();
        // As A: a root singleton whose construction reaches a module service that is not built yet.
        var rootSide = new Worker("root-singleton-reaches-a-module-service",
            () => mesh.Root.GetRequiredService<RootSingletonA>());
        // As B: a module singleton whose construction needs a root singleton that is not built yet.
        var moduleSide = new Worker("module-singleton-needs-a-root-singleton",
            () => mesh.Module.GetRequiredService<ModuleSingletonY>());
        try
        {
            rootSide.Start();
            moduleSide.Start();

            rootSide.Join(JoinBound).Should().BeTrue(
                "the root singleton's construction must get the module's service without the two containers "
                + "locking each other out in opposite order");
            moduleSide.Join(JoinBound).Should().BeTrue(
                "the module singleton's construction must get the root singleton without the two containers "
                + "locking each other out in opposite order");
            rootSide.Fault.Should().BeNull();
            moduleSide.Fault.Should().BeNull();
        }
        finally
        {
            rootSide.Abandon();
            moduleSide.Abandon();
        }

        mesh.Module.GetRequiredService<ModuleSingletonY>().Root.Should().BeSameAs(
            mesh.Root.GetRequiredService<RootSingletonB>(),
            "a root singleton is still the root's one instance when a module asks for it");
        mesh.Root.GetRequiredService<RootSingletonA>().Module.Should().BeSameAs(
            mesh.Module.GetRequiredService<ModuleSingletonX>(),
            "a module singleton is still the module's one instance when the root reaches it");
    }

    /// <summary>
    /// The negative control: the same two constructions in the same forced order across two plain
    /// containers that reach each other directly, nothing in between. They deadlock, so the test above can
    /// tell a deadlock from a pass.
    /// </summary>
    [Fact]
    public void TheSameShape_BetweenTwoContainersThatReachEachOtherDirectly_Deadlocks()
    {
        var meeting = new LockMeeting();
        meeting.Arm();
        IServiceProvider? second = null;
        var first = new ServiceCollection()
            .AddSingleton<RootSingletonB>()
            .AddSingleton(_ =>
            {
                meeting.RootHolds();
                return new RootSingletonA(second!.GetRequiredService<ModuleSingletonX>());
            })
            .CreateMeshWeaverServiceProvider();
        second = new ServiceCollection()
            .AddSingleton<ModuleSingletonX>()
            .AddSingleton(_ =>
            {
                meeting.ModuleHolds();
                return new ModuleSingletonY(first.GetRequiredService<RootSingletonB>());
            })
            .CreateMeshWeaverServiceProvider();
        var secondProvider = second;

        var firstSide = new Worker("first-container-builds-and-reaches-the-second",
            () => first.GetRequiredService<RootSingletonA>());
        var secondSide = new Worker("second-container-builds-and-reaches-the-first",
            () => secondProvider.GetRequiredService<ModuleSingletonY>());
        try
        {
            firstSide.Start();
            secondSide.Start();
            SpinWait.SpinUntil(() => meeting.RootEntered && meeting.ModuleEntered, TestTimeouts.Convergence)
                .Should().BeTrue("both constructions must be inside their own lock at the same time");

            firstSide.Join(JoinBound).Should().BeFalse("the first container's lock is held while the second's is awaited");
            secondSide.Join(TimeSpan.Zero).Should().BeFalse("the second container's lock is held while the first's is awaited");
        }
        finally
        {
            firstSide.Abandon();
            secondSide.Abandon();
        }
    }

    /// <summary>A root container, and a module container built by <see cref="ModuleServiceProvider"/>.</summary>
    private sealed class Mesh
    {
        public LockMeeting Meeting { get; } = new();
        public IServiceProvider Root { get; }
        public IServiceProvider Module { get; }

        public Mesh()
        {
            var meeting = Meeting;
            IServiceProvider? module = null;
            Root = new ServiceCollection()
                .AddSingleton<RootSingletonB>()
                .AddSingleton(_ =>
                {
                    // A ROOT singleton: the root's lock is held for this whole construction.
                    meeting.RootHolds();
                    // Reaching the module the way the root's forwarders do (ModuleOwnedRootSource's stand-ins).
                    return new RootSingletonA(
                        (ModuleSingletonX)ModuleServiceProvider.ResolveAt(module!, typeof(ModuleSingletonX), 0));
                })
                .CreateMeshWeaverServiceProvider();
            Module = ModuleServiceProvider.Create(
                new ServiceCollection()
                    .AddSingleton<ModuleSingletonX>()
                    .AddSingleton(sp =>
                    {
                        // A MODULE singleton: the module container's lock is held for this whole construction.
                        meeting.ModuleHolds();
                        // Needing a root singleton: the module's fallback resolves it in the root.
                        return new ModuleSingletonY(sp.GetRequiredService<RootSingletonB>());
                    }),
                Root, _ => false, []);
            module = Module;
        }
    }

    /// <summary>A root singleton built on first resolve.</summary>
    private sealed class RootSingletonB;

    /// <summary>A module singleton built on first resolve.</summary>
    private sealed class ModuleSingletonX;

    /// <summary>A root singleton whose construction reaches a module service.</summary>
    private sealed class RootSingletonA(ModuleSingletonX module)
    {
        public ModuleSingletonX Module { get; } = module;
    }

    /// <summary>A module singleton whose construction needs a root singleton.</summary>
    private sealed class ModuleSingletonY(RootSingletonB root)
    {
        public RootSingletonB Root { get; } = root;
    }

    /// <summary>Holds each construction where it owns its own lock until the other has arrived.</summary>
    private sealed class LockMeeting
    {
        public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        private int armed;
        private int rootEntered;
        private int moduleEntered;

        public bool RootEntered => Volatile.Read(ref rootEntered) == 1;
        public bool ModuleEntered => Volatile.Read(ref moduleEntered) == 1;

        public void Arm() => Volatile.Write(ref armed, 1);

        public void RootHolds() => Meet(ref rootEntered, () => ModuleEntered);

        public void ModuleHolds() => Meet(ref moduleEntered, () => RootEntered);

        private void Meet(ref int entered, Func<bool> other)
        {
            if (Volatile.Read(ref armed) == 0)
                return;
            Volatile.Write(ref entered, 1);
            SpinWait.SpinUntil(other, Bound);
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
