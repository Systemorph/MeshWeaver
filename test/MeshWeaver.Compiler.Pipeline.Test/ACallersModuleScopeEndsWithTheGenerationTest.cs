using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6584 — the module scope a CALLER owns (<see cref="ModuleServiceProvider.InCallerScope"/>)
/// ends with whichever ends first: the caller, or the module generation. A caller that outlives a
/// generation — a long-lived hub scope across a live swap — must not keep that generation's instances
/// alive, so the generation's end disposes every caller's module scope and lets go of it; the caller's
/// own end afterwards finds nothing left to do. Real containers, nothing mocked.
/// </summary>
public sealed class ACallersModuleScopeEndsWithTheGenerationTest
{
    [Fact]
    public void TheGenerationEndingFirst_EndsTheCallersModuleScope_AndTheCallerEndsCleanly()
    {
        var root = new ServiceCollection()
            .AddScoped<CircuitBoundState>()
            .CreateMeshWeaverServiceProvider();
        var module = ModuleServiceProvider.Create(
            new ServiceCollection().AddScoped<ModuleDialog>(), root, _ => false, []);
        var caller = root.CreateScope();
        var state = caller.ServiceProvider.GetRequiredService<CircuitBoundState>();
        state.Initialize();
        var callerScope = MeshServiceProvider.From(caller.ServiceProvider)!;

        var dialog = ModuleServiceProvider.InCallerScope(module, callerScope).GetRequiredService<ModuleDialog>();

        dialog.State.Should().BeSameAs(state, "the module scope's root dependencies are the caller's own");
        ModuleServiceProvider.InCallerScope(module, callerScope).GetRequiredService<ModuleDialog>()
            .Should().BeSameAs(dialog, "one caller owns one module scope");

        ((IDisposable)module).Dispose();

        state.Released.Should().Be(1, "the generation ended first, and took the caller's module scope with it");
        caller.Dispose();
        state.Released.Should().Be(1, "the caller's end finds its module scope already ended — nothing is disposed twice");
    }

    /// <summary>The control: with the generation alive, the caller's end is what ends its module scope.</summary>
    [Fact]
    public void TheCallerEndingFirst_EndsItsModuleScope_AndTheGenerationServesOn()
    {
        var root = new ServiceCollection()
            .AddScoped<CircuitBoundState>()
            .CreateMeshWeaverServiceProvider();
        using var module = (IDisposable)ModuleServiceProvider.Create(
            new ServiceCollection().AddScoped<ModuleDialog>(), root, _ => false, []);
        var caller = root.CreateScope();
        var state = caller.ServiceProvider.GetRequiredService<CircuitBoundState>();
        state.Initialize();
        ModuleServiceProvider.InCallerScope((IServiceProvider)module, MeshServiceProvider.From(caller.ServiceProvider)!)
            .GetRequiredService<ModuleDialog>();

        state.Released.Should().Be(0);
        caller.Dispose();

        state.Released.Should().Be(1, "the module scope a caller owns ends with the caller");
        using var next = root.CreateScope();
        next.ServiceProvider.GetRequiredService<CircuitBoundState>().Initialize();
        ModuleServiceProvider.InCallerScope((IServiceProvider)module, MeshServiceProvider.From(next.ServiceProvider)!)
            .GetRequiredService<ModuleDialog>().State
            .Should().BeSameAs(next.ServiceProvider.GetRequiredService<CircuitBoundState>(), "the generation serves the next caller");
    }

    /// <summary>A module's scoped service over circuit state, released when its scope ends.</summary>
    private sealed class ModuleDialog : IDisposable
    {
        public ModuleDialog(CircuitBoundState state)
        {
            state.Subscribe();
            State = state;
        }

        public CircuitBoundState State { get; }

        public void Dispose() => State.Release();
    }
}
