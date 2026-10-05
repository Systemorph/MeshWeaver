using System.Runtime.Loader;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A keyed root registration whose KEY is a module type (the keyed-by-marker-type shape,
/// <c>AddKeyedSingleton&lt;IService&gt;(typeof(SomeModuleType), …)</c>) would pin the module's
/// collectible load context in the root for the life of the process. The key's runtime type is
/// CoreLib's <c>RuntimeType</c>, so an instance-type check alone misses it; <see cref="ModuleServices.Probe"/>
/// must name it as a blocker. The control: the same registration under a plain string key forwards.
/// </summary>
public sealed class AModuleTypeAsServiceKeyIsABlockerTest
{
    [Fact]
    public void AKeyThatIsAModuleType_IsNamedAsABlocker_AStringKeyIsNot()
    {
        var marker = Load("KeyedMarkerModule", "KeyedMarker", out var context);
        try
        {
            using (var probed = ModuleServices.Probe("KeyedMarkerModule", context,
                       [s => s.AddKeyedSingleton<IDisposable>(marker, (_, _) => new MemoryStream())], []))
            {
                probed.Blockers.Should().ContainSingle(
                    b => b.Contains("service key is a module type") && b.Contains("KeyedMarker"),
                    "a Type key from the module would be held by the root and pin the generation");
            }

            using var control = ModuleServices.Probe("KeyedMarkerModule", context,
                [s => s.AddKeyedSingleton<IDisposable>("plain-key", (_, _) => new MemoryStream())], []);
            control.Blockers.Should().BeEmpty("a platform key pins nothing — the registration forwards");
        }
        finally
        {
            context.Unload();
        }
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
