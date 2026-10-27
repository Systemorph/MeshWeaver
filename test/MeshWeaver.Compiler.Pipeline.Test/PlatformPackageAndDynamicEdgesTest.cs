using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// The edge kinds that make a CORE carve-out answerable from data
/// (<c>Doc/Architecture/DependencyGraph</c> → <i>The three evidence classes</i>):
/// <see cref="DependencyEdgeKinds.Platform"/> (a used <c>MeshWeaver.*</c> host assembly),
/// <see cref="DependencyEdgeKinds.Package"/> (a used <c>#r "nuget:"</c> assembly),
/// <see cref="DependencyEdgeKinds.Dynamic"/> (a reflection call with a constant target) and
/// <see cref="DependencyEdgeKinds.DynamicUnresolved"/> (one without).
///
/// <para>Every positive case has its negative control: a platform assembly referenced but not
/// bound, a package referenced but not bound, a BCL assembly bound (no edge), <c>x.GetType()</c>
/// and <c>Activator.CreateInstance(typeof(T))</c> (static, not by name), and a reflection call
/// inside FOREIGN code (the foreign owner's edge, not this type's).</para>
/// </summary>
public class PlatformPackageAndDynamicEdgesTest
{
    private const string A = "Lib/A";
    private const string B = "App/B";

    private static MeshNode TypeNode(string path) => MeshNode.FromPath(path) with
    {
        Name = path[(path.LastIndexOf('/') + 1)..],
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new NodeTypeDefinition(),
    };

    private static MeshNode Code(string ownerPath, string id, string code) => new(id, $"{ownerPath}/Source")
    {
        NodeType = "Code",
        Name = id,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new CodeConfiguration { Language = "csharp", Code = code },
    };

