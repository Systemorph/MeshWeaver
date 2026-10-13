using System.Reflection;
using Autofac;
using Autofac.Core.Lifetime;
using MeshWeaver.Fixture;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6584 — <b>a caller-scoped root service that reaches a module's scoped service while it is
/// built, and that module service needing a caller-scoped root service while IT is built, must both
/// complete.</b>
///
/// <para>Autofac builds a scope's shared instances under that scope's one shared-instance lock. The module
/// scope a caller owns is a scope of the MODULE container, and its root resolutions are made in the
/// caller's own scope, so the two locks were taken in both orders: caller then module scope (a root service
/// of the caller reaching the module), module scope then caller (the module service taking a root service
/// of the caller not built yet). The fix gives the module scope its caller's lock — one lock owner — so
/// the second build waits before it holds anything.</para>
///
/// <para>Real containers, nothing mocked: a root built as the mesh builds it, a module container built by
/// <see cref="ModuleServiceProvider"/>, and one caller scope. Each construction is held where it owns its
/// lock until the other has arrived (or the meeting bound passes), so the two orders overlap whenever the
/// locks allow it. The negative control is the same module scope with its own lock back — it deadlocks.</para>
/// </summary>
public sealed class CallerAndModuleScopedServicesBuildInOneLockTest
{
    private static readonly TimeSpan Meet = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan JoinBound = Meet + Meet + TimeSpan.FromSeconds(5);

    [Fact]
    public void ACallerServiceReachingAModule_AndAModuleServiceNeedingTheCaller_BothComplete()
    {
        var mesh = new Mesh();
        using var caller = mesh.Root.CreateScope();
        var callerScope = caller.ServiceProvider.GetRequiredService<ILifetimeScope>();
        mesh.Gate.Arm();
        var callerSide = new Worker("caller-scoped-root-service-reaches-the-module",
            () => caller.ServiceProvider.GetRequiredService<CallerNeedsModule>());
        var moduleSide = new Worker("module-scoped-service-needs-the-caller",
            () => ModuleServiceProvider.InCallerScope(mesh.Module, callerScope).GetRequiredService<ModuleNeedsCaller>());
        try
        {
            callerSide.Start();
            SpinWait.SpinUntil(() => mesh.Gate.CallerEntered, TestTimeouts.Convergence)
                .Should().BeTrue("the caller-side construction must be under way before the module side starts");
            moduleSide.Start();

            callerSide.Join(JoinBound).Should().BeTrue(
                "the module scope builds under its caller's lock, so the module side waits before it holds anything");
            moduleSide.Join(JoinBound).Should().BeTrue("the module side then reads the instance the caller side built");
            callerSide.Fault.Should().BeNull();
            moduleSide.Fault.Should().BeNull();
            var built = ModuleServiceProvider.InCallerScope(mesh.Module, callerScope).GetRequiredService<ModuleNeedsCaller>();
            caller.ServiceProvider.GetRequiredService<CallerNeedsModule>().Module.Should().BeSameAs(built,
                "both sides see the caller's one instance of the module's scoped service");
            built.Other.Should().BeSameAs(caller.ServiceProvider.GetRequiredService<OtherCallerService>(),
                "its root dependency is the caller's own instance");
        }
        finally
        {
            callerSide.Abandon();
            moduleSide.Abandon();
        }
    }

