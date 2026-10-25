using System.Runtime.CompilerServices;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.ServiceProvider;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A collectible <see cref="NodeAssemblyLoadContext"/> whose types were activated through a hub's
/// container must be collectable once the hub is gone and the context unloaded — while the mesh's ROOT
/// container lives on. Two ways a container can root such a context, both pinned here:
/// <list type="number">
/// <item>a process-static reflection cache keyed by <c>ConstructorInfo</c> (the container this replaced
/// kept one, and the context leaked unless it was evicted by hand on unload) — <see cref="MeshServiceProvider"/>
/// keeps every plan on the registration it belongs to, so there is nothing to evict;</item>
/// <item>a ROOT singleton closed over the collectible type (an open generic such as <c>ILogger&lt;&gt;</c>
/// asked for with a NodeType's type) — held by the level that asked, never by the root.</item>
/// </list>
/// The negative control resolves the same closure AT the root, where it is the root's to hold: the
/// context is then retained — proof the instrument sees a retention when there is one.
/// </summary>
public class ACollectibleTypeIsNeverHeldByTheRootTest
{
    private const string WidgetSource =
        "namespace Dyn { public sealed class Widget { public Widget() { } } }";

    /// <summary>A root open generic — the <c>ILogger&lt;T&gt;</c> shape.</summary>
    public sealed class Holder<T> { }

    [Fact]
    public void UnloadedNodeContext_IsCollectible_AfterActivationThroughAHubLevel()
    {
        var root = RootWithOpenGeneric();

        var weak = ActivateThroughALevelThenUnload(root, resolveAtTheRoot: false);
        Collect(weak);

        weak.IsAlive.Should().BeFalse(
            "a collectible node context must be collectable once its hub level ended and it unloaded, while the "
            + "root container lives on: nothing the root holds may name one of its types");
        GC.KeepAlive(root);
    }

    [Fact]
    public void NegativeControl_AClosureResolvedAtTheRoot_IsRetainedByTheRoot()
    {
        var root = RootWithOpenGeneric();

        var weak = ActivateThroughALevelThenUnload(root, resolveAtTheRoot: true);
        Collect(weak);

        weak.IsAlive.Should().BeTrue(
            "resolved AT the root, a singleton closed over the collectible type is the root's own — and roots the "
            + "context; this is what the instrument must see, or the positive test above checked nothing");
        GC.KeepAlive(root);
    }

    /// <summary>A root open generic whose closures the root itself caches — a transient asked for AT the root.</summary>
    public sealed class Transient<T> { }

    /// <summary>
    /// The root caches each closure of an open generic for reuse, and that cache must never pin what it
    /// closed over: a transient closed over the collectible type and resolved AT the root leaves its closure
    /// (closed type, implementation type, constructor plan) on the root's registration for the root's whole
    /// life — keyed weakly, so the context still unloads.
    /// </summary>
    [Fact]
    public void UnloadedNodeContext_IsCollectible_AfterTheRootClosedAnOpenGenericOverIt()
    {
        var root = new ServiceCollection().AddTransient(typeof(Transient<>), typeof(Transient<>)).CreateMeshWeaverServiceProvider();

        var weak = CloseAtTheRootThenUnload(root);
        Collect(weak);

        weak.IsAlive.Should().BeFalse(
            "the root's cache of closed registrations is keyed weakly by the closed type: it must not keep the "
            + "collectible context alive once nothing else names its types");
        GC.KeepAlive(root);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CloseAtTheRootThenUnload(IServiceProvider root)
    {
        var bytes = CompileWidget();
        var ctx = new NodeAssemblyLoadContext("CollectibleWidgetClosure", dllPath: null, logger: null);
        var widgetType = ctx.LoadFromBytes(bytes, null).GetType("Dyn.Widget")!;
        var closedType = typeof(Transient<>).MakeGenericType(widgetType);

        root.GetRequiredService(closedType).Should().NotBeSameAs(root.GetRequiredService(closedType),
            "a transient: the root caches the closure, never the instance");

        var weak = new WeakReference(ctx);
        ctx.Dispose(); // → Unload
        return weak;
    }

    private static IServiceProvider RootWithOpenGeneric() =>
        new ServiceCollection().AddSingleton(typeof(Holder<>), typeof(Holder<>)).CreateMeshWeaverServiceProvider();

    private static void Collect(WeakReference weak)
    {
        for (var i = 0; i < 15 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    // Kept out-of-line + non-inlined so no local (assembly / type / instance) stays rooted on the
    // caller's frame — otherwise the context could not be collected regardless of the container.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ActivateThroughALevelThenUnload(IServiceProvider root, bool resolveAtTheRoot)
    {
        var bytes = CompileWidget();
        var ctx = new NodeAssemblyLoadContext("CollectibleWidget", dllPath: null, logger: null);
        var assembly = ctx.LoadFromBytes(bytes, null);
        var widgetType = assembly.GetType("Dyn.Widget")!;
        var holderType = typeof(Holder<>).MakeGenericType(widgetType);

        // Activate exactly as a hosted hub would: the hub's own registration of the NodeType's type, and a
        // root open generic closed over it.
        var level = new ServiceCollection().AddTransient(widgetType).SetupModules(root);
        level.GetRequiredService(widgetType).Should().NotBeNull();
        level.GetRequiredService(holderType).Should().BeSameAs(level.GetRequiredService(holderType),
            "a singleton closed over a collectible type is one instance in the level that asked");
        if (resolveAtTheRoot)
            root.GetRequiredService(holderType).Should().NotBeNull();
        ((IDisposable)level).Dispose();

        var weak = new WeakReference(ctx);
        ctx.Dispose(); // → Unload
        return weak;
    }

    private static byte[] CompileWidget()
    {
        // The process-wide platform set, read once — see PlatformReferences for why (#4127).
        var references = PlatformReferences.Platform();

        var compilation = CSharpCompilation.Create(
            "CollectibleWidget",
            new[] { CSharpSyntaxTree.ParseText(WidgetSource) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        result.Success.Should().BeTrue(
            "the widget must compile: " +
            string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return ms.ToArray();
    }
}
