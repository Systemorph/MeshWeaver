using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The container's own contract (<see cref="MeshServiceProvider"/>, MeshWeaver#7145) — the behaviours the
/// platform relied on from the container it replaced and that the standard provider does not have, each
/// pinned on a real container: a level's lookup order and lifetimes, the implicit relationship types,
/// ownership, cycle refusal, and the fault that is never cached. The standard
/// <c>Microsoft.Extensions.DependencyInjection</c> specification suite was run against the same container
/// (every case passes except the deliberate differences documented in
/// <c>Doc/Architecture/HubServiceLayers</c>: hierarchical scopes, a level's root-owned singletons, and one
/// AnyKey enumeration-caching detail).
/// </summary>
public class MeshServiceProviderTest
{
    public interface IValidator { string Name { get; } }
    public sealed record NamedValidator(string Name) : IValidator;
    public sealed class TakesValidators(IEnumerable<Lazy<IValidator>> validators)
    {
        public IReadOnlyList<Lazy<IValidator>> Validators { get; } = validators.ToArray();
    }
    public sealed class TakesList(IReadOnlyList<IValidator> validators) { public int Count => validators.Count; }
    public sealed class TakesFunc(Func<IValidator> next) { public IValidator Next => next(); }
    public sealed class SelfCycle(SelfCycleDependency dependency) { public SelfCycleDependency Dependency { get; } = dependency; }
    public sealed class SelfCycleDependency(SelfCycle cycle) { public SelfCycle Cycle { get; } = cycle; }
    public sealed class FlakyCounter { public int Attempts; }
    public sealed class Flaky
    {
        public Flaky(FlakyCounter counter)
        {
            if (Interlocked.Increment(ref counter.Attempts) == 1)
                throw new InvalidOperationException("the first construction fails");
        }
    }

    private static IServiceProvider Root(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.CreateMeshWeaverServiceProvider();
    }

    /// <summary>
    /// 🚨 <c>IEnumerable&lt;Lazy&lt;T&gt;&gt;</c> is one deferred element per registration of <c>T</c> — the
    /// node-validator set every query provider takes. Read as "registrations of <c>Lazy&lt;T&gt;</c>" it is
    /// EMPTY, and every validator — row-level security among them — is silently skipped; that is how this
    /// test was found (<c>AModuleNodeValidatorIsNeverBuiltForNoCallerTest</c> went red on it).
    /// </summary>
    [Fact]
    public void ACollectionOfDeferredServices_IsOnePerRegistration_AcrossLevels_BuiltOnlyWhenAsked()
    {
        var built = 0;
        var root = Root(s => s
            .AddSingleton<IValidator>(_ => { Interlocked.Increment(ref built); return new NamedValidator("root"); })
            .AddScoped<TakesValidators>());
        var level = new ServiceCollection()
            .AddSingleton<IValidator>(_ => { Interlocked.Increment(ref built); return new NamedValidator("hub"); })
            .SetupModules(root);

        var taker = level.GetRequiredService<TakesValidators>();

        taker.Validators.Should().HaveCount(2, "one deferred element per registration, the root's and the level's");
        built.Should().Be(0, "a deferred element builds nothing until it is asked");
        taker.Validators.Select(v => v.Value.Name).Should().Equal(new[] { "root", "hub" }, "parent first, then the level's own");
        built.Should().Be(2);
    }

    [Fact]
    public void TheCollectionShapes_AndADeferredFactory_AreAnsweredWithoutARegistration()
    {
        var root = Root(s => s
            .AddSingleton<IValidator>(new NamedValidator("a"))
            .AddSingleton<IValidator>(new NamedValidator("b"))
            .AddTransient<TakesList>()
            .AddTransient<TakesFunc>());

        root.GetRequiredService<TakesList>().Count.Should().Be(2);
        root.GetRequiredService<TakesFunc>().Next.Name.Should().Be("b", "a single resolve answers the last registration");
        root.GetRequiredService<IEnumerable<string>>().Should().BeEmpty("an enumeration of nothing registered is empty, not null");
    }

    [Fact]
    public void ACycle_IsRefusedByName_NotRecursedUntilTheStackOverflows()
    {
        var root = Root(s => s.AddSingleton<SelfCycle>().AddSingleton<SelfCycleDependency>());

        ((Action)(() => root.GetRequiredService<SelfCycle>())).Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("circular dependency");
    }

    [Fact]
    public void ACycle_ThroughAFactory_IsRefusedToo()
    {
        var root = Root(s => s.AddSingleton<FlakyCounter>(sp => sp.GetRequiredService<FlakyCounter>()));

        ((Action)(() => root.GetRequiredService<FlakyCounter>())).Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("circular dependency");
    }

    /// <summary>A shared instance whose construction faulted is NOT cached: the next resolve builds afresh
    /// (a transient fault must never become a permanent one).</summary>
    [Fact]
    public void AFaultedSingletonBuild_IsNotCached_TheNextResolveBuildsAfresh()
    {
        var counter = new FlakyCounter();
        var root = Root(s => s.AddSingleton(counter).AddSingleton<Flaky>());

        ((Action)(() => root.GetRequiredService<Flaky>())).Should().Throw<InvalidOperationException>();
        root.GetRequiredService<Flaky>().Should().BeSameAs(root.GetRequiredService<Flaky>());
        counter.Attempts.Should().Be(2, "one failed attempt, one that succeeded and is now the singleton");
    }

    [Fact]
    public void AFactoryReturningNull_IsRefused_NotAnsweredAsAbsent()
    {
        var root = Root(s => s.AddSingleton<IValidator>(_ => null!));

        ((Action)(() => root.GetService(typeof(IValidator)))).Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("returned null");
    }

    [Fact]
    public void AKeyedRegistrationMissing_NamesTheKeyAndTheType()
    {
        var root = Root(_ => { });

        ((Action)(() => root.GetRequiredKeyedService(typeof(IValidator), "mw-module-service:10")))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Be(
                $"The requested service 'mw-module-service:10 ({typeof(IValidator).FullName})' has not been registered.",
                "the module-lease alarm parses this exact phrase (ModuleForwarderHealthCheck, the Hosting observation queries)");
    }

    /// <summary>An ended scope refuses every service it can answer, and answers a service nobody registered
    /// with null, as before — teardown code probes optional services on a dying hub.</summary>
    [Fact]
    public void AnEndedScope_RefusesARegisteredService_AndAnswersAnUnregisteredOneWithNull()
    {
        var root = Root(s => s.AddSingleton<IValidator>(new NamedValidator("a")));
        var scope = root.CreateScope();
        scope.Dispose();

        ((Action)(() => scope.ServiceProvider.GetService(typeof(IValidator)))).Should().Throw<ObjectDisposedException>()
            .Which.ObjectName.Should().Be(MeshServiceProvider.DisposedObjectName);
        scope.ServiceProvider.GetService(typeof(TakesList)).Should().BeNull("nothing registered it");
    }

    /// <summary>A scope begun from a hub level sees the level's registrations — scopes are hierarchical,
    /// as the platform's hubs need them to be.</summary>
    [Fact]
    public void AScopeBegunFromALevel_SeesTheLevelsRegistrations()
    {
        var root = Root(s => s.AddScoped<TakesList>());
        var level = new ServiceCollection().AddSingleton<IValidator>(new NamedValidator("hub")).SetupModules(root);

        using var scope = level.CreateScope();
        scope.ServiceProvider.GetRequiredService<TakesList>().Count.Should().Be(1);
        root.CreateScope().ServiceProvider.GetRequiredService<TakesList>().Count.Should().Be(0,
            "the root's own scopes do not see a level's registrations");
    }
}
