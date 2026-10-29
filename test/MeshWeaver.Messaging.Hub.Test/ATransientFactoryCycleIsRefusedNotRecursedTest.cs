using System;
using System.Collections.Generic;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// MeshWeaver#7400 — a cycle made only of TRANSIENT registrations through a factory has no shared slot
/// (<c>InstanceSlot.BuildingThread</c>) and no resolve chain (a factory is handed the scope itself, so the
/// chain restarts inside it). Before the fix it recursed until the process died of a stack overflow
/// (measured on #5388's mutation: 1,804 <c>MeshServiceProvider.Build</c> frames, exit 134). Every module
/// stand-in is such a registration (<see cref="ServiceRegistration.ForDelegate"/>, transient by default),
/// and a source mints its stand-ins afresh on every lookup — so a guard keyed by registration identity
/// would not see a cycle made of them.
///
/// <para>Each factory counts its own depth and gives up LOUDLY past <see cref="Bound"/>: without the
/// refusal a test fails on that bound — "recursed N deep" — instead of killing the test host, so the
/// failure mode before the fix is an assertion, never a crash. Real containers, real sources; nothing is
/// mocked.</para>
/// </summary>
public class ATransientFactoryCycleIsRefusedNotRecursedTest
{
    private const int Bound = 64;

    public interface IFirst;
    public interface ISecond;
    private sealed class First : IFirst;
    private sealed class Second : ISecond;

    /// <summary>How deep a cycle went — instance state of one test, never static.</summary>
    public sealed class Depth
    {
        public int Value;

        public void Enter()
        {
            if (++Value > Bound)
                throw new RecursedUnrefusedException(
                    $"recursed {Bound} deep — the transient cycle was not refused by the container");
        }
    }

    public sealed class RecursedUnrefusedException(string message) : Exception(message);

    [Fact]
    public void ACycleOfTransientFactoryRegistrations_IsRefusedByName()
    {
        var depth = new Depth();
        var root = new ServiceCollection()
            .AddTransient<IFirst>(sp => { depth.Enter(); sp.GetRequiredService<ISecond>(); return new First(); })
            .AddTransient<ISecond>(sp => { depth.Enter(); sp.GetRequiredService<IFirst>(); return new Second(); })
            .CreateMeshWeaverServiceProvider();

        Action resolve = () => root.GetRequiredService<IFirst>();

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*circular dependency*IFirst*asked for itself again*",
                "the second entry of IFirst on this thread, while it is still being built, is the cycle");
        depth.Value.Should().BeLessThanOrEqualTo(3, "it is refused on its first repeat, not after recursing");
    }

    /// <summary>
    /// The shape #5388 took: stand-ins a registration SOURCE mints afresh on every lookup, each a
    /// transient <see cref="ServiceRegistration.ForDelegate"/> resolving the other — no two lookups share a
    /// registration, so only a key that survives the re-minting can see the repeat.
    /// </summary>
    [Fact]
    public void ACycleOfFreshlyMintedSourceStandIns_IsRefusedByName()
    {
        var depth = new Depth();
        var root = new ServiceCollection()
            .AddSingleton<IServiceRegistrationSource>(new MintsStandInsAfresh(depth))
            .CreateMeshWeaverServiceProvider();

        Action resolve = () => root.GetRequiredService<IFirst>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*circular dependency*IFirst*");
        depth.Value.Should().BeLessThanOrEqualTo(3);
    }

    /// <summary>A deferred <c>Func&lt;T&gt;</c> invoked inside a constructor restarts the chain the same way a
    /// factory does — so a constructor-only cycle closed through it is refused as well.</summary>
    [Fact]
    public void ACycleClosedThroughADeferredFuncInAConstructor_IsRefusedByName()
    {
        var root = new ServiceCollection()
            .AddSingleton(new Depth())
            .AddTransient<CallsItsFuncWhenBuilt>()
            .AddTransient<NeedsTheCaller>()
            .CreateMeshWeaverServiceProvider();

        Action resolve = () => root.GetRequiredService<CallsItsFuncWhenBuilt>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*circular dependency*");
        root.GetRequiredService<Depth>().Value.Should().BeLessThanOrEqualTo(3);
    }

    /// <summary>
    /// The negative controls: what is NOT a cycle still resolves. The same transient asked for twice in a
    /// row (and twice nested through DIFFERENT services) builds twice; a decorator that resolves ANOTHER
    /// registration of its own service is untouched; and a refused or faulted build leaves nothing behind
    /// — the next resolve on the same thread builds normally.
    /// </summary>
    [Fact]
    public void WhatIsNotACycle_StillResolves_AndARefusalLeavesNothingBehind()
    {
        var fail = true;
        var root = new ServiceCollection()
            .AddTransient<ISecond>(_ => fail ? throw new InvalidOperationException("transient fault") : new Second())
            .AddTransient<Pair>(sp => new Pair(sp.GetRequiredService<ISecond>(), sp.GetRequiredService<ISecond>()))
            .AddTransient<IFirst>(_ => new First())
            .AddTransient<IFirst>(sp =>
            {
                // A decorator over the registration before it — another registration of the same service.
                var scope = MeshServiceProvider.From(sp)!;
                var inner = scope.RegistrationsOf(typeof(IFirst))[0];
                return new Decorated((IFirst)scope.ResolveRegistration(inner));
            })
            .CreateMeshWeaverServiceProvider();

        root.GetRequiredService<IFirst>().Should().BeOfType<Decorated>()
            .Which.Inner.Should().BeOfType<First>("a decorator resolving another registration is not a cycle");

        ((Action)(() => root.GetRequiredService<ISecond>())).Should().Throw<InvalidOperationException>()
            .WithMessage("transient fault");
        fail = false;
        var pair = root.GetRequiredService<Pair>();
        pair.A.Should().NotBeSameAs(pair.B, "a transient asked twice in one build is two builds, not a cycle");
    }

    private sealed record Pair(ISecond A, ISecond B);
    private sealed record Decorated(IFirst Inner) : IFirst;

    public sealed class CallsItsFuncWhenBuilt
    {
        public CallsItsFuncWhenBuilt(Depth depth, Func<NeedsTheCaller> next)
        {
            depth.Enter();
            next();
        }
    }

    public sealed class NeedsTheCaller(CallsItsFuncWhenBuilt caller)
    {
        public CallsItsFuncWhenBuilt Caller { get; } = caller;
    }

    /// <summary>A source whose stand-ins are minted afresh on every lookup — ModuleServiceProvider's shape.</summary>
    private sealed class MintsStandInsAfresh(Depth depth) : IServiceRegistrationSource
    {
        public IEnumerable<ServiceRegistration> RegistrationsFor(Type serviceType, object? serviceKey, bool explicitRegistrationsExist)
        {
            if (serviceKey is not null)
                return [];
            if (serviceType == typeof(IFirst))
                return [ServiceRegistration.ForDelegate(typeof(IFirst), null,
                    scope => { depth.Enter(); scope.GetRequiredService<ISecond>(); return new First(); })];
            if (serviceType == typeof(ISecond))
                return [ServiceRegistration.ForDelegate(typeof(ISecond), null,
                    scope => { depth.Enter(); scope.GetRequiredService<IFirst>(); return new Second(); })];
            return [];
        }
    }
}