    /// <summary>Compiles <see cref="B"/> through the build-process bake — the same extraction the
    /// runtime compile runs — and returns its edge set.</summary>
    private static DependencyEdgeSet CompileB(System.Threading.CancellationToken ct, MeshNode[] nodes, IReadOnlyList<string>? sources = null)
    {
        var output = Path.Combine(Path.GetTempPath(), "mw-depedges-dyn-" + Guid.NewGuid().ToString("N"));
        try
        {
            return NodeSetCompiler.CompileWithModules(
                NodeSet.Create([TypeNode(B), .. nodes]), TypeNode(B),
                sources, tests: null, configuration: null, contentCollections: null,
                references: CompileReferences.Default,
                dependencyIdOf: _ => null,
                toolchainId: NodeTypeCompilationHelpers.ProcessToolchainId,
                outputDirectory: output,
                moduleAssemblyPaths: null,
                ct: ct).DependencyEdges!;
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static DependencyEdge Single(DependencyEdgeSet set, string kind) =>
        set.Edges.Should().ContainSingle(e => e.Kind == kind, $"exactly one {kind} edge").Which;

    [Fact(Timeout = 120_000)]
    public void TheHomeAppSourceShape_IsADynamicEdgeIntoThePlatform_WithTheCallSiteAsEvidence()
    {
        // The real shape: MeshWeaver.Plugins AppleMaps' AmapsConnect resolves a Graph type by its
        // assembly-qualified name — nothing the compiler binds, so no static edge can see it.
        var set = CompileB(TestContext.Current.CancellationToken, [Code(B, "Connect", """
            /// <summary>Resolves the home-app source by name.</summary>
            public static class Connect
            {
                /// <summary>The type, or null.</summary>
                public static System.Type? Home() =>
                    System.Type.GetType("MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph");
            }
            """)]);

        var edge = Single(set, DependencyEdgeKinds.Dynamic);
        edge.Target.Should().Be("MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph");
        edge.TargetKind.Should().Be(DependencyEdgeKinds.Platform, "MeshWeaver.Graph is a platform assembly of this host");
        edge.EvidenceClass.Should().Be(DependencyEvidenceClasses.Dynamic);
        edge.Evidence.Should().Equal(
            ["Type.GetType(\"MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph\") in Connect"]);
        set.Edges.Should().NotContain(e => e.Kind == DependencyEdgeKinds.DynamicUnresolved);
        set.Edges.Select(e => e.Target).Should().OnlyHaveUniqueItems("targets are unique within a set");

        // Negative control on the STATIC side: the same type without the reflection call has the
        // SAME static edges — the string binds nothing, which is why the dynamic kind exists.
        var without = CompileB(TestContext.Current.CancellationToken, [Code(B, "Connect", """
            /// <summary>Resolves nothing.</summary>
            public static class Connect
            {
                /// <summary>Always null.</summary>
                public static System.Type? Home() => null;
            }
            """)]);
        without.Edges.Where(e => e.EvidenceClass == DependencyEvidenceClasses.Static).Select(e => e.Target)
            .Should().Equal(set.Edges.Where(e => e.EvidenceClass == DependencyEvidenceClasses.Static).Select(e => e.Target),
                "a reflection string is invisible to GetUsedAssemblyReferences");
        without.Edges.Should().NotContain(e => e.EvidenceClass == DependencyEvidenceClasses.Dynamic);
    }

    [Fact(Timeout = 120_000)]
    public void ANonConstantArgument_IsDynamicUnresolved_NeverDropped()
    {
        var set = CompileB(TestContext.Current.CancellationToken, [Code(B, "ByName", """
            /// <summary>Resolves whatever it is handed.</summary>
            public static class ByName
            {
                /// <summary>The type, or null.</summary>
                public static System.Type? Of(string name) => System.Type.GetType(name);
                /// <summary>The assembly.</summary>
                public static System.Reflection.Assembly Load(string name) => System.Reflection.Assembly.Load(name);
            }
            """)]);

        set.Edges.Where(e => e.Kind == DependencyEdgeKinds.DynamicUnresolved).Select(e => e.Target)
            .Should().Equal("?System.Reflection.Assembly.Load", "?System.Type.GetType");
        var getType = set.Edges.Single(e => e.Target == "?System.Type.GetType");
        getType.Evidence.Should().Equal(["Type.GetType(name) in ByName"]);
        getType.EvidenceClass.Should().Be(DependencyEvidenceClasses.Dynamic);
        getType.TargetKind.Should().BeNull("unknown is unknown");
        set.Edges.Should().NotContain(e => e.Kind == DependencyEdgeKinds.Dynamic);
    }

    [Fact(Timeout = 120_000)]
    public void NegativeControl_StaticReflection_IsNoDynamicEdge()
    {
        var set = CompileB(TestContext.Current.CancellationToken, [Code(B, "Static", """
            /// <summary>Reflection that names its target statically.</summary>
            public static class Static
            {
                /// <summary>The runtime type of a value.</summary>
                public static System.Type Of(object value) => value.GetType();
                /// <summary>A new instance through a Type.</summary>
                public static object? Make() => System.Activator.CreateInstance(typeof(System.Text.StringBuilder));
                /// <summary>A new instance through a generic.</summary>
                public static System.Text.StringBuilder MakeGeneric() => System.Activator.CreateInstance<System.Text.StringBuilder>();
            }
            """)]);

        set.Edges.Should().NotContain(e => e.EvidenceClass == DependencyEvidenceClasses.Dynamic,
            "object.GetType(), CreateInstance(Type) and CreateInstance<T>() bind nothing by name");
    }

    [Fact(Timeout = 120_000)]
    public void ConstantForms_Resolve_ThroughConstFieldsAssemblyNamesAndTheFramework()
    {
        var set = CompileB(TestContext.Current.CancellationToken, [Code(B, "Forms", """
            /// <summary>Every constant form.</summary>
            public static class Forms
            {
                private const string Asm = "MeshWeaver.Graph";
                /// <summary>Loads Graph by a const.</summary>
                public static System.Reflection.Assembly Graph() => System.Reflection.Assembly.Load(Asm);
                /// <summary>Loads through an AssemblyName.</summary>
                public static System.Reflection.Assembly Named() =>
                    System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(
                        new System.Reflection.AssemblyName("MeshWeaver.Mesh.Contract"));
                /// <summary>A BCL type by name.</summary>
                public static object? Builder() => System.Activator.CreateInstance("System.Runtime", "System.Text.StringBuilder");
            }
            """)]);

        set.Edges.Where(e => e.Kind == DependencyEdgeKinds.Dynamic)
            .Select(e => $"{e.Target} => {e.TargetKind}")
            .Should().Equal(
                "*, MeshWeaver.Graph => Platform",
                "*, MeshWeaver.Mesh.Contract => Platform",
                "System.Text.StringBuilder, System.Runtime => Framework");
        set.Edges.Should().NotContain(e => e.Kind == DependencyEdgeKinds.DynamicUnresolved);
    }

    [Fact(Timeout = 120_000)]
    public void AReflectionCallInForeignCode_IsTheForeignOwnersEdge_NotThisTypes()
    {
        var foreign = Code(A, "Resolver", """
            /// <summary>Lib/A's own reflection.</summary>
            public static class Resolver
            {
                /// <summary>The type.</summary>
                public static System.Type? Home() =>
                    System.Type.GetType("MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph");
            }
            """);
        var set = CompileB(TestContext.Current.CancellationToken,
            [foreign, Code(B, "Uses", """
                /// <summary>B calls A's resolver.</summary>
                public static class Uses
                {
                    /// <summary>The type.</summary>
                    public static System.Type? Home() => Resolver.Home();
                }
                """)],
            ["namespace:Source scope:subtree", $"shared=@{A}/Source"]);

        set.Edges.Should().Contain(e => e.Kind == DependencyEdgeKinds.NodeType && e.Target == A);
        set.Edges.Should().NotContain(e => e.EvidenceClass == DependencyEvidenceClasses.Dynamic,
            "the reflection call sits in Lib/A's code; Lib/A's own compile records it");
    }

    [Fact(Timeout = 120_000)]
    public void PlatformEdges_NameEveryBoundMeshWeaverAssembly_AndNoBclAssembly()
    {
        var set = CompileB(TestContext.Current.CancellationToken, [Code(B, "Plain", """
            /// <summary>Binds a platform type and a BCL type.</summary>
            public static class Plain
            {
                /// <summary>A path's last segment.</summary>
                public static string Last(MeshWeaver.Mesh.MeshNode node) => new System.Text.StringBuilder(node.Path).ToString();
            }
            """)]);

        var platform = set.Edges.Where(e => e.Kind == DependencyEdgeKinds.Platform).ToList();
        platform.Select(e => e.Target).Should().Contain("MeshWeaver.Mesh.Contract");
        platform.Should().OnlyContain(e => e.Target.StartsWith("MeshWeaver", StringComparison.Ordinal));
        platform.Should().OnlyContain(e => e.TargetBuildMvid != null, "a platform edge names the build it compiled against");
        platform.Should().OnlyContain(e => e.EvidenceClass == DependencyEvidenceClasses.Static);
        platform.Single(e => e.Target == "MeshWeaver.Mesh.Contract").Evidence.Should().Contain("MeshWeaver.Mesh.MeshNode",
            "the evidence is the type references the emitted metadata resolves through that assembly");
        set.Edges.Should().NotContain(e => e.Target.StartsWith("System", StringComparison.Ordinal),
            "the BCL is on every host and never moves — not an edge");
    }

    [Fact(Timeout = 120_000)]
    public void APackageAssembly_IsAPackageEdgeNamedByItsPackageId_AndAnUnusedOneIsNone()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-depedges-pkg-" + Guid.NewGuid().ToString("N"));
        var used = Emit(Path.Combine(root, "acme.widgets", "1.2.0", "lib", "net10.0"), "Acme.Widgets",
            "namespace Acme.Widgets; public sealed class Widget { public int Size => 1; }");
        var unused = Emit(Path.Combine(root, "acme.gadgets", "2.0.0", "lib", "net10.0"), "Acme.Gadgets",
            "namespace Acme.Gadgets; public sealed class Gadget { }");
        try
        {
            var references = CompileReferences.Default
                .Append(MetadataReference.CreateFromFile(used))
                .Append(MetadataReference.CreateFromFile(unused))
                .ToList();
            var compilation = CSharpCompilation.Create("PkgProbe",
                [CSharpSyntaxTree.ParseText("public static class P { public static int S() => new Acme.Widgets.Widget().Size; }")],
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var classes = DependencyReferenceClasses.Of(references, [], [used, unused]);
            var image = new MemoryStream();
            var emit = compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
            emit.Success.Should().BeTrue(string.Join("\n", emit.Diagnostics));

            image.Position = 0;
            var edges = DependencyEdgeExtraction.EmittedReferenceEdges(compilation, image, classes);

            var package = edges.Should().ContainSingle(e => e.Kind == DependencyEdgeKinds.Package).Which;
            package.Target.Should().Be("acme.widgets", "the package id, read off the global-packages layout");
            package.Evidence.Should().Equal(["Acme.Widgets"]);
            edges.Should().NotContain(e => e.Target == "acme.gadgets", "referenced, never bound");
            // Negative control: the same file NOT classified as a package is not a package edge (and,
            // not being a MeshWeaver name, not a platform edge either).
            image.Position = 0;
            DependencyEdgeExtraction.EmittedReferenceEdges(compilation, image, DependencyReferenceClasses.None)
                .Should().NotContain(e => e.Kind == DependencyEdgeKinds.Package);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData("MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph", "MeshWeaver.Graph.Apps.HomeAppSource", "MeshWeaver.Graph")]
    [InlineData("Ns.T, Asm, Version=1.0.0.0, Culture=neutral", "Ns.T", "Asm")]
    [InlineData("Ns.G`1[[Ns.A, AsmA]], AsmG", "Ns.G`1[[Ns.A, AsmA]]", "AsmG")]
    [InlineData("Ns.Plain", "Ns.Plain", null)]
    public void AssemblyQualifiedNames_SplitAtTheFirstTopLevelComma(string name, string type, string? assembly) =>
        DynamicBindingExtraction.SplitAssemblyQualifiedName(name).Should().Be((type, assembly));

    [Theory]
    [InlineData("/home/u/.nuget/packages/acme.widgets/1.2.0/lib/net10.0/Acme.Widgets.dll", "acme.widgets")]
    [InlineData("/shelf/Acme.Flat.dll", "Acme.Flat")]
    public void PackageIdOf_ReadsTheGlobalPackagesLayout(string path, string id) =>
        DependencyReferenceClasses.PackageIdOf(path).Should().Be(id);

    private static string Emit(string directory, string name, string code)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".dll");
        var result = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(code)], CompileReferences.Default,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .Emit(path, cancellationToken: TestContext.Current.CancellationToken);
        result.Success.Should().BeTrue(string.Join("\n", result.Diagnostics));
        return path;
    }
}
