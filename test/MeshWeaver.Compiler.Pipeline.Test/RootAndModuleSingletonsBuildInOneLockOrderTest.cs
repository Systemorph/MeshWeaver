using MeshWeaver.Fixture;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6542 — <b>a root singleton that reaches a module's service while it is built, and a
/// module singleton that needs a root singleton while it is built, must both complete.</b>
///
/// <para>Autofac holds a scope's shared-instance lock for the whole build of a shared instance, and that
/// lock covers every shared instance in the scope. A root singleton whose construction resolves a
/// module's service takes the root's lock, then the module container's. A module singleton whose
/// construction resolves a root singleton (through <c>ModuleFallbackSource</c>) took the module
/// container's lock, then the root's. Two first-time builds overlapping in those orders deadlocked —
/// the #6432 shape across the container boundary, reproduced while reading that incident's dump.</para>
///
/// <para>The fix is <b>built before published</b>: every shared instance of a module container is
/// built (<see cref="ModuleServiceProvider.BuildSharedInstances"/>) before the container can be reached
/// from the root, so the root only ever reads a module's built instances — answered without any lock.</para>
///
/// <para>The containers are real and nothing is mocked: a root built as the mesh builds it, and a
/// module container built by <see cref="ModuleServiceProvider"/>. Each construction is held where it
/// owns its own lock until the other has arrived (or the meeting bound passes), so the opposite-order
/// requests overlap whenever the locks allow them to.</para>
/// </summary>
public sealed class RootAndModuleSingletonsBuildInOneLockOrderTest
{
    private static readonly TimeSpan JoinBound = Bound.Meet + Bound.Meet + TimeSpan.FromSeconds(5);

    [Fact]
    public void ARootSingletonReachingAModule_AndAModuleSingletonNeedingARootSingleton_BothComplete()
    {
        var mesh = new Mesh(ordered: true);
        mesh.Gate.Arm();
        var rootSide = new Worker("root-singleton-reaches-a-module-service",
            () => mesh.Root.GetRequiredService<RootNeedsModule>());
        var moduleSide = new Worker("module-singleton-needs-a-root-singleton",
            () => mesh.Module.GetRequiredService<ModuleNeedsRoot>());
        try
        {
            rootSide.Start();
            SpinWait.SpinUntil(() => mesh.Gate.RootEntered, TestTimeouts.Convergence)
                .Should().BeTrue("the root singleton's construction must be under way before the module's starts");
            moduleSide.Start();

            rootSide.Join(JoinBound).Should().BeTrue(
                "the module's singleton was built before the module could be reached from the root, so the "
                + "root's construction reads it without waiting for the module container's lock");
            moduleSide.Join(JoinBound).Should().BeTrue("the module side reads its built singleton");
            rootSide.Fault.Should().BeNull();
            moduleSide.Fault.Should().BeNull();
            mesh.Root.GetRequiredService<RootNeedsModule>().Module.Should()
                .BeSameAs(mesh.Module.GetRequiredService<ModuleNeedsRoot>(),
                    "both sides see the module's one singleton");
            mesh.ModuleSingletonsBuilt.Should().Be(1, "the module's singleton was built once, before it was published");
        }
        finally
        {
            rootSide.Abandon();
            moduleSide.Abandon();
        }
    }

    [Fact]
    public void AModuleSingleton_IsBuiltBeforeTheModuleIsPublished_AndServedAsBuilt()
    {
        var mesh = new Mesh(ordered: true);
        mesh.ModuleSingletonsBuilt.Should().Be(1, "the module's singleton is built before anything can reach it");
        var built = mesh.Module.GetRequiredService<ModuleNeedsRoot>();
        mesh.Module.GetRequiredService<ModuleNeedsRoot>().Should().BeSameAs(built);
        mesh.Root.GetRequiredService<RootNeedsModule>().Module.Should().BeSameAs(built);
    }