    /// <summary>
    /// The negative control: the SAME caller-owned module scope with its own lock back — the shape before
    /// the fix — and the same two constructions in the same forced order. They deadlock, so the test above
    /// can tell a deadlock from a pass.
    /// </summary>
    [Fact]
    public void TheSameShape_WithTheModuleScopesOwnLock_Deadlocks()
    {
        var mesh = new Mesh();
        var caller = mesh.Root.CreateScope();
        var callerScope = caller.ServiceProvider.GetRequiredService<ILifetimeScope>();
        var moduleScope = ModuleServiceProvider.InCallerScope(mesh.Module, callerScope).GetRequiredService<ILifetimeScope>();
        typeof(LifetimeScope).GetField("_synchRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(moduleScope, new object());
        mesh.Gate.Arm();
        var callerSide = new Worker("caller-scoped-root-service-reaches-the-module",
            () => caller.ServiceProvider.GetRequiredService<CallerNeedsModule>());
        var moduleSide = new Worker("module-scoped-service-needs-the-caller",
            () => ModuleServiceProvider.InCallerScope(mesh.Module, callerScope).GetRequiredService<ModuleNeedsCaller>());
        try
        {
            callerSide.Start();
            SpinWait.SpinUntil(() => mesh.Gate.CallerEntered, TestTimeouts.Convergence).Should().BeTrue();
            moduleSide.Start();
            SpinWait.SpinUntil(() => mesh.Gate.ModuleEntered, TestTimeouts.Convergence)
                .Should().BeTrue("with two locks both constructions are inside their own lock at once");

            callerSide.Join(JoinBound).Should().BeFalse("the caller's lock is held while the module scope's is awaited");
            moduleSide.Join(TimeSpan.Zero).Should().BeFalse("the module scope's lock is held while the caller's is awaited");
        }
        finally
        {
            callerSide.Abandon();
            moduleSide.Abandon();
        }
    }

    /// <summary>A root with caller-scoped services, and a module container the caller reaches into.</summary>
    private sealed class Mesh
    {
        public LockMeeting Gate { get; } = new();
        public IServiceProvider Root { get; }
        public IServiceProvider Module { get; }

        public Mesh()
        {
            var holder = new ModuleHolder();
            var gate = Gate;
            Root = new ServiceCollection()
                .AddScoped<OtherCallerService>()
                // A caller-scoped root service whose construction reaches the module's scoped service — as
                // a circuit's component service that takes a module's scoped registration does.
                .AddScoped(sp =>
                {
                    gate.CallerHolds();
                    return new CallerNeedsModule(ModuleServiceProvider
                        .InCallerScope(holder.Module!, sp.GetRequiredService<ILifetimeScope>())
                        .GetRequiredService<ModuleNeedsCaller>());
                })
                .CreateMeshWeaverServiceProvider();
            // The module's scoped service, whose construction takes a caller-scoped root service not built yet.
            Module = ModuleServiceProvider.Create(
                new ServiceCollection().AddScoped(sp =>
                {
                    gate.ModuleHolds();
                    return new ModuleNeedsCaller(sp.GetRequiredService<OtherCallerService>());
                }),
                Root, _ => false, []);
            holder.Module = Module;
        }
    }

    private sealed class ModuleHolder
    {
        public IServiceProvider? Module { get; set; }
    }

    /// <summary>A caller-scoped root service the module's service needs.</summary>
    private sealed class OtherCallerService;

    /// <summary>The module's scoped service.</summary>
    private sealed class ModuleNeedsCaller(OtherCallerService other)
    {
        public OtherCallerService Other { get; } = other;
    }

    /// <summary>The caller-scoped root service that reaches the module.</summary>
    private sealed class CallerNeedsModule(ModuleNeedsCaller module)
    {
        public ModuleNeedsCaller Module { get; } = module;
    }

    /// <summary>Holds each construction where it owns its lock until the other has arrived.</summary>
    private sealed class LockMeeting
    {
        private int armed;
        private int callerEntered;
        private int moduleEntered;

        public bool CallerEntered => Volatile.Read(ref callerEntered) == 1;
        public bool ModuleEntered => Volatile.Read(ref moduleEntered) == 1;

        public void Arm() => Volatile.Write(ref armed, 1);

        public void CallerHolds() => MeetAt(ref callerEntered, () => ModuleEntered);

        public void ModuleHolds() => MeetAt(ref moduleEntered, () => CallerEntered);

        private void MeetAt(ref int entered, Func<bool> other)
        {
            if (Volatile.Read(ref armed) == 0)
                return;
            Volatile.Write(ref entered, 1);
            SpinWait.SpinUntil(other, Meet);
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
