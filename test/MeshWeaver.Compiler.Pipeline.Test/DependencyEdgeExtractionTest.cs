using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// The forward dependency edges Roslyn reads at the end of a NodeType compile
/// (<c>Doc/Architecture/DependencyGraph</c>), measured on REAL emits through the build-process bake
/// (<see cref="NodeSetCompiler.Compile"/>) — the same extraction the runtime compile runs.
///
/// <para>Every positive case has a negative control beside it that differs in exactly the one
/// thing the edge is supposed to follow: binding versus merely including, owning code versus
/// foreign code, a used module versus a referenced-but-unused one.</para>
/// </summary>
public class DependencyEdgeExtractionTest
{
    private const string A = "Lib/A";
    private const string B = "App/B";
    private const string C = "Lib/C";

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

    private static MeshNode AThing() => Code(A, "AThing", """
        /// <summary>A type owned by Lib/A.</summary>
        public record AThing(int Value);
        """);

    private static MeshNode CThing() => Code(C, "CThing", """
        /// <summary>A type owned by Lib/C.</summary>
        public record CThing(string Name);
        """);

    /// <summary>Compiles <see cref="B"/> out of <paramref name="nodes"/> with
    /// <paramref name="sources"/> and returns what the bake recorded.</summary>
    private static NodeSetCompiler.CompiledNode CompileB(
        IReadOnlyList<MeshNode> nodes, IReadOnlyList<string>? sources, System.Threading.CancellationToken ct,
        IReadOnlyList<MetadataReference>? references = null, IReadOnlyCollection<string>? moduleAssemblyPaths = null)
    {
        var output = Path.Combine(Path.GetTempPath(), "mw-depedges-" + Guid.NewGuid().ToString("N"));
        try
        {
            return NodeSetCompiler.CompileWithModules(
                NodeSet.Create([TypeNode(B), .. nodes]), TypeNode(B),
                sources, tests: null, configuration: null, contentCollections: null,
                references: references ?? CompileReferences.Default,
                dependencyIdOf: _ => null,
                toolchainId: NodeTypeCompilationHelpers.ProcessToolchainId,
                outputDirectory: output,
                ct: ct,
                moduleAssemblyPaths: moduleAssemblyPaths);
        }
        finally
        {
            // The record is read off the compile, never off the files — the directory can go.
            try { Directory.Delete(output, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly string[] OwnAndShared = ["namespace:Source scope:subtree", $"shared=@{A}/Source"];

    [Fact(Timeout = 120_000)]
    public void BBindsATypeOfA_RecordsExactlyTheEdgeBToA_StampedWithTheEmittedMvid()
    {
        var output = Path.Combine(Path.GetTempPath(), "mw-depedges-mvid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var compiled = NodeSetCompiler.Compile(
                NodeSet.Create([TypeNode(B), AThing(), Code(B, "UsesA", """
                    /// <summary>B's own code, binding A's type.</summary>
                    public static class UsesA
                    {
                        /// <summary>Doubles an AThing.</summary>
                        public static AThing Twice(AThing a) => new(a.Value * 2);
                    }
                    """)]),
                TypeNode(B), OwnAndShared, tests: null, configuration: null, contentCollections: null,
                references: CompileReferences.Default, dependencyIdOf: _ => null,
                toolchainId: NodeTypeCompilationHelpers.ProcessToolchainId, outputDirectory: output,
                ct: TestContext.Current.CancellationToken);

            var edges = compiled.DependencyEdges?.GraphEdges();
            edges.Should().NotBeNull("the bake always extracts — null would read as 'never extracted'");
            edges!.Source.Should().Be(B);
            edges.Edges.Should().ContainSingle("B binds exactly one foreign owner's code");
            var edge = edges.Edges[0];
            edge.Target.Should().Be(A);
            edge.Kind.Should().Be(DependencyEdgeKinds.NodeType);
            edge.Evidence.Should().Equal(["AThing"], "the evidence names WHY B depends on A");
            edge.TargetBuildMvid.Should().BeNull("A's code is compiled INTO B — there is no separate A build");
            edges.IncludedWithoutUse.Should().BeEmpty("A is bound, so it is an edge, not a bare inclusion");

            // The build identity is the emitted assembly's own MVID, read independently here.
            edges.BuildMvid.Should().Be(MvidOf(compiled.DllPath),
                "the edge set names the bytes it was read from, so a stale set can be told from a current one");
            edges.InputDigest.Should().NotBeNullOrEmpty();
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact(Timeout = 120_000)]
    public void NegativeControl_BIncludesAButNeverBindsIt_HasNoEdge_AndNamesTheInclusion()
    {
        var edges = CompileB(
            [AThing(), Code(B, "Standalone", """
                /// <summary>B's own code, binding nothing of A's.</summary>
                public static class Standalone
                {
                    /// <summary>Forty-two.</summary>
                    public static int Answer() => 42;
                }
                """)],
            OwnAndShared, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Should().BeEmpty(
            "A's code was compiled in, but nothing B owns binds it — an inclusion is a declared source "
            + "query, an edge is what Roslyn bound");
        edges.IncludedWithoutUse.Should().Equal([A]);
        edges.BuildMvid.Should().NotBeNullOrEmpty("an EMPTY set is still an extracted set for a named build");
    }

    [Fact(Timeout = 120_000)]
    public void NegativeControl_BWithoutAnyForeignCode_HasNoEdgeAndNoInclusion()
    {
        var edges = CompileB(
            [AThing(), Code(B, "Standalone", """
                /// <summary>B's own code.</summary>
                public record Standalone(int Value);
                """)],
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Should().BeEmpty();
        edges.IncludedWithoutUse.Should().BeEmpty("B's default source query never reaches A");
    }

    [Fact(Timeout = 120_000)]
    public void AForeignOwnersOwnReference_IsThatOwnersEdge_NotTheCompilingTypes()
    {
        // B compiles A's and C's code in; A's code binds C's type, B's own code binds only A's.
        var edges = CompileB(
            [
                CThing(),
                Code(A, "AThing", """
                    /// <summary>A type owned by Lib/A that binds Lib/C.</summary>
                    public record AThing(CThing Inner, int Count);
                    """),
                Code(B, "UsesA", """
                    /// <summary>B's own code, binding only A's type.</summary>
                    public static class UsesA
                    {
                        /// <summary>The count.</summary>
                        public static int Count(AThing a) => a.Count;
                    }
                    """),
            ],
            ["namespace:Source scope:subtree", $"shared=@{A}/Source", $"shared=@{C}/Source"],
            TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Select(e => e.Target).Should().Equal([A],
            "A → C is A's edge, recorded by A's own compile; the closure joins B → A → C. B's own "
            + "code binds only A's members, so C is compiled in but never bound by B");
        edges.IncludedWithoutUse.Should().Equal([C]);
    }

    [Fact(Timeout = 120_000)]
    public void AnIncludedSnippet_IsAttributedToTheIncludedNodesOwner()
    {
        var edges = CompileB(
            [AThing(), Code(B, "UsesA", """
                @@Lib/A/Source/AThing
                /// <summary>B's own code, binding the included type.</summary>
                public static class UsesA
                {
                    /// <summary>One.</summary>
                    public static AThing One() => new(1);
                }
                """)],
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Should().ContainSingle().Which.Target.Should().Be(A,
            "the snippet's text was spliced into B's file, but the INCLUDED node is A's");
    }

    [Fact(Timeout = 120_000)]
    public void AUsedModule_IsAModuleEdgeWithItsMvid_AndAnUnusedOneIsNot()
    {
        var used = EmitModule("DepGraphProbeModuleUsed", """
            namespace DepGraphProbe.Used;
            /// <summary>A type a NodeType binds.</summary>
            public sealed class Widget { /// <summary>Its name.</summary>
            public string Name => "w"; }
            """);
        var unused = EmitModule("DepGraphProbeModuleUnused", """
            namespace DepGraphProbe.Unused;
            /// <summary>A type no NodeType binds.</summary>
            public sealed class Gadget { }
            """);
        try
        {
            var edges = CompileB(
                [Code(B, "UsesModule", """
                    /// <summary>B's own code, binding a module type.</summary>
                    public static class UsesModule
                    {
                        /// <summary>The widget's name.</summary>
                        public static string Name() => new DepGraphProbe.Used.Widget().Name;
                    }
                    """)],
                sources: null, TestContext.Current.CancellationToken,
                references: [.. CompileReferences.Default,
                    MetadataReference.CreateFromFile(used), MetadataReference.CreateFromFile(unused)],
                moduleAssemblyPaths: [used, unused]).DependencyEdges!.GraphEdges();

            var module = edges.Edges.Should().ContainSingle(
                "the unused module is referenced but never bound — the emitted image carries no AssemblyRef to it").Which;
            module.Kind.Should().Be(DependencyEdgeKinds.Module);
            module.Target.Should().Be("DepGraphProbeModuleUsed");
            module.TargetBuildMvid.Should().Be(MvidOf(used), "a module edge names the module build it compiled against");
        }
        finally
        {
            foreach (var file in new[] { used, unused })
                try { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Module edges are read off the EMITTED image, not off
    /// <see cref="Compilation.GetUsedAssemblyReferences"/>: that call re-binds every method body a
    /// second time (it roughly doubled every NodeType compile) and counts a module the lookup merely
    /// looked into — an imported extension that lost overload resolution. Negative control: the same
    /// compilation without an image falls back to <see cref="Compilation.GetUsedAssemblyReferences"/>,
    /// which DOES name that module — so the assertion discriminates.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AModuleOnlyImported_IsNoModuleEdge_FromTheEmittedImage()
    {
        var ct = TestContext.Current.CancellationToken;
        var module = EmitModule("DepGraphProbeImportedOnly", """
            namespace DepGraphProbe.Imported;
            /// <summary>An extension the probe's call LOOKS AT and does not bind.</summary>
            public static class Extensions
            {
                /// <summary>A candidate for <c>"x".Count(…)</c> that loses to LINQ's predicate overload.</summary>
                public static int Count(this string value) => value.Length;
            }
            """);
        try
        {
            var references = CompileReferences.Default.Append(MetadataReference.CreateFromFile(module)).ToList();
            var tree = CSharpSyntaxTree.ParseText("""
                using System.Linq;
                using DepGraphProbe.Imported;
                public static class P { public static int S() => "x".Count(c => c == 'x'); }
                """, cancellationToken: ct);
            var compilation = CSharpCompilation.Create("ImportedOnlyProbe", [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var image = new MemoryStream();
            compilation.Emit(image, cancellationToken: ct).Success.Should().BeTrue();
            var classes = DependencyReferenceClasses.Of(references, [module], new Dictionary<string, string>());

            var fromImage = DependencyEdgeExtraction.Extract(compilation, [tree], "App/P", [], null, classes,
                () => new MemoryStream(image.ToArray(), writable: false), ct: ct);
            var withoutImage = DependencyEdgeExtraction.Extract(compilation, [tree], "App/P", [], null, classes,
                emittedImage: null, ct: ct);

            fromImage.Edges.Should().NotContain(e => e.Kind == DependencyEdgeKinds.Module,
                "nothing in the emitted code references the module — its extension was only a lookup candidate");
            withoutImage.Edges.Should().Contain(
                e => e.Kind == DependencyEdgeKinds.Module && e.Target == "DepGraphProbeImportedOnly",
                "negative control: GetUsedAssemblyReferences counts the imported candidate, which is what the image avoids");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(module)!, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// What the emitted image cannot show is still a module edge: a constant (an enum member
    /// included) the compiler INLINED, an erased <c>[Conditional]</c> call, a <c>default(enum)</c>
    /// constant, and a <c>nameof</c> of a module type or of a generic argument naming one, leave no
    /// <c>AssemblyRef</c> — asserted below as the precondition, so the case really exercises the
    /// inlining path — and a module extension used only inside the arguments of an erased call (a
    /// consumer-owned <c>[Conditional]</c> method, <c>Debug.Assert</c> without DEBUG) — yet a change to the module changes or breaks the consumer, and CI selection
    /// reads the module edge to know that. Negative control: a third module declares a constant of
    /// the SAME name the consumer uses for its own local, and is no edge — the name only nominates,
    /// the binding decides.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AModuleOnlyReadAtCompileTime_IsStillAModuleEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        var constants = EmitModule("DepGraphProbeConstOnly", """
            namespace DepGraphProbe.Consts;
            /// <summary>Constants a consumer inlines.</summary>
            public static class Limits
            {
                /// <summary>Inlined by the consumer.</summary>
                public const int Max = 7;
            }
            /// <summary>An enum whose member a consumer inlines.</summary>
            public enum Color
            {
                /// <summary>Red.</summary>
                Red,
                /// <summary>Blue.</summary>
                Blue,
            }
            """);
        var named = EmitModule("DepGraphProbeNameofOnly", """
            namespace DepGraphProbe.Named;
            /// <summary>A type a consumer only names.</summary>
            public sealed class Widget { }
            """);
        var conditional = EmitModule("DepGraphProbeConditionalOnly", """
            namespace DepGraphProbe.Cond;
            /// <summary>A call the compiler erases when NEVER is undefined.</summary>
            public static class Log
            {
                /// <summary>Erased at the call site.</summary>
                [System.Diagnostics.Conditional("NEVER")]
                public static void Trace(string message) { }
            }
            """);
        var shaded = EmitModule("DepGraphProbeDefaultOnly", """
            namespace DepGraphProbe.Shades;
            /// <summary>An enum only read as <c>(int)default(Shade)</c>.</summary>
            public enum Shade
            {
                /// <summary>Dark.</summary>
                Dark,
            }
            """);
        var generic = EmitModule("DepGraphProbeNameofArgumentOnly", """
            namespace DepGraphProbe.Args;
            /// <summary>A type only named as a generic argument inside a nameof.</summary>
            public sealed class Gadget { }
            """);
        var erasedArgument = EmitModule("DepGraphProbeErasedArgumentOnly", """
            namespace DepGraphProbe.Ext;
            /// <summary>An extension used only inside a consumer-owned erased [Conditional] call.</summary>
            public static class Probes
            {
                /// <summary>Erased with the call it is an argument of.</summary>
                public static string Probe(this string value) => value;
            }
            """);
        var assertArgument = EmitModule("DepGraphProbeAssertArgumentOnly", """
            namespace DepGraphProbe.Holds;
            /// <summary>An extension used only inside an erased Debug.Assert.</summary>
            public static class Checks
            {
                /// <summary>Erased with the Debug.Assert it is an argument of.</summary>
                public static bool Holds(this string value) => value.Length > 0;
            }
            """);
        var shadowed = EmitModule("DepGraphProbeSameNameOnly", """
            namespace DepGraphProbe.Shadow;
            /// <summary>A constant whose NAME the consumer reuses for a local.</summary>
            public static class Other
            {
                /// <summary>Never read by the consumer.</summary>
                public const int Total = 1;
            }
            """);
        try
        {
            var references = CompileReferences.Default
                .Append(MetadataReference.CreateFromFile(constants))
                .Append(MetadataReference.CreateFromFile(named))
                .Append(MetadataReference.CreateFromFile(conditional))
                .Append(MetadataReference.CreateFromFile(shaded))
                .Append(MetadataReference.CreateFromFile(generic))
                .Append(MetadataReference.CreateFromFile(shadowed))
                .Append(MetadataReference.CreateFromFile(erasedArgument))
                .Append(MetadataReference.CreateFromFile(assertArgument))
                .ToList();
            var tree = CSharpSyntaxTree.ParseText("""
                using DepGraphProbe.Consts;
                using DepGraphProbe.Ext;
                using DepGraphProbe.Holds;
                public static class P
                {
                    [System.Diagnostics.Conditional("NEVER")]
                    static void Trace(string message) { }
                    public static void E() => Trace("x".Probe());
                    public static void A() => System.Diagnostics.Debug.Assert("y".Holds());
                    public static int S() { var Total = Limits.Max + (int)Color.Blue; return Total; }
                    public static string N() => nameof(DepGraphProbe.Named.Widget);
                    public static void T() => DepGraphProbe.Cond.Log.Trace("erased");
                    public static int D() => (int)default(DepGraphProbe.Shades.Shade);
                    public static string G() => nameof(System.Collections.Generic.List<DepGraphProbe.Args.Gadget>);
                }
                """, cancellationToken: ct);
            var compilation = CSharpCompilation.Create("ConstOnlyProbe", [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var image = new MemoryStream();
            var emit = compilation.Emit(image, cancellationToken: ct);
            emit.Success.Should().BeTrue(string.Join("\n", emit.Diagnostics));
            using (var pe = new PEReader(new MemoryStream(image.ToArray(), writable: false)))
            {
                var reader = pe.GetMetadataReader();
                reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
                    .Should().NotContain(["DepGraphProbeConstOnly", "DepGraphProbeNameofOnly", "DepGraphProbeConditionalOnly",
                            "DepGraphProbeDefaultOnly", "DepGraphProbeNameofArgumentOnly",
                            "DepGraphProbeErasedArgumentOnly", "DepGraphProbeAssertArgumentOnly"],
                        "precondition: every use is erased at compile time, so the image carries no AssemblyRef to any of them");
            }
            var classes = DependencyReferenceClasses.Of(references,
                [constants, named, conditional, shaded, generic, shadowed, erasedArgument, assertArgument],
                new Dictionary<string, string>());

            var modules = DependencyEdgeExtraction.Extract(compilation, [tree], "App/P", [], null, classes,
                    () => new MemoryStream(image.ToArray(), writable: false), ct: ct)
                .Edges.Where(e => e.Kind == DependencyEdgeKinds.Module).ToList();

            modules.Select(e => e.Target).Should().Equal(
                ["DepGraphProbeAssertArgumentOnly", "DepGraphProbeConditionalOnly", "DepGraphProbeConstOnly",
                 "DepGraphProbeDefaultOnly", "DepGraphProbeErasedArgumentOnly", "DepGraphProbeNameofArgumentOnly",
                 "DepGraphProbeNameofOnly"],
                "an inlined constant, an erased [Conditional] call, a module extension inside the ARGUMENTS of an erased "
                + "call (consumer-owned or Debug.Assert), a default(enum) constant and a nameof (its generic argument "
                + "included) are uses the image cannot show; a same-named local is not a use");
            modules.Single(e => e.Target == "DepGraphProbeConstOnly").TargetBuildMvid.Should().Be(MvidOf(constants));
        }
        finally
        {
            foreach (var path in new[] { constants, named, conditional, shaded, generic, shadowed, erasedArgument, assertArgument })
            {
                try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Three more callees that erase their arguments, each leaving a module extension in those
    /// arguments with no <c>AssemblyRef</c> (asserted as the precondition): a <c>[Conditional]</c>
    /// method of a module that IS already an edge (its live member is called too), a partial method
    /// declared without an implementation, and a consumer method whose <c>[Conditional]</c> is spelled
    /// through a using ALIAS. Each extension's module is still a module edge. Negative control: the
    /// already-edged module is its edge once, from the image, and a module the consumer never uses is none.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AnArgumentOfAnyErasedCall_IsStillAModuleEdge_EdgedModuleConditionalPartialAndAlias()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = EmitModule("DepGraphProbeLiveAndErased", """
            namespace DepGraphProbe.Mixed;
            /// <summary>One live and one erased method.</summary>
            public static class Log
            {
                /// <summary>Live: the image references this module.</summary>
                public static void Live() { }
                /// <summary>Erased at the call site.</summary>
                [System.Diagnostics.Conditional("NEVER")]
                public static void Note(string value) { }
            }
            """);
        string Ext(string assembly, string ns, string method) => EmitModule(assembly, $$"""
            namespace {{ns}};
            /// <summary>An extension used only inside an erased call.</summary>
            public static class E
            {
                /// <summary>Erased with its call.</summary>
                public static string {{method}}(this string value) => value;
            }
            """);
        var mixed = Ext("DepGraphProbeMixedArgOnly", "DepGraphProbe.MixedArg", "ProbeM");
        var partial = Ext("DepGraphProbePartialArgOnly", "DepGraphProbe.PartialArg", "ProbeP");
        var aliased = Ext("DepGraphProbeAliasArgOnly", "DepGraphProbe.AliasArg", "ProbeA");
        var unused = Ext("DepGraphProbeUnusedModule", "DepGraphProbe.Unused", "ProbeU");
        var all = new[] { log, mixed, partial, aliased, unused };
        try
        {
            var references = CompileReferences.Default.Concat(all.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))).ToList();
            var tree = CSharpSyntaxTree.ParseText("""
                using C = System.Diagnostics.ConditionalAttribute;
                using DepGraphProbe.MixedArg;
                using DepGraphProbe.PartialArg;
                using DepGraphProbe.AliasArg;
                public static partial class P
                {
                    static partial void Pnote(string value);
                    [C("NEVER")]
                    static void Drop(string value) { }
                    public static void M() { DepGraphProbe.Mixed.Log.Live(); DepGraphProbe.Mixed.Log.Note("x".ProbeM()); }
                    public static void Q() => Pnote("x".ProbeP());
                    public static void A() => Drop("x".ProbeA());
                }
                """, cancellationToken: ct);
            var compilation = CSharpCompilation.Create("ErasedArgProbe", [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var image = new MemoryStream();
            var emit = compilation.Emit(image, cancellationToken: ct);
            emit.Success.Should().BeTrue(string.Join("\n", emit.Diagnostics));
            using (var pe = new PEReader(new MemoryStream(image.ToArray(), writable: false)))
            {
                var reader = pe.GetMetadataReader();
                var refs = reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name)).ToList();
                refs.Should().Contain("DepGraphProbeLiveAndErased", "precondition: the live call references the conditional's module");
                refs.Should().NotContain(["DepGraphProbeMixedArgOnly", "DepGraphProbePartialArgOnly", "DepGraphProbeAliasArgOnly"],
                    "precondition: every extension is used only inside an erased call");
            }
            var classes = DependencyReferenceClasses.Of(references, all, new Dictionary<string, string>());

            var modules = DependencyEdgeExtraction.Extract(compilation, [tree], "App/P", [], null, classes,
                    () => new MemoryStream(image.ToArray(), writable: false), ct: ct)
                .Edges.Where(e => e.Kind == DependencyEdgeKinds.Module).Select(e => e.Target).ToList();

            modules.Should().Equal(
                ["DepGraphProbeAliasArgOnly", "DepGraphProbeLiveAndErased", "DepGraphProbeMixedArgOnly", "DepGraphProbePartialArgOnly"],
                "an argument of an already-edged module's conditional, of an unimplemented partial and of an aliased "
                + "[Conditional] is a use the image cannot show; an unused module is not an edge");
        }
        finally
        {
            foreach (var path in all)
            {
                try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// #7449: a module member used ONLY in code a constant condition makes unreachable is bound — a
    /// change to the module breaks the consumer's compile — but the compiler discards that code, so the
    /// image carries no <c>AssemblyRef</c> for it. Six shapes, each on its own module: <c>if (false)</c>,
    /// <c>false &amp;&amp; …</c>, <c>true || …</c>, a constant ternary's dead arm, <c>while (false)</c>,
    /// <c>for (…; false; …)</c>, and a named <c>const bool</c> condition. Under BOTH optimisation levels
    /// (the bake emits Debug) every one is still a module edge. On unfixed code the shapes whose
    /// AssemblyRef the compiler dropped were missing.
    /// Negative control: a constant-TRUE branch is edged once from the image, a non-constant condition
    /// is ordinary reachable code, and a module the consumer never uses is no edge.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AMemberUsedOnlyInConstantUnreachableCode_IsStillAModuleEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        string Ext(string assembly, string ns, string method) => EmitModule(assembly, $$"""
            namespace {{ns}};
            /// <summary>An extension used only inside constant-unreachable code.</summary>
            public static class E
            {
                /// <summary>Discarded with its branch.</summary>
                public static string {{method}}(this string value) => value;
            }
            """);
        var deadShapes = new (string Assembly, string Ns, string Method)[]
        {
            ("DepGraphProbeIfFalse", "DepGraphProbe.IfFalse", "PIf"),
            ("DepGraphProbeAndFalse", "DepGraphProbe.AndFalse", "PAnd"),
            ("DepGraphProbeOrTrue", "DepGraphProbe.OrTrue", "POr"),
            ("DepGraphProbeTernaryArm", "DepGraphProbe.TernaryArm", "PTer"),
            ("DepGraphProbeWhileFalse", "DepGraphProbe.WhileFalse", "PWhile"),
            ("DepGraphProbeForFalse", "DepGraphProbe.ForFalse", "PFor"),
            ("DepGraphProbeConstFlag", "DepGraphProbe.ConstFlag", "PConst"),
        };
        var dead = deadShapes.Select(s => Ext(s.Assembly, s.Ns, s.Method)).ToArray();
        var live = Ext("DepGraphProbeIfTrue", "DepGraphProbe.IfTrue", "PLive");
        var flagged = Ext("DepGraphProbeRuntimeFlag", "DepGraphProbe.RuntimeFlag", "PFlag");
        var unused = Ext("DepGraphProbeNeverUsed", "DepGraphProbe.NeverUsed", "PNever");
        var all = dead.Append(live).Append(flagged).Append(unused).ToArray();
        try
        {
            var references = CompileReferences.Default.Concat(all.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))).ToList();
            var tree = CSharpSyntaxTree.ParseText("""
                using DepGraphProbe.IfFalse;
                using DepGraphProbe.AndFalse;
                using DepGraphProbe.OrTrue;
                using DepGraphProbe.TernaryArm;
                using DepGraphProbe.WhileFalse;
                using DepGraphProbe.ForFalse;
                using DepGraphProbe.ConstFlag;
                using DepGraphProbe.IfTrue;
                using DepGraphProbe.RuntimeFlag;
                public static class P
                {
                    const bool Off = false;
                    public static bool Flag;
                    public static void I() { if (false) { "x".PIf(); } }
                    public static bool A() => false && "x".PAnd().Length > 0;
                    public static bool O() => true || "x".POr().Length > 0;
                    public static string T() => true ? "a" : "x".PTer();
                    public static void W() { while (false) { "x".PWhile(); } }
                    public static void F() { for (var i = 0; false; i++) { "x".PFor(); } }
                    public static void C() { if (Off) { "x".PConst(); } }
                    public static void L() { if (true) { "x".PLive(); } }
                    public static void R() { if (Flag) { "x".PFlag(); } }
                }
                """, cancellationToken: ct);
            var classes = DependencyReferenceClasses.Of(references, all, new Dictionary<string, string>());
            var droppedSomewhere = new HashSet<string>(StringComparer.Ordinal);
            foreach (var level in new[] { OptimizationLevel.Debug, OptimizationLevel.Release })
            {
                var compilation = CSharpCompilation.Create("DeadCodeProbe", [tree], references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithOptimizationLevel(level));
                var image = new MemoryStream();
                var emit = compilation.Emit(image, cancellationToken: ct);
                emit.Success.Should().BeTrue(string.Join("\n", emit.Diagnostics));
                using (var pe = new PEReader(new MemoryStream(image.ToArray(), writable: false)))
                {
                    var reader = pe.GetMetadataReader();
                    var refs = reader.AssemblyReferences.Select(h => reader.GetString(reader.GetAssemblyReference(h).Name)).ToList();
                    refs.Should().Contain(["DepGraphProbeIfTrue", "DepGraphProbeRuntimeFlag"],
                        $"precondition ({level}): reachable code is in the image");
                    droppedSomewhere.UnionWith(deadShapes.Select(s => s.Assembly).Where(a => !refs.Contains(a)));
                    TestContext.Current.TestOutputHelper?.WriteLine($"{level}: AssemblyRef dropped for [{string.Join(", ", deadShapes.Select(s => s.Assembly).Where(a => !refs.Contains(a)))}]");
                }

                var modules = DependencyEdgeExtraction.Extract(compilation, [tree], "App/P", [], null, classes,
                        () => new MemoryStream(image.ToArray(), writable: false), ct: ct)
                    .Edges.Where(e => e.Kind == DependencyEdgeKinds.Module).Select(e => e.Target).ToList();

                modules.Should().Equal(
                    deadShapes.Select(s => s.Assembly).Append("DepGraphProbeIfTrue").Append("DepGraphProbeRuntimeFlag")
                        .OrderBy(n => n, StringComparer.Ordinal).ToList(),
                    $"({level}) a module used only in constant-unreachable code is still bound by the consumer; "
                    + "a reachable use is edged from the image; an unused module is no edge");
            }
            droppedSomewhere.Should().Contain("DepGraphProbeIfFalse",
                "precondition: the compiler really discards an if (false) body, so the image alone would miss that module");
        }
        finally
        {
            foreach (var path in all)
            {
                try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    [Fact(Timeout = 120_000)]
    public void NegativeControl_AReferenceTheCallerDoesNotNameAsAModule_IsNoModuleEdge()
    {
        var used = EmitModule("DepGraphProbeNotNamed", """
            namespace DepGraphProbe.NotNamed;
            /// <summary>A type a NodeType binds.</summary>
            public sealed class Widget { /// <summary>Its name.</summary>
            public string Name => "w"; }
            """);
        try
        {
            var edges = CompileB(
                [Code(B, "UsesIt", """
                    /// <summary>Binds a referenced assembly the caller did not name as a module.</summary>
                    public static class UsesIt
                    {
                        /// <summary>The widget's name.</summary>
                        public static string Name() => new DepGraphProbe.NotNamed.Widget().Name;
                    }
                    """)],
                sources: null, TestContext.Current.CancellationToken,
                references: [.. CompileReferences.Default, MetadataReference.CreateFromFile(used)],
                moduleAssemblyPaths: null).DependencyEdges!.GraphEdges();
            edges.Edges.Should().BeEmpty("what is a module is NAMED by the caller, never guessed by subtracting a baseline");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(used)!, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact(Timeout = 120_000)]
    public void AForeignFileThatDeclaresNoType_IsStillAnInclusion()
    {
        var edges = CompileB(
            [Code(A, "Notes", "// Lib/A's shared notes: declares nothing."), Code(B, "Own", """
                /// <summary>B's own code.</summary>
                public record Own(int Value);
                """)],
            OwnAndShared, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Should().BeEmpty();
        edges.IncludedWithoutUse.Should().Equal([A], "the file was consumed although it declares no type");
    }

    [Fact(Timeout = 120_000)]
    public void AnIncludeSplicedIntoANamespace_IsAttributedUnderItsNamespacedName()
    {
        var edges = CompileB(
            [AThing(), Code(B, "UsesA", """
                namespace Wrapped
                {
                    @@Lib/A/Source/AThing
                }

                /// <summary>B's own code, binding the included type under its namespaced name.</summary>
                public static class UsesWrapped
                {
                    /// <summary>One.</summary>
                    public static Wrapped.AThing One() => new(1);
                }
                """)],
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Should().ContainSingle().Which.Target.Should().Be(A);
        edges.Edges[0].Evidence.Should().Equal(["Wrapped.AThing"]);
        edges.IncludedWithoutUse.Should().BeEmpty();
    }

    [Fact(Timeout = 120_000)]
    public void AnImplicitBinding_AForeachOverAForeignExtensionEnumerator_IsAnEdge()
    {
        var edges = CompileB(
            [Code(A, "Ranges", """
                using System.Collections.Generic;
                /// <summary>Lib/A's extension enumerator over an int.</summary>
                public static class IntRanges
                {
                    /// <summary>0 .. n-1.</summary>
                    public static IEnumerator<int> GetEnumerator(this int n)
                    {
                        for (var i = 0; i < n; i++)
                            yield return i;
                    }
                }
                """), Code(B, "Sums", """
                /// <summary>B's own code: no name of A's appears, the binding is the foreach pattern.</summary>
                public static class Sums
                {
                    /// <summary>0 + 1 + 2.</summary>
                    public static int UpToThree()
                    {
                        var sum = 0;
                        foreach (var i in 3)
                            sum += i;
                        return sum;
                    }
                }
                """)],
            OwnAndShared, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Should().ContainSingle().Which.Target.Should().Be(A);
        edges.Edges[0].Evidence.Should().Equal(["IntRanges"]);
    }

    [Fact(Timeout = 120_000)]
    public void NegativeControl_AHoistedUsingStaticOfForeignCode_IsNotAUse()
    {
        var edges = CompileB(
            [Code(A, "Helpers", """
                using static AHelpers;
                /// <summary>Lib/A's helpers, imported by its own file.</summary>
                public static class AHelpers
                {
                    /// <summary>One.</summary>
                    public static int One() => 1;
                }
                """), Code(B, "Own", """
                /// <summary>B's own code, binding nothing of A's.</summary>
                public record Own(int Value);
                """)],
            OwnAndShared, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Should().BeEmpty("A's own using directive is hoisted into the skeleton, but an import is not a use");
        edges.IncludedWithoutUse.Should().Equal([A]);
    }

    [Fact(Timeout = 120_000)]
    public void AUserDefinedOperatorOfAThirdOwner_IsAnEdge_ThoughNoNameOfItAppears()
    {
        var edges = CompileB(
            [
                Code(C, "Money", """
                    /// <summary>Lib/C's value with an addition operator.</summary>
                    public readonly record struct Money(int Cents)
                    {
                        /// <summary>The sum, in cents.</summary>
                        public static int operator +(Money a, Money b) => a.Cents + b.Cents;
                    }
                    """),
                Code(A, "Factory", """
                    /// <summary>Lib/A's factory, returning Lib/C's type.</summary>
                    public static class MoneyFactory
                    {
                        /// <summary>One cent.</summary>
                        public static Money Cent() => new(1);
                    }
                    """),
                Code(B, "Adds", """
                    /// <summary>B names only A's factory; the addition binds C's operator.</summary>
                    public static class Adds
                    {
                        /// <summary>Two cents.</summary>
                        public static int Two() => MoneyFactory.Cent() + MoneyFactory.Cent();
                    }
                    """),
            ],
            ["namespace:Source scope:subtree", $"shared=@{A}/Source", $"shared=@{C}/Source"],
            TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Select(e => e.Target).Should().Equal([A, C]);
        edges.Edges.Single(e => e.Target == C).Evidence.Should().Equal(["Money"]);
        edges.IncludedWithoutUse.Should().BeEmpty();
    }

    [Fact(Timeout = 120_000)]
    public void AForeignTypeSplicedIntoTheCompilingTypesClass_IsStillTheForeignOwners()
    {
        var edges = CompileB(
            [AThing(), Code(B, "Host", """
                /// <summary>B's class, hosting A's record.</summary>
                public static class Host
                {
                    @@Lib/A/Source/AThing

                    /// <summary>One.</summary>
                    public static AThing One() => new(1);
                }
                """)],
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Should().ContainSingle().Which.Target.Should().Be(A);
        edges.Edges[0].Evidence.Should().Equal(["Host+AThing"], "the nested identity is looked up innermost first");
    }

    [Fact(Timeout = 120_000)]
    public void AnIncludeSplicedTwice_EveryCopyIsTheIncludedOwners()
    {
        var edges = CompileB(
            [AThing(), Code(B, "Twice", """
                namespace First
                {
                    @@Lib/A/Source/AThing
                }

                namespace Second
                {
                    @@Lib/A/Source/AThing
                }

                /// <summary>B binds only the SECOND copy.</summary>
                public static class UsesSecond
                {
                    /// <summary>One.</summary>
                    public static Second.AThing One() => new(1);
                }
                """)],
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Should().ContainSingle().Which.Target.Should().Be(A);
        edges.Edges[0].Evidence.Should().Equal(["Second.AThing"]);
    }

    private static readonly string[] OwnAndAAndC =
        ["namespace:Source scope:subtree", $"shared=@{A}/Source", $"shared=@{C}/Source"];

    [Fact(Timeout = 120_000)]
    public void AnIndexerOfAThirdOwner_IsAnEdge_PlainAndConditional()
    {
        var edges = CompileB(
            [
                Code(C, "Grid", """
                    /// <summary>Lib/C's indexed type.</summary>
                    public sealed class Grid
                    {
                        /// <summary>The cell.</summary>
                        public int this[int i] => i * 2;
                    }
                    """),
                Code(A, "Factory", """
                    /// <summary>Lib/A's factory, returning Lib/C's type.</summary>
                    public static class GridFactory
                    {
                        /// <summary>A grid.</summary>
                        public static Grid Make() => new();
                    }
                    """),
                Code(B, "Reads", """
                    /// <summary>B names only A's factory; the element access binds C's indexer.</summary>
                    public static class Reads
                    {
                        /// <summary>Plain.</summary>
                        public static int Plain() => GridFactory.Make()[1];
                    }
                    """),
            ],
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();
        var conditional = CompileB(
            [
                Code(C, "Grid", """
                    /// <summary>Lib/C's indexed type.</summary>
                    public sealed class Grid
                    {
                        /// <summary>The cell.</summary>
                        public int this[int i] => i * 2;
                    }
                    """),
                Code(A, "Factory", """
                    /// <summary>Lib/A's factory, returning Lib/C's type.</summary>
                    public static class GridFactory
                    {
                        /// <summary>A grid, or none.</summary>
                        public static Grid? Maybe() => new();
                    }
                    """),
                Code(B, "Reads", """
                    /// <summary>B names only A's factory; the conditional access binds C's indexer.</summary>
                    public static class Reads
                    {
                        /// <summary>Conditional.</summary>
                        public static int? Conditional() => GridFactory.Maybe()?[1];
                    }
                    """),
            ],
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        foreach (var set in new[] { edges, conditional })
        {
            set.Edges.Select(e => e.Target).Should().Equal([A, C]);
            set.Edges.Single(e => e.Target == C).Evidence.Should().Equal(["Grid"]);
            set.IncludedWithoutUse.Should().BeEmpty();
        }
    }

    [Fact(Timeout = 120_000)]
    public void AUsingOfAThirdOwnersDisposable_IsAnEdge_ThoughNoNameOfItAppears()
    {
        var edges = CompileB(
            [
                Code(C, "Lease", """
                    /// <summary>Lib/C's ref struct, disposable by pattern only.</summary>
                    public ref struct Lease
                    {
                        /// <summary>Releases nothing.</summary>
                        public void Dispose() { }
                    }
                    """),
                Code(A, "Factory", """
                    /// <summary>Lib/A's factory, returning Lib/C's type.</summary>
                    public static class LeaseFactory
                    {
                        /// <summary>A lease.</summary>
                        public static Lease Make() => new();
                    }
                    """),
                Code(B, "Holds", """
                    /// <summary>B names only A's factory; the using statement calls C's Dispose.</summary>
                    public static class Holds
                    {
                        /// <summary>Takes and releases.</summary>
                        public static int Once()
                        {
                            using (LeaseFactory.Make())
                            {
                            }
                            return 1;
                        }
                    }
                    """),
            ],
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Select(e => e.Target).Should().Equal([A, C]);
        edges.Edges.Single(e => e.Target == C).Evidence.Should().Equal(["Lease"]);
    }

    [Fact(Timeout = 120_000)]
    public void AnAliasOverAForeignTypeArgument_IsAnEdge()
    {
        var edges = CompileB(
            [AThing(), Code(B, "Aliased", """
                using AList = System.Collections.Generic.List<AThing>;

                /// <summary>B uses A's type only through an alias over an external generic.</summary>
                public static class Aliased
                {
                    /// <summary>How many.</summary>
                    public static int Count(AList list) => list.Count;
                }
                """)],
            OwnAndShared, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Should().ContainSingle().Which.Target.Should().Be(A,
            "the alias binds List<T>, and only its type argument names A");
        edges.Edges[0].Evidence.Should().Equal(["AThing"]);
        edges.IncludedWithoutUse.Should().BeEmpty();
    }

    [Fact(Timeout = 120_000)]
    public void IdenticalSnippetsOfTwoOwners_NameBothOwners_NeverOnlyTheLastOne()
    {
        const string snippet = """
            /// <summary>The same text, shipped by two owners.</summary>
            public record Twin(int Value);
            """;
        var edges = CompileB(
            [Code(A, "Twin", snippet), Code(C, "Twin", snippet), Code(B, "UsesA", """
                namespace FromA
                {
                    @@Lib/A/Source/Twin
                }

                namespace FromC
                {
                    @@Lib/C/Source/Twin
                }

                /// <summary>B binds only the copy A supplied.</summary>
                public static class UsesFromA
                {
                    /// <summary>One.</summary>
                    public static FromA.Twin One() => new(1);
                }
                """)],
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Select(e => e.Target).Should().Contain(A,
            "text cannot tell the two copies apart, so the edge to A must not be lost to C's copy");
        edges.Edges.Select(e => e.Target).Should().BeSubsetOf([A, C],
            "the ambiguity may cost an extra edge to C, never one to anybody else");
    }

    [Fact(Timeout = 120_000)]
    public void IdenticalSnippetsOfTheCompilingTypeAndAForeignOwner_StillNameTheForeignOwner()
    {
        const string snippet = """
            /// <summary>The same text, shipped by B itself and by A.</summary>
            public record Twin(int Value);
            """;
        var edges = CompileB(
            [Code(A, "Twin", snippet), Code(B, "Twin", snippet), Code(B, "UsesA", """
                namespace FromA
                {
                    @@Lib/A/Source/Twin
                }

                namespace FromB
                {
                    @@App/B/Source/Twin
                }

                /// <summary>B binds the copy A supplied.</summary>
                public static class UsesFromA
                {
                    /// <summary>One.</summary>
                    public static FromA.Twin One() => new(1);
                }
                """)],
            sources: ["namespace:Source scope:subtree"], TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        edges.Edges.Select(e => e.Target).Should().Equal([A],
            "the self owner is dropped from the ambiguous set, never the foreign one beside it");
    }

    [Fact(Timeout = 120_000)]
    public void ADelegateInvocation_AWithExpression_AndAForeachElementConversion_AreEdges()
    {
        var edges = CompileB(
            [
                Code(C, "Shapes", """
                    /// <summary>Lib/C's delegate.</summary>
                    public delegate int Doubler(int x);
                    /// <summary>Lib/C's record.</summary>
                    public record Point(int X);
                    /// <summary>Lib/C's value, convertible to int.</summary>
                    public readonly record struct Cents(int Value)
                    {
                        /// <summary>To int.</summary>
                        public static explicit operator int(Cents c) => c.Value;
                    }
                    """),
                Code(A, "Factory", """
                    using System.Collections.Generic;
                    /// <summary>Lib/A's factory, returning Lib/C's types.</summary>
                    public static class Make
                    {
                        /// <summary>A doubler.</summary>
                        public static Doubler Twice() => x => x * 2;
                        /// <summary>A point.</summary>
                        public static Point Origin() => new(0);
                        /// <summary>Some cents.</summary>
                        public static IEnumerable<Cents> Coins() => [new(1), new(2)];
                    }
                    """),
                Code(B, "Uses", """
                    /// <summary>B names only A's factory.</summary>
                    public static class Uses
                    {
                        /// <summary>Invokes C's delegate.</summary>
                        public static int Four() => Make.Twice()(2);
                    }
                    """),
            ],
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();
        var with = CompileB(
            [
                Code(C, "Shapes", """
                    /// <summary>Lib/C's record.</summary>
                    public record Point(int X);
                    """),
                Code(A, "Factory", """
                    /// <summary>Lib/A's factory, returning Lib/C's record.</summary>
                    public static class Make
                    {
                        /// <summary>A point.</summary>
                        public static Point Origin() => new(0);
                    }
                    """),
                Code(B, "Uses", """
                    /// <summary>B names only A's factory.</summary>
                    public static class Uses
                    {
                        /// <summary>Clones C's record.</summary>
                        public static object Copy() => Make.Origin() with { };
                    }
                    """),
            ],
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();
        var conversion = CompileB(
            [
                Code(C, "Cents", """
                    /// <summary>Lib/C's value, convertible to int.</summary>
                    public readonly record struct Cents(int Value)
                    {
                        /// <summary>To int.</summary>
                        public static explicit operator int(Cents c) => c.Value;
                    }
                    """),
                Code(A, "Factory", """
                    using System.Collections.Generic;
                    /// <summary>Lib/A's factory, returning Lib/C's values.</summary>
                    public static class Make
                    {
                        /// <summary>Some cents.</summary>
                        public static IEnumerable<Cents> Coins() => [new(1), new(2)];
                    }
                    """),
                Code(B, "Uses", """
                    /// <summary>B names only A's factory and int.</summary>
                    public static class Uses
                    {
                        /// <summary>Sums them.</summary>
                        public static int Sum()
                        {
                            var sum = 0;
                            foreach (int value in Make.Coins())
                                sum += value;
                            return sum;
                        }
                    }
                    """),
            ],
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!.GraphEdges();

        foreach (var (set, evidence) in new[] { (edges, "Doubler"), (with, "Point"), (conversion, "Cents") })
        {
            set.Edges.Select(e => e.Target).Should().Equal([A, C]);
            set.Edges.Single(e => e.Target == C).Evidence.Should().Contain(evidence);
            set.IncludedWithoutUse.Should().BeEmpty();
        }
    }

    [Fact]
    public void DeclaredTypeNames_SpellNestedGenericAndNamespacedTypesAsTheBoundSymbolDoes()
    {
        const string code = """
            namespace Outer.Inner
            {
                public class Box<T> { public class Lid { } }
                public delegate void Handler<T1, T2>(T1 a, T2 b);
                public enum Colour { Red }
            }
            public record Top;
            namespace @Escaped.\u0041lso
            {
                public class Spelled { }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(code);
        var declared = DependencyEdgeExtraction.DeclaredTypeNames(tree.GetRoot()).ToImmutableSortedSet(StringComparer.Ordinal);

        var compilation = CSharpCompilation.Create("probe", [tree], CompileReferences.Default);
        var bound = compilation.SyntaxTrees.SelectMany(t => t.GetRoot().DescendantNodes())
            .Select(n => compilation.GetSemanticModel(n.SyntaxTree).GetDeclaredSymbol(n))
            .OfType<INamedTypeSymbol>()
            .Select(DependencyEdgeExtraction.MetadataNameOf)
            .ToImmutableSortedSet(StringComparer.Ordinal);

        declared.Should().Equal(bound, "attribution matches a parse-time name to a bind-time name — they must be spelled alike");
        declared.Should().Contain(["Outer.Inner.Box`1", "Outer.Inner.Box`1+Lid", "Outer.Inner.Handler`2", "Outer.Inner.Colour", "Top",
            "Escaped.Also.Spelled"]);
    }

    [Theory]
    [InlineData("Lib/A/Source/AThing", "Lib/A")]
    [InlineData("Lib/A/Test/AThingTest", "Lib/A")]
    [InlineData("Lib/A/Source/Deep/Nested", "Lib/A")]
    [InlineData("Lib/Common/Helpers", null)]
    [InlineData("Source/Top", null)]
    public void OwnerOfCodePath_IsEverythingBeforeTheFirstSourceOrTestSegment(string path, string? owner) =>
        CodeConventions.OwnerOfCodePath(path).Should().Be(owner);

    private static string MvidOf(string dllPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("N");
    }

    /// <summary>Emits a small module assembly to its own temp directory and returns its path.</summary>
    private static string EmitModule(string name, string code)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mw-depedges-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".dll");
        var result = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(code)], CompileReferences.Default,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .Emit(path, cancellationToken: TestContext.Current.CancellationToken);
        result.Success.Should().BeTrue(string.Join("\n", result.Diagnostics));
        return path;
    }
}
