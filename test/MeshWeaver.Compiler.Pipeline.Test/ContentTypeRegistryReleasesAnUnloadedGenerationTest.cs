using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using MeshWeaver.Mesh.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The mesh-wide content-type registry must let go of a collectible generation when it unloads.
/// Measured: after a live swap of the real MeshWeaver.AI, a heap dump showed
/// <see cref="MeshContentTypeRegistry"/>'s discriminator map as the ONLY strong root of the swapped-out
/// generation — its content types were registered (by name and by NodeType path) and nothing
/// re-registered them. The registry now evicts, on the context's <c>Unloading</c>, exactly the entries
/// whose type belongs to it. The negative control: a type from a context that is NOT unloaded stays
/// resolvable.
/// </summary>
public sealed class ContentTypeRegistryReleasesAnUnloadedGenerationTest
{
    [Fact]
    public void AnUnloadedGenerationsContentTypes_AreEvicted_AndTheGenerationIsCollected()
    {
        var registry = new MeshContentTypeRegistry();
        var kept = Load("MeshWeaver.Test.KeptContent", "KeptContent", out var keptContext);
        registry.Register(kept, "type/Kept");

        var weak = RegisterAndUnload(registry);
        for (var round = 0; round < 10 && weak.IsAlive; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        weak.IsAlive.Should().BeFalse("the registry must not pin the unloaded generation");
        registry.TryResolveByDiscriminator("EvictedContent", out _).Should().BeFalse();
        registry.TryResolveByNodeType("type/Evicted", out _).Should().BeFalse();
        registry.TryResolveByDiscriminator("KeptContent", out var stillThere).Should().BeTrue(
            "the control: a type whose context is NOT unloading keeps its claim");
        stillThere.Should().BeSameAs(kept);
        GC.KeepAlive(keptContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterAndUnload(MeshContentTypeRegistry registry)
    {
        var type = Load("MeshWeaver.Test.EvictedContent", "EvictedContent", out var context);
        registry.Register(type, "type/Evicted");
        registry.TryResolveByDiscriminator("EvictedContent", out _).Should().BeTrue();
        context.Unload();
        return new WeakReference(context);
    }

    private static Type Load(string assemblyName, string typeName, out AssemblyLoadContext context)
    {
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText($"public sealed record {typeName}(string Text);")],
            PlatformReferences.Platform(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        compilation.Emit(buffer).Success.Should().BeTrue();
        buffer.Position = 0;
        context = new AssemblyLoadContext(assemblyName, isCollectible: true);
        return context.LoadFromStream(buffer).GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} is not in the emitted {assemblyName}");
    }
}
