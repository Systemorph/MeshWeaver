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
            return NodeSetCompiler.Compile(
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

            var edges = compiled.DependencyEdges;
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
            OwnAndShared, TestContext.Current.CancellationToken).DependencyEdges!;

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
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!;

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
            TestContext.Current.CancellationToken).DependencyEdges!;

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
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!;

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
                moduleAssemblyPaths: [used, unused]).DependencyEdges!;

            var module = edges.Edges.Should().ContainSingle(
                "the unused module is referenced but never bound — GetUsedAssemblyReferences prunes it").Which;
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
                moduleAssemblyPaths: null).DependencyEdges!;
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
            OwnAndShared, TestContext.Current.CancellationToken).DependencyEdges!;

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
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!;

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
            OwnAndShared, TestContext.Current.CancellationToken).DependencyEdges!;

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
            OwnAndShared, TestContext.Current.CancellationToken).DependencyEdges!;

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
            TestContext.Current.CancellationToken).DependencyEdges!;

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
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!;

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
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!;

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
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!;
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
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!;

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
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!;

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
            OwnAndShared, TestContext.Current.CancellationToken).DependencyEdges!;

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
            sources: null, TestContext.Current.CancellationToken).DependencyEdges!;

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
            sources: ["namespace:Source scope:subtree"], TestContext.Current.CancellationToken).DependencyEdges!;

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
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!;
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
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!;
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
            OwnAndAAndC, TestContext.Current.CancellationToken).DependencyEdges!;

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
