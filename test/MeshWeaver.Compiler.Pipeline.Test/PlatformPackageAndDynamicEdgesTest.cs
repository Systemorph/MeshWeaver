using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.NuGet;
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
    private static DependencyEdgeSet CompileB(System.Threading.CancellationToken ct, MeshNode[] nodes, IReadOnlyList<string>? sources = null,
        Func<IReadOnlyList<NuGetPackageReference>, System.Threading.CancellationToken, IReadOnlyList<string>>? resolveNuGet = null)
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
                resolveNuGet: resolveNuGet,
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
        getType.Evidence.Should().Equal(["Type.GetType(name) in ByName — 'typeName' is not a compile-time constant"]);
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
            package.Target.Should().Be("package:acme.widgets", "the package id, read off the global-packages layout, in the package namespace");
            package.Evidence.Should().Equal(["Acme.Widgets"]);
            edges.Should().NotContain(e => e.Target == "package:acme.gadgets", "referenced, never bound");
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

    /// <summary>
    /// A closed generic needs EVERY argument's assembly: a framework <c>List`1</c> over a platform
    /// type reaches into core and must not hide behind its outer type's kind. Negative control: the
    /// same outer type over a framework argument stays Framework. An argument whose assembly the
    /// compile cannot classify makes the whole target unknown.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AClosedGenericsArguments_DecideItsTargetKind_NotOnlyItsOuterType()
    {
        var set = CompileB(TestContext.Current.CancellationToken, [Code(B, "Generic", """
            /// <summary>Closed generics by name.</summary>
            public static class Generic
            {
                /// <summary>Over a platform type.</summary>
                public static System.Type? Nodes() => System.Type.GetType(
                    "System.Collections.Generic.List`1[[MeshWeaver.Mesh.MeshNode, MeshWeaver.Mesh.Contract]], System.Private.CoreLib");
                /// <summary>Over a framework type.</summary>
                public static System.Type? Strings() => System.Type.GetType(
                    "System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib");
                /// <summary>Over an assembly nobody references.</summary>
                public static System.Type? Unknown() => System.Type.GetType(
                    "System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Acme.Thing, Acme.Nowhere]], System.Private.CoreLib");
            }
            """)]);

        var kinds = set.Edges.Where(e => e.Kind == DependencyEdgeKinds.Dynamic)
            .ToDictionary(e => e.Target, e => e.TargetKind);
        kinds["System.Collections.Generic.List`1[[MeshWeaver.Mesh.MeshNode, MeshWeaver.Mesh.Contract]], System.Private.CoreLib"]
            .Should().Be(DependencyEdgeKinds.Platform, "resolving it requires the platform type");
        kinds["System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib"]
            .Should().Be(DependencyEdgeKinds.Framework, "negative control: nothing in it is the platform");
        kinds["System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],[Acme.Thing, Acme.Nowhere]], System.Private.CoreLib"]
            .Should().BeNull("an argument the compile cannot classify makes the target unknown, which counts as into core");
    }

    /// <summary>
    /// A FLAT package directory (the module-libraries shelf) carries no package in a file's path, so
    /// the caller hands the ownership over and the Package edge is named by it. Negative control:
    /// the path-only classification falls back to the file's simple name, which a declared
    /// <c>PackageReference Include="Acme.Bundle"</c> could never match.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AFlatShelfFile_IsNamedByTheOwnershipTheCallerHandsOver()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-depedges-shelf-" + Guid.NewGuid().ToString("N"));
        var widget = Emit(root, "Acme.Widget", "namespace Acme.Widget; public sealed class W { public int Size => 1; }");
        try
        {
            var references = CompileReferences.Default.Append(MetadataReference.CreateFromFile(widget)).ToList();
            var compilation = CSharpCompilation.Create("ShelfProbe",
                [CSharpSyntaxTree.ParseText("public static class P { public static int S() => new Acme.Widget.W().Size; }")],
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var image = new MemoryStream();
            compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken).Success.Should().BeTrue();

            image.Position = 0;
            var owned = DependencyEdgeExtraction.EmittedReferenceEdges(compilation, image,
                DependencyReferenceClasses.Of(references, [], new Dictionary<string, string> { [widget] = "Acme.Bundle" }));
            owned.Should().ContainSingle(e => e.Kind == DependencyEdgeKinds.Package).Which.Target.Should().Be("package:Acme.Bundle");

            image.Position = 0;
            var byPath = DependencyEdgeExtraction.EmittedReferenceEdges(compilation, image,
                DependencyReferenceClasses.Of(references, [], [widget]));
            byPath.Should().ContainSingle(e => e.Kind == DependencyEdgeKinds.Package).Which.Target.Should().Be("package:Acme.Widget",
                "negative control: a flat path names only the file");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A NODE-SUPPLIED source generator (one the node <c>#r</c>'d) runs code the node chose, so the
    /// reflection lookup it EMITS is the node's dynamic edge — the scan reads authored + node-generated
    /// trees. Negative control: the same authored code without the generator has no dynamic edge, so
    /// the edge comes from the generated tree.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AReflectionLookupANodeSuppliedGeneratorEmits_IsADynamicEdge()
    {
        var generator = BuildLookupGenerator();
        try
        {
            var withGenerator = CompileB(TestContext.Current.CancellationToken, [Code(B, "Plain", """
                #r "nuget:Probe.LookupGenerator, 1.0.0"
                /// <summary>No reflection of its own.</summary>
                public static class Plain { }
                """)], resolveNuGet: (_, _) => [generator]);
            var edge = withGenerator.Edges.Should().ContainSingle(e => e.Kind == DependencyEdgeKinds.Dynamic).Which;
            edge.Target.Should().Be("MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph");
            edge.TargetKind.Should().Be(DependencyEdgeKinds.Platform);

            var without = CompileB(TestContext.Current.CancellationToken, [Code(B, "Plain", """
                /// <summary>No reflection of its own.</summary>
                public static class Plain { }
                """)]);
            without.Edges.Should().NotContain(e => e.EvidenceClass == DependencyEvidenceClasses.Dynamic,
                "negative control: the authored code alone binds nothing by name");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(generator)!, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A generator assembly built here, on disk, the way a node's <c>#r "nuget:"</c>
    /// resolve hands one over; it emits a reflection lookup into the platform.</summary>
    private static string BuildLookupGenerator()
    {
        const string source = """
            using Microsoft.CodeAnalysis;
            [Generator]
            public sealed class LookupGenerator : ISourceGenerator
            {
                public void Initialize(GeneratorInitializationContext context) { }
                public void Execute(GeneratorExecutionContext context) =>
                    context.AddSource("Lookup.g.cs",
                        "public static class Lookup { public static System.Type? Home() => System.Type.GetType(\"MeshWeaver.Graph.Apps.HomeAppSource, MeshWeaver.Graph\"); }");
            }
            """;
        var name = $"Probe.LookupGenerator_{Guid.NewGuid():N}";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(ISourceGenerator).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var directory = Directory.CreateTempSubdirectory("lookup-gen-").FullName;
        var path = Path.Combine(directory, name + ".dll");
        var emitted = compilation.Emit(path, cancellationToken: TestContext.Current.CancellationToken);
        emitted.Success.Should().BeTrue(string.Join("; ", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }

    /// <summary>
    /// THE RULE, case 1 — an OVERRIDE of a reflection lookup: a subclass overriding
    /// <c>Assembly.GetType(string, bool, bool)</c> is still that lookup (matched through the override
    /// chain) but its behaviour is not the API's own, so the call is UNRESOLVED whatever its
    /// arguments. Negative control: the API's own method with proven inputs resolves.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AnOverrideOfAReflectionLookup_IsUnresolved_ByTheRule()
    {
        var set = CompileB(TestContext.Current.CancellationToken, [Code(B, "Overrides", """
            /// <summary>An assembly whose lookup is the author's.</summary>
            public sealed class Custom : System.Reflection.Assembly
            {
                /// <summary>Any answer at all.</summary>
                public override System.Type? GetType(string typeName, bool throwOnError, bool ignoreCase) => null;
            }
            /// <summary>Both lookups.</summary>
            public static class Overrides
            {
                /// <summary>Through the override.</summary>
                public static System.Type? ViaOverride() => new Custom().GetType("MeshWeaver.Graph.Apps.HomeAppSource", false, false);
                /// <summary>The API's own, every input proven.</summary>
                public static System.Type? Own() => typeof(string).Assembly.GetType("System.Text.StringBuilder", false, false);
            }
            """)]);

        var unresolved = set.Edges.Should().ContainSingle(e => e.Kind == DependencyEdgeKinds.DynamicUnresolved).Which;
        unresolved.Target.Should().Be("?System.Reflection.Assembly.GetType", "matched through the override chain");
        unresolved.Evidence.Should().ContainSingle().Which.Should().EndWith("whose behaviour is not the API's own");
        set.Edges.Should().ContainSingle(e => e.Kind == DependencyEdgeKinds.Dynamic
                                              && e.Target.StartsWith("System.Text.StringBuilder, ", StringComparison.Ordinal),
            "negative control: the API's own method with proven inputs resolves");
    }

    /// <summary>
    /// THE RULE, case 2 — a RESOLVER: <c>Type.GetType(name, assemblyResolver, typeResolver)</c> can
    /// return any type for any name, so the constant name proves nothing unless every resolver is
    /// provably null. Negative control: the same call with both resolvers <c>null</c> resolves.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AResolverThatIsNotProvablyNull_IsUnresolved_ByTheRule()
    {
        var set = CompileB(TestContext.Current.CancellationToken, [Code(B, "Resolvers", """
            /// <summary>Lookups through resolvers.</summary>
            public static class Resolvers
            {
                private static System.Type? Anything(System.Reflection.Assembly? a, string n, bool i) => typeof(MeshWeaver.Mesh.MeshNode);
                /// <summary>A resolver that can return a platform type for a framework name.</summary>
                public static System.Type? Resolved() => System.Type.GetType("System.String", null, Anything);
                /// <summary>Both resolvers provably null.</summary>
                public static System.Type? Plain() => System.Type.GetType("System.String", null, null);
            }
            """)]);

        var unresolved = set.Edges.Should().ContainSingle(e => e.Kind == DependencyEdgeKinds.DynamicUnresolved).Which;
        unresolved.Target.Should().Be("?System.Type.GetType");
        unresolved.Evidence.Should().ContainSingle().Which.Should().EndWith("the resolver 'typeResolver' is not provably null");
        set.Edges.Should().ContainSingle(e => e.Kind == DependencyEdgeKinds.Dynamic
                                              && e.Target.StartsWith("System.String, ", StringComparison.Ordinal)
                                              && e.TargetKind == DependencyEdgeKinds.Framework,
            "negative control: with both resolvers null the constant name decides, and it is the framework's");
    }

    /// <summary>
    /// Target identity across KINDS: a module ASSEMBLY named <c>Acme.Bundle</c> and a PACKAGE
    /// <c>Acme.Bundle</c> (shipping <c>Acme.Widget.dll</c>) are two dependencies, and the set keeps
    /// both — the package lives in its own target namespace, so target-keyed readers (the Postgres
    /// projection's <c>(source, target)</c> key) never have to choose. Negative control: the package
    /// id really is the module's name, so without the namespace the two targets would collide.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AModuleAndAPackageSharingAName_AreTwoEdges_WithDistinctTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-depedges-collide-" + Guid.NewGuid().ToString("N"));
        var module = Emit(Path.Combine(root, "modules"), "Acme.Bundle", "namespace Acme.Bundle; public sealed class M { public int V => 1; }");
        var widget = Emit(Path.Combine(root, "shelf"), "Acme.Widget", "namespace Acme.Widget; public sealed class W { public int V => 2; }");
        try
        {
            var references = CompileReferences.Default
                .Append(MetadataReference.CreateFromFile(module))
                .Append(MetadataReference.CreateFromFile(widget))
                .ToList();
            var tree = CSharpSyntaxTree.ParseText(
                "public static class P { public static int S() => new Acme.Bundle.M().V + new Acme.Widget.W().V; }");
            var compilation = CSharpCompilation.Create("CollideProbe", [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var image = new MemoryStream();
            compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken).Success.Should().BeTrue();
            var classes = DependencyReferenceClasses.Of(references, [module],
                new Dictionary<string, string> { [widget] = "Acme.Bundle" });

            var set = DependencyEdgeExtraction.Extract(compilation, [tree], "App/P", [], null, classes,
                () => new MemoryStream(image.ToArray(), writable: false), TestContext.Current.CancellationToken);

            var described = set.Edges.Select(e => $"{e.Kind} {e.Target}").ToList();
            described.Should().Contain("Module Acme.Bundle");
            described.Should().Contain("Package package:Acme.Bundle");
            set.Edges.Select(e => e.Target).Should().OnlyHaveUniqueItems();
            DependencyEdgeTargets.PackageIdOf("package:Acme.Bundle").Should().Be("Acme.Bundle",
                "negative control: the package id IS the module's name — only the namespace keeps them apart");
            System.Action collide = () => DependencyEdgeExtraction.UniqueTargets("App/P", [
                    new DependencyEdge { Target = "Acme.Bundle", Kind = DependencyEdgeKinds.Module },
                    new DependencyEdge { Target = "Acme.Bundle", Kind = DependencyEdgeKinds.Package }]);
            collide
                .Should().Throw<InvalidOperationException>("a collision is an extractor defect, reported loudly");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The strict grammar: what it accepts it fully describes; what it does not
    /// understand is REFUSED (the caller then counts the target as unknown — into core).</summary>
    [Theory]
    [InlineData("G`2[[A, X],[B`1[[C, Y]], Z]], Out", true, "G`2<A@X,B`1<C@Y>@Z>@Out")]
    [InlineData("System.Collections.Generic.Dictionary`2[System.String,[MeshWeaver.Mesh.MeshNode, MeshWeaver.Mesh.Contract]], System.Private.CoreLib",
        true, "System.Collections.Generic.Dictionary`2<System.String,MeshWeaver.Mesh.MeshNode@MeshWeaver.Mesh.Contract>@System.Private.CoreLib")]
    [InlineData("Ns.Plain", true, "Ns.Plain")]
    [InlineData("Ns.Array[], Asm, Version=1.0.0.0", true, "Ns.Array@Asm")]
    [InlineData("Ns.Outer+Inner[,]&", true, "Ns.Outer+Inner")]
    [InlineData("G`1[[A, X]", false, "")]
    [InlineData("G`1[A,]", false, "")]
    [InlineData("Ns.Esc\\,aped", false, "")]
    [InlineData("Ns.T[x", false, "")]
    public void TypeNames_ParseStrictly_OrNotAtAll(string name, bool parses, string expected)
    {
        static string Render(DynamicBindingExtraction.ParsedTypeName t) =>
            t.Arguments.IsEmpty
                ? t.Name
                : $"{t.Name}<{string.Join(",", t.Arguments.Select(a => Render(a.Type) + (a.Assembly is null ? "" : "@" + a.Assembly)))}>";
        DynamicBindingExtraction.TryParseTypeName(name, out var parsed, out var assembly).Should().Be(parses);
        if (parses)
            (Render(parsed!) + (assembly is null ? "" : "@" + assembly)).Should().Be(expected);
    }

    /// <summary>
    /// A MIXED-qualified generic (a bare first argument, a qualified second) still needs the platform
    /// argument's assembly. Negative control: the same shape over framework arguments stays Framework.
    /// A name the strict grammar refuses is unknown, never a guess.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AMixedQualifiedGeneric_AndAnUnparsableName_AreJudgedConservatively()
    {
        var set = CompileB(TestContext.Current.CancellationToken, [Code(B, "Mixed", """
            /// <summary>Mixed generics by name.</summary>
            public static class Mixed
            {
                /// <summary>Into the platform.</summary>
                public static System.Type? Core() => System.Type.GetType(
                    "System.Collections.Generic.Dictionary`2[System.String,[MeshWeaver.Mesh.MeshNode, MeshWeaver.Mesh.Contract]], System.Private.CoreLib");
                /// <summary>Framework only.</summary>
                public static System.Type? Bcl() => System.Type.GetType(
                    "System.Collections.Generic.Dictionary`2[System.String,[System.Int32, System.Private.CoreLib]], System.Private.CoreLib");
                /// <summary>Unbalanced.</summary>
                public static System.Type? Broken() => System.Type.GetType("System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib], System.Private.CoreLib");
            }
            """)]);

        var kinds = set.Edges.Where(e => e.Kind == DependencyEdgeKinds.Dynamic).ToDictionary(e => e.Target, e => e.TargetKind);
        kinds.Single(k => k.Key.Contains("MeshNode", StringComparison.Ordinal)).Value.Should().Be(DependencyEdgeKinds.Platform);
        kinds.Single(k => k.Key.Contains("System.Int32, System.Private.CoreLib]]", StringComparison.Ordinal)).Value
            .Should().Be(DependencyEdgeKinds.Framework, "negative control: nothing in it is the platform");
        kinds.Single(k => k.Key.Contains("List`1", StringComparison.Ordinal)).Value
            .Should().BeNull("a name the strict grammar refuses is unknown, which counts as into core");
    }

    /// <summary>
    /// <c>typeof(T).Assembly</c> of a TYPE PARAMETER names whatever assembly the runtime argument
    /// comes from — unknown at compile time, never the parameter's declaring assembly and never a
    /// fall-back to the compilation's own lookup. Negative control: <c>typeof(string).Assembly</c> is
    /// statically the framework's.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void ALookupInTypeofAGenericParameter_IsUnknown_NotTheDeclaringAssembly()
    {
        var set = CompileB(TestContext.Current.CancellationToken, [Code(B, "Receivers", """
            /// <summary>Lookups in an assembly chosen by a receiver.</summary>
            public static class Receivers
            {
                /// <summary>In T's assembly — the caller decides.</summary>
                public static System.Type? Of<T>() => typeof(T).Assembly.GetType("MeshWeaver.Graph.Apps.HomeAppSource");
                /// <summary>In the framework's.</summary>
                public static System.Type? Bcl() => typeof(string).Assembly.GetType("System.Text.StringBuilder");
                /// <summary>Any Assembly value.</summary>
                public static System.Type? Any(System.Reflection.Assembly assembly) => assembly.GetType("Receivers");
            }
            """)]);

        // Both unknown receivers fall to the ONE rule: an input that decides the result is unproven ⇒ unresolved.
        var unresolved = set.Edges.Should().ContainSingle(e => e.Kind == DependencyEdgeKinds.DynamicUnresolved).Which;
        unresolved.Target.Should().Be("?System.Reflection.Assembly.GetType");
        unresolved.Evidence.Should().HaveCount(2).And.OnlyContain(e => e.EndsWith("the receiver it searches is not statically known"),
            "T's runtime argument, and an arbitrary Assembly, decide where the name is looked up");
        var kinds = set.Edges.Where(e => e.Kind == DependencyEdgeKinds.Dynamic).ToDictionary(e => e.Target, e => e.TargetKind);
        // string's assembly is whichever the reference set carries (a reference or an implementation
        // assembly) — the point is that it is KNOWN, and the framework's.
        kinds.Single(k => k.Key.StartsWith("System.Text.StringBuilder, ", StringComparison.Ordinal)).Value
            .Should().Be(DependencyEdgeKinds.Framework, "negative control: typeof(string) is statically known");
    }

    /// <summary>
    /// A type-registry lookup is matched by the interface's NAME and POSITION, so a registry that
    /// renames the parameter (<c>GetType(string typeName)</c>) stays on the ratchet. Negative control:
    /// the same signature on a type that is not a registry is no edge.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void ARegistryThatRenamesItsParameter_IsStillMatched()
    {
        var set = CompileB(TestContext.Current.CancellationToken, [Code(B, "Registries", """
            /// <summary>A registry surface with a renamed parameter.</summary>
            public interface IRenamedRegistry : MeshWeaver.Domain.ITypeRegistry
            {
                /// <summary>By another name.</summary>
                new System.Type? GetType(string typeName);
            }
            /// <summary>Not a registry.</summary>
            public interface INotARegistry
            {
                /// <summary>Same signature, different surface.</summary>
                System.Type? GetType(string typeName);
            }
            /// <summary>Calls both.</summary>
            public static class Registries
            {
                /// <summary>Through the registry.</summary>
                public static System.Type? Registered(IRenamedRegistry r) => r.GetType("Story");
                /// <summary>Through something else.</summary>
                public static System.Type? Other(INotARegistry r) => r.GetType("Other");
            }
            """)]);

        var registry = set.Edges.Should().ContainSingle(e => e.EvidenceClass == DependencyEvidenceClasses.Dynamic).Which;
        registry.Kind.Should().Be(DependencyEdgeKinds.DynamicUnresolved,
            "a registry resolves to whatever some hub registered — never proven at compile time");
        registry.Target.Should().Be("?MeshWeaver.Domain.ITypeRegistry.GetType");
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
