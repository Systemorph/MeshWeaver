using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using MeshWeaver.Messaging;
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
    private readonly XmlDocsReader docs = MessageHubExtensions.CreateTypeRegistry().XmlDocs();

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

        docs.Summary(first).Should().Be("Generation one.");
        docs.Summary(second).Should().Be("Generation two.");

        firstContext.Unload();
        secondContext.Unload();
    }

    /// <summary>
    /// The texts of a never-unloading assembly are interned so every mesh shares one copy; a
    /// COLLECTIBLE generation's must not be, because the runtime's intern pool would keep them for
    /// the life of the process, one more set per recompile. The summary is generated at run time, so
    /// nothing but the reader could have put it into the pool. <b>Should fail if</b> the parse
    /// interns a collectible assembly's documentation.
    /// </summary>
    [Fact]
    public void ACollectibleGenerationsDocs_AreNotInterned()
    {
        var unique = "Generated " + Guid.NewGuid().ToString("N") + ".";
        var type = Emit("Uninterned", "gen", unique, out var context);
        type.Assembly.IsCollectible.Should().BeTrue("CONTROL — the fixture emits into a collectible load context");

        var summary = docs.Summary(type);
        summary.Should().Be(unique);
        string.IsInterned(summary).Should().BeNull("a collectible generation's documentation must not enter the process-wide intern pool");

        context.Unload();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference ReadAndUnload(string node, string summary, out string typeSummary, out string propertySummary)
    {
        var type = Emit(node, "gen", summary, out var context);
        typeSummary = docs.Summary(type);
        propertySummary = docs.Summary(type.GetProperty("Label")!);
        context.Unload();
        return new WeakReference(context);
    }

    private Type Emit(string node, string generation, string summary, out AssemblyLoadContext context)
    {
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
        return XmlDocsFixtureAssembly.Emit(Path.Combine(root, generation), node, source, out context).GetType("Probe.Subject")
            ?? throw new InvalidOperationException("Probe.Subject is not in the emitted assembly");
    }
}

/// <summary>
/// Compiles C# with XML documentation into a COLLECTIBLE context, laid out like a compiled NodeType:
/// <c>{node}.dll</c> named <c>DynamicNode_{node}</c>, with <c>DynamicNode_{node}.xml</c> beside it.
/// </summary>
internal static class XmlDocsFixtureAssembly
{
    public static Assembly Emit(string directory, string node, string source, out AssemblyLoadContext context)
    {
        Directory.CreateDirectory(directory);
        var assemblyName = "DynamicNode_" + node;
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Diagnose))],
            PlatformReferences.Platform(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var dllPath = Path.Combine(directory, node + ".dll");
        using (var dll = File.Create(dllPath))
        using (var xml = File.Create(Path.Combine(directory, assemblyName + ".xml")))
        {
            var result = compilation.Emit(dll, xmlDocumentationStream: xml);
            result.Success.Should().BeTrue(string.Join("\n", result.Diagnostics));
        }

        context = new AssemblyLoadContext(assemblyName + "-" + Path.GetFileName(directory), isCollectible: true);
        return context.LoadFromAssemblyPath(dllPath);
    }
}