    /// <summary>
    /// The negative control: the same two constructions in the same forced order, against the SAME
    /// module container published WITHOUT building its shared instances first — the lifecycle before
    /// the fix. They deadlock, so the test above can tell a deadlock from a pass.
    /// </summary>
    [Fact]
    public void TheSameShape_PublishedBeforeItsSingletonsAreBuilt_Deadlocks()
    {
        var mesh = new Mesh(ordered: false);
        mesh.Gate.Arm();
        var rootSide = new Worker("root-singleton-reaches-a-module-service",
            () => mesh.Root.GetRequiredService<RootNeedsModule>());
        var moduleSide = new Worker("module-singleton-needs-a-root-singleton",
            () => mesh.Module.GetRequiredService<ModuleNeedsRoot>());
        try
        {
            rootSide.Start();
            SpinWait.SpinUntil(() => mesh.Gate.RootEntered, TestTimeouts.Convergence).Should().BeTrue();
            moduleSide.Start();
            SpinWait.SpinUntil(() => mesh.Gate.ModuleEntered, TestTimeouts.Convergence)
                .Should().BeTrue("without the ordering both constructions are inside their own lock at once");

            rootSide.Join(JoinBound).Should().BeFalse("the root lock is held while the module's lock is awaited");
            mesh.ModuleSingletonsBuilt.Should().Be(0, "the module's singleton was never built — its construction is parked");
            moduleSide.Join(TimeSpan.Zero).Should().BeFalse("the module's lock is held while the root lock is awaited");
        }
        finally
        {
            rootSide.Abandon();
            moduleSide.Abandon();
        }
    }

    /// <summary>A root, and a module container the root reaches into.</summary>
    private sealed class Mesh
    {
        private int moduleSingletonsBuilt;

        public LockMeeting Gate { get; } = new();
        public IServiceProvider Root { get; }
        public IServiceProvider Module { get; }
        public int ModuleSingletonsBuilt => Volatile.Read(ref moduleSingletonsBuilt);

        /// <param name="ordered">True: the module container is published only after its shared instances
        /// are built — the production lifecycle. False: published as built, lazily — the negative control.</param>
        public Mesh(bool ordered)
        {
            var moduleHolder = new ModuleHolder();
            Root = new ServiceCollection()
                .AddSingleton(Gate)
                .AddSingleton<OtherRootSingleton>()
                // The root side: a root singleton whose construction reaches a module's service — as
                // ModuleOwnedRootSource, a Current-route forwarder or a module's IConfigureOptions
                // proxy do from inside a root singleton's constructor.
                .AddSingleton(sp =>
                {
                    sp.GetRequiredService<LockMeeting>().RootHolds();
                    return new RootNeedsModule(moduleHolder.Module!.GetRequiredService<ModuleNeedsRoot>());
                })
                .CreateMeshWeaverServiceProvider();

            // The module side: a module singleton whose construction needs a root singleton not built yet.
            var moduleServices = new ServiceCollection()
                .AddSingleton(sp =>
                {
                    sp.GetRequiredService<LockMeeting>().ModuleHolds();
                    var built = new ModuleNeedsRoot(sp.GetRequiredService<OtherRootSingleton>());
                    Interlocked.Increment(ref moduleSingletonsBuilt);
                    return built;
                });
            Module = ModuleServiceProvider.Create(moduleServices, Root, _ => false, []);
            if (ordered)
                // Built before published — as ModuleServices.BuildBeforePublish does at boot and before
                // a swap commits. The gate is not armed yet: this build meets nobody.
                ModuleServiceProvider.BuildSharedInstances(Module, _ => false);
            // Published: from here on the root can reach the module.
            moduleHolder.Module = Module;
        }
    }

    private sealed class ModuleHolder
    {
        public IServiceProvider? Module { get; set; }
    }

    /// <summary>A root singleton built on first resolve, needed by the module's singleton.</summary>
    private sealed class OtherRootSingleton;

    /// <summary>The module's singleton.</summary>
    private sealed class ModuleNeedsRoot(OtherRootSingleton root)
    {
        public OtherRootSingleton Root { get; } = root;
    }

    /// <summary>The root singleton that reaches the module.</summary>
    private sealed class RootNeedsModule(ModuleNeedsRoot module)
    {
        public ModuleNeedsRoot Module { get; } = module;
    }

    private static class Bound
    {
        public static readonly TimeSpan Meet = TimeSpan.FromSeconds(5);
    }

    /// <summary>Holds each construction where it owns its own lock until the other has arrived.</summary>
    private sealed class LockMeeting
    {
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
            SpinWait.SpinUntil(other, Bound.Meet);
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
