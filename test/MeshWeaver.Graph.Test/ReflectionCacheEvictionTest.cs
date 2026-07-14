using System.Runtime.CompilerServices;
using Autofac;
using Autofac.Core;
using MeshWeaver.Graph.Configuration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the fix for the concurrent-hub-construction SIGSEGV: a collectible
/// <see cref="NodeAssemblyLoadContext"/> whose types were activated through Autofac must be
/// GC-collectable once unloaded. Autofac's process-static reflection cache keys on
/// <c>ConstructorInfo</c>, which strongly roots the assembly (and its load context); if the
/// cache isn't evicted on unload, the context leaks AND a later concurrent cache probe
/// dereferences the freed metadata → <see cref="System.AccessViolationException"/>.
/// This asserts the (deterministic) root cause — a retained reference — not the flaky crash.
/// </summary>
public class ReflectionCacheEvictionTest
{
    private const string WidgetSource =
        "namespace Dyn { public sealed class Widget { public Widget() { } } }";

    [Fact]
    public void UnloadedNodeContext_IsCollectible_AfterAutofacActivation()
    {
        // Pin the shared reflection cache alive for the whole test. It is held only by a
        // WeakReference, so without this a plain GC would collect the entire cache and release the
        // widget ctor on its own — masking the leak. A running app keeps the cache alive through
        // constant container/scope activity; that is exactly when a retained stale key both roots
        // the context (leak) and, on a concurrent probe, dereferences freed metadata (SIGSEGV).
        var pinnedCache = ReflectionCacheSet.Shared;

        var weak = ActivateThroughAutofacThenUnload();

        for (var i = 0; i < 15 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        weak.IsAlive.Should().BeFalse(
            "a collectible node AssemblyLoadContext must be collectable once unloaded; if Autofac's " +
            "process-static reflection cache still holds the widget's ConstructorInfo it roots the " +
            "context forever (leak) AND a later concurrent GetOrAdd bucket-probe would dereference the " +
            "freed metadata → AccessViolationException. NodeAssemblyLoadContext must evict the shared " +
            "reflection cache before Unload.");

        GC.KeepAlive(pinnedCache);
    }

    // Kept out-of-line + non-inlined so no local (assembly / type / instance) stays rooted on the
    // caller's frame — otherwise the context could not be collected regardless of the fix.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ActivateThroughAutofacThenUnload()
    {
        var bytes = CompileWidget();
        var ctx = new NodeAssemblyLoadContext("EvictionTestWidget", dllPath: null, logger: null);
        var assembly = ctx.LoadFromBytes(bytes, null);
        var widgetType = assembly.GetType("Dyn.Widget")!;

        // Activate through Autofac exactly as a hosted-hub container would: this creates a
        // ConstructorBinder for the widget's ctor and caches it in ReflectionCacheSet.Shared.
        var builder = new ContainerBuilder();
        builder.RegisterType(widgetType);
        using (var container = builder.Build())
        {
            container.Resolve(widgetType).Should().NotBeNull();
        }

        var weak = new WeakReference(ctx);
        ctx.Dispose(); // → Unloading → ReflectionCacheEviction.EvictFor → Unload
        return weak;
    }

    private static byte[] CompileWidget()
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = tpa
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "EvictionTestWidget",
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
