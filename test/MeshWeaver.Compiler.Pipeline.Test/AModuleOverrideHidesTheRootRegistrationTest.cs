using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6556 / #6542 — <b>the scoped-dependency walk sees what the module container resolves,
/// and nothing it cannot reach.</b> A module that registers its OWN singleton <c>T</c> never reaches the
/// root's <c>T</c> (the module's fallback consults the root only for a type the module does not
/// register). A root-SCOPED <c>T</c> the module overrides must therefore not stop a root-reachable
/// module singleton that takes <c>T</c> from being built before the module is published — left to its
/// first resolve, it reopens the #6542 lock inversion.
///
/// <para>Real containers, nothing mocked: a root built as the mesh builds it and a module container
/// built by <see cref="ModuleServiceProvider"/>.</para>
/// </summary>
public sealed class AModuleOverrideHidesTheRootRegistrationTest
{
    [Fact]
    public void AModuleSingletonOverAModuleOwnedOverride_OfARootScopedService_IsBuiltBeforePublish()
    {
        var (built, module) = Build(moduleOverrides: true);

        built.Count.Should().Be(1,
            "the module's singleton takes the module's OWN Overridden singleton — the root's scoped registration is unreachable and decides nothing");
        module.GetRequiredService<ReachableSingleton>().Dependency.Origin.Should().Be("module");
        built.Count.Should().Be(1, "the singleton built before publish is the one served");
    }

    /// <summary>
    /// The negative control: the same module WITHOUT its own <c>Overridden</c> reaches the root's SCOPED
    /// registration through its fallback, so its singleton is left to its first resolve (#6556).
    /// </summary>
    [Fact]
    public void WithoutTheOverride_TheRootScopedServiceIsReached_AndTheSingletonIsNotBuiltAtPublish()
    {
        var (built, _) = Build(moduleOverrides: false);

        built.Count.Should().Be(0, "the singleton reaches a root-SCOPED service, which is never built at boot");
    }

    private static (BuildCounter Built, IServiceProvider Module) Build(bool moduleOverrides)
    {
        var built = new BuildCounter();
        var root = new ServiceCollection()
            .AddSingleton(built)
            .AddScoped(_ => new Overridden("root"))
            .CreateMeshWeaverServiceProvider();

        var moduleServices = new ServiceCollection();
        if (moduleOverrides)
            moduleServices.AddSingleton(new Overridden("module"));
        moduleServices.AddSingleton<ReachableSingleton>();
        var module = ModuleServiceProvider.Create(moduleServices, root, _ => false, []);

        ModuleServiceProvider.BuildRootReachableSingletons(
            module, root, (type, key) => key is null && type == typeof(ReachableSingleton));
        return (built, module);
    }

    /// <summary>Counts constructions of <see cref="ReachableSingleton"/> — a root singleton, so the
    /// module's fallback hands every build the same counter.</summary>
    public sealed class BuildCounter
    {
        private int count;

        /// <summary>How many were built.</summary>
        public int Count => Volatile.Read(ref count);

        /// <summary>Records one build.</summary>
        public void Built() => Interlocked.Increment(ref count);
    }

    /// <summary>Registered SCOPED in the root and — in the override case — as the module's own singleton.</summary>
    /// <param name="Origin">Which container built it.</param>
    public sealed record Overridden(string Origin);

    /// <summary>A module singleton the root reaches, taking <see cref="Overridden"/>.</summary>
    public sealed class ReachableSingleton
    {
        /// <summary>Built with the dependency the module container answers.</summary>
        public ReachableSingleton(Overridden dependency, BuildCounter counter)
        {
            Dependency = dependency;
            counter.Built();
        }

        /// <summary>The dependency it was built with.</summary>
        public Overridden Dependency { get; }
    }
}
