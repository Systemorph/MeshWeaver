using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6584 — the road from one module's container to ANOTHER module's scoped registration
/// (<c>ModuleFallbackSource</c>'s stand-ins) carries the caller too: module A's scoped service that takes
/// module B's scoped type gets, in each caller's module scope, THAT caller's instance of B — built in B's
/// module scope the same caller owns, against the caller's own root state — and each caller's end disposes
/// its own instances exactly once. Two real module containers and a real root; nothing is mocked.
/// </summary>
public sealed class AnotherModulesScopedServiceIsTheCallersTest
{
    [Fact]
    public void ModuleAsScopedService_GetsModuleBsScopedInstance_OfItsOwnCaller()
    {
        var root = new ServiceCollection()
            .AddScoped<CircuitBoundState>()
            .CreateMeshWeaverServiceProvider();
        var moduleB = ModuleServiceProvider.Create(
            new ServiceCollection().AddScoped<ModuleBDialog>(), root, _ => false, []);
        // Module A asks for B's type: the registrations elsewhere are B's, resolved for the caller A's
        // module scope serves — exactly what ModuleServices.RegistrationsElsewhereForCallers hands it.
        var moduleA = ModuleServiceProvider.CreateForCallers(
            new ServiceCollection().AddScoped<ModuleAPanel>(), root, t => t == typeof(ModuleBDialog), [],
            type => type == typeof(ModuleBDialog)
                ? [caller => ModuleServiceProvider.ResolveAt(moduleB, typeof(ModuleBDialog), 0, caller)]
                : []);

        var first = root.CreateScope();
        var second = root.CreateScope();
        var firstState = Initialized(first);
        var secondState = Initialized(second);

        var firstPanel = ModuleServiceProvider.InCallerScope(moduleA, CallerOf(first)).GetRequiredService<ModuleAPanel>();
        var secondPanel = ModuleServiceProvider.InCallerScope(moduleA, CallerOf(second)).GetRequiredService<ModuleAPanel>();

        firstPanel.Dialog.State.Should().BeSameAs(firstState,
            "module B's scoped instance reached from module A is built against the first caller's own root state");
        secondPanel.Dialog.State.Should().BeSameAs(secondState, "and the second caller's against its own");
        firstPanel.Dialog.Should().NotBeSameAs(secondPanel.Dialog, "each caller has its own instance of module B's scoped service");
        firstPanel.Dialog.Should().BeSameAs(
            ModuleServiceProvider.InCallerScope(moduleB, CallerOf(first)).GetRequiredService<ModuleBDialog>(),
            "it is the instance in module B's scope the first caller owns — one per caller, whichever road reaches it");

        first.Dispose();

        firstState.Released.Should().Be(1, "the first caller's end disposed its instance of module B's service exactly once");
        secondState.Released.Should().Be(0, "and nothing of the second caller's");
        second.Dispose();
        secondState.Released.Should().Be(1);
    }

    /// <summary>
    /// The control: the same road with NO caller lands in module B's own root — one instance for everyone,
    /// built against a root child scope no caller initialised, which throws exactly as an uninitialised
    /// circuit's navigation does. It is what the caller-carrying road above exists to avoid.
    /// </summary>
    [Fact]
    public void WithoutACaller_TheSameRoad_BuildsAgainstStateNoCallerInitialised()
    {
        var root = new ServiceCollection()
            .AddScoped<CircuitBoundState>()
            .CreateMeshWeaverServiceProvider();
        var moduleB = ModuleServiceProvider.Create(
            new ServiceCollection().AddScoped<ModuleBDialog>(), root, _ => false, []);
        var moduleA = ModuleServiceProvider.CreateForCallers(
            new ServiceCollection().AddScoped<ModuleAPanel>(), root, t => t == typeof(ModuleBDialog), [],
            type => type == typeof(ModuleBDialog)
                ? [caller => ModuleServiceProvider.ResolveAt(moduleB, typeof(ModuleBDialog), 0, caller)]
                : []);

        Action resolve = () => moduleA.GetRequiredService<ModuleAPanel>();

        resolve.Should().Throw<Exception>("with no caller the dependency is built against state no caller initialised");
    }

    private static CircuitBoundState Initialized(IServiceScope scope)
    {
        var state = scope.ServiceProvider.GetRequiredService<CircuitBoundState>();
        state.Initialize();
        return state;
    }

    private static MeshServiceProvider CallerOf(IServiceScope scope) =>
        MeshServiceProvider.From(scope.ServiceProvider)!;

    /// <summary>Module B's scoped service over circuit state, released when its scope ends.</summary>
    private sealed class ModuleBDialog : IDisposable
    {
        public ModuleBDialog(CircuitBoundState state)
        {
            state.Subscribe();
            State = state;
        }

        public CircuitBoundState State { get; }

        public void Dispose() => State.Release();
    }

    /// <summary>Module A's scoped service, taking module B's scoped type.</summary>
    private sealed class ModuleAPanel(ModuleBDialog dialog)
    {
        public ModuleBDialog Dialog { get; } = dialog;
    }
}
