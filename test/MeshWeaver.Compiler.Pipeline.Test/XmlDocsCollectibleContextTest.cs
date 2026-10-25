using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using MeshWeaver.Messaging.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="XmlDocs"/> reads the docs of compiled NodeTypes, which live in COLLECTIBLE load
/// contexts laid out the way the compile pipeline lays them out: <c>{node}.dll</c> whose assembly
/// is named <c>DynamicNode_{node}</c>, with <c>DynamicNode_{node}.xml</c> beside it. Its per-assembly
/// cache must neither root such a generation (the previous reader's process-wide static cache was
/// the reason this class needed a global lock, see #613) nor serve one generation's docs to the next.
/// </summary>
public sealed class XmlDocsCollectibleContextTest : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "xmldocs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// <b>Should fail if</b> the docs cache holds the assembly (or anything reachable from it)
    /// strongly: the unloaded context is never collected.
    /// </summary>
    [Fact]
    public void ReadingDocsFromACollectibleGeneration_DoesNotKeepItAlive()
    {
        var context = ReadAndUnload("Probe", "The first generation.", out var typeSummary, out var propertySummary);
        typeSummary.Should().Be("The first generation.");
        propertySummary.Should().Be("The probe's label.");

        for (var round = 0; round < 20 && context.IsAlive; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        context.IsAlive.Should().BeFalse("the docs cache must not pin an unloaded NodeType generation");
    }

    /// <summary>
    /// A recompiled NodeType keeps its assembly NAME; the docs are keyed by the assembly instance,
    /// so each generation reads its own file. <b>Should fail if</b> the cache is keyed by name (the
    /// previous reader's key): the second generation reads the first one's summary.
    /// </summary>
    [Fact]
    public void ARecompiledGenerationWithTheSameName_ReadsItsOwnDocs()
    {
        var first = Emit("Rebuilt", "gen1", "Generation one.", out var firstContext);
        var second = Emit("Rebuilt", "gen2", "Generation two.", out var secondContext);
        first.Assembly.GetName().FullName.Should().Be(second.Assembly.GetName().FullName, "CONTROL — the same identity, two generations");

        XmlDocs.Summary(first).Should().Be("Generation one.");
        XmlDocs.Summary(second).Should().Be("Generation two.");

        firstContext.Unload();
        secondContext.Unload();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference ReadAndUnload(string node, string summary, out string typeSummary, out string propertySummary)
    {
        var type = Emit(node, "gen", summary, out var context);
        typeSummary = XmlDocs.Summary(type);
        propertySummary = XmlDocs.Summary(type.GetProperty("Label")!);
        context.Unload();
        return new WeakReference(context);
    }

    private Type Emit(string node, string generation, string summary, out AssemblyLoadContext context)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, generation)).FullName;
        var assemblyName = "DynamicNode_" + node;
        var source = $$"""
            namespace Probe;
            /// <summary>
            /// {{summary}}
            /// </summary>
            public sealed class Subject
            {
                /// <summary>The probe's label.</summary>
                public string Label { get; set; } = "";
            }
            """;
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose))],
            PlatformReferences.Platform(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var dllPath = Path.Combine(directory, node + ".dll");
        using (var dll = File.Create(dllPath))
        using (var xml = File.Create(Path.Combine(directory, assemblyName + ".xml")))
            compilation.Emit(dll, xmlDocumentationStream: xml).Success.Should().BeTrue();

        context = new AssemblyLoadContext(assemblyName + "-" + generation, isCollectible: true);
        return context.LoadFromAssemblyPath(dllPath).GetType("Probe.Subject")
            ?? throw new InvalidOperationException("Probe.Subject is not in the emitted assembly");
    }
}
