using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#5388 — the StackOverflowException that took down a portal pod (thread 0xf5 of the governed
/// dump analysis: ~395 repeats of <c>ModuleServiceProvider.ResolveAt → ModuleFallbackSource.RegistrationsFor
/// stand-in → ResolveAt</c>). The cycle needs exactly one shape: a ROOT stand-in that resolves position
/// <c>i</c> of a type in a module container which does NOT register that type itself — the state a cached
/// root stand-in for a module-declared type is in once its module was swapped to a new generation, whose
/// container holds the new generation's type and never the old one. The module's fallback then answers the
/// type from the root (the root reports it as a service, because the stand-in IS a root registration), so
/// "the registration at position i" of the module container is the module's own fallback stand-in back to
/// the root — and each hop is a fresh resolve, so no per-resolve cycle detection ever sees it.
///
/// <para>The cycle is closed BY CONSTRUCTION: <c>ResolveAt</c> counts positions among the module
/// container's OWN registrations only (<c>ModuleServiceProvider.RegistrationAt</c>), never among what its
/// fallback source adds — so a position the module does not register is refused by name. Two real
/// containers, a real registration source on the root; nothing is mocked.</para>
/// </summary>
public sealed class AStaleRootStandInIsRefusedNotRecursedTest
{
    [Fact]
    public void ARootStandInIntoAModuleThatDoesNotRegisterTheType_IsRefusedByName()
    {
        var (root, module) = RootStandingInFor(new ServiceCollection());

        // Armed: the cycle's precondition holds. The module container CAN reach a registration of the
        // type at position 0 — its fallback's stand-in for the root's registration, which is the root's
        // stand-in back into this module. Counting positions over THAT list is what recursed.
        var reachable = MeshServiceProvider.From(module)!.RegistrationsOf(typeof(IPlatformWidget));
        reachable.Should().ContainSingle("the module's fallback answers the type from the root");
        reachable[0].StandsInFor.Should().NotBeNull("and that answer is a stand-in for the root's registration");

        using var caller = root.CreateScope();
        Action resolve = () => caller.ServiceProvider.GetRequiredService<IPlatformWidget>();

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*has no registration at position 0 in this module container*",
                "a position the module does not register is refused by name — never answered by the module's own "
                + "fallback, which leads straight back to the root stand-in that asked");
    }

    [Fact]
    public void WithNoCaller_TheSameRoad_IsRefusedByName()
    {
        var (_, module) = RootStandingInFor(new ServiceCollection());

        Action resolve = () => ModuleServiceProvider.ResolveAt(module, typeof(IPlatformWidget), 0);

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*has no registration at position 0 in this module container*");
    }

    /// <summary>
    /// The negative control: the same root stand-in, into a module that DOES register the type, answers
    /// the module's own instance — the road itself is sound; only a position the module does not hold is
    /// refused.
    /// </summary>
    [Fact]
    public void ARootStandInIntoAModuleThatRegistersTheType_AnswersTheModulesInstance()
    {
        var (root, _) = RootStandingInFor(new ServiceCollection().AddScoped<IPlatformWidget, ModuleWidget>());

        using var caller = root.CreateScope();
        var widget = caller.ServiceProvider.GetRequiredService<IPlatformWidget>();

        widget.Should().BeOfType<ModuleWidget>("the root stand-in resolves the module's own registration at position 0");
        caller.ServiceProvider.GetRequiredService<IPlatformWidget>().Should().BeSameAs(widget,
            "a scoped registration is the caller's one instance, in the module scope it owns");
    }

    // A root whose registration source stands in for IPlatformWidget with position 0 of `module` — what
    // ModuleOwnedRootSource hands the root for a module-declared type — and the module container itself,
    // whose fallback reaches the root for every type it does not register and that does not name it.
    private static (IServiceProvider Root, IServiceProvider Module) RootStandingInFor(IServiceCollection moduleServices)
    {
        var source = new StandInToModule();
        var root = new ServiceCollection()
            .AddSingleton<IServiceRegistrationSource>(source)
            .CreateMeshWeaverServiceProvider();
        var module = ModuleServiceProvider.CreateForCallers(moduleServices, root, _ => false, [], _ => []);
        source.Module = module;
        return (root, module);
    }

    /// <summary>The root's stand-in for a module's registration, by position (ModuleOwnedRootSource's shape).</summary>
    private sealed class StandInToModule : IServiceRegistrationSource
    {
        public IServiceProvider? Module { get; set; }

        public IEnumerable<ServiceRegistration> RegistrationsFor(Type serviceType, object? serviceKey, bool explicitRegistrationsExist) =>
            serviceType != typeof(IPlatformWidget) || serviceKey is not null || Module is not { } module
                ? []
                : ModuleServiceProvider.StandIns(serviceType, null,
                    [caller => ModuleServiceProvider.ResolveAt(module, serviceType, 0, caller)],
                    ModuleServiceProvider.CallerOfRootScope);
    }

    /// <summary>A platform-side service type a module may implement.</summary>
    public interface IPlatformWidget;

    private sealed class ModuleWidget : IPlatformWidget;
}
