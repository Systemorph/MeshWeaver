#pragma warning disable CS1591

using MeshWeaver.Compiler;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The compile's source ORDER is a function of the SOURCE SET, never of how it was delivered.
///
/// <para>The join order is part of the emitted bytes — <c>NodeCompileShaping.CombineSources</c>
/// concatenates in this order — and it used to be whatever order the snapshot arrived in. On the
/// mesh path that delivery is <c>ImmutableDictionary.Values</c> (see
/// <c>MeshNodeCompilationService</c>'s source probe), i.e. hash-bucket order over string hashes
/// that .NET RANDOMISES PER PROCESS. So the mesh compiled the same content into a different
/// assembly on every process: not reproducible against the compiler-driven bake, and not
/// reproducible against itself. It is why the assembly store's digest of the emitted bytes could
/// never dedupe, and it made the generated-input content key move on every compile.</para>
///
/// <para>Pinned here as a unit test as well as in <c>BakeEquivalenceTest</c>, because the
/// equivalence test is an expensive integration test in a different project and this property is
/// cheap to state: a reversed delivery must produce an identical compile unit.</para>
/// </summary>
public class CompileSourceOrderTest
{
    private static MeshNode Code(string path, string code) =>
        new(path[(path.LastIndexOf('/') + 1)..], path[..path.LastIndexOf('/')])
        {
            NodeType = "Code",
            Content = new CodeConfiguration { Code = code, Language = "csharp" },
        };

    [Fact]
    public void CollectCompileSources_OrdersByPath_WhateverOrderTheSnapshotDelivered()
    {
        MeshNode[] delivered =
        [
            Code("Widget/Thing/Test/ThingTests", "class T {}"),
            Code("Lib/Shared/Source/Helper", "class H {}"),
            Code("Widget/Thing/Source/Sub/Deep", "class D {}"),
            Code("Widget/Thing/Source/Thing", "class Th {}"),
        ];

        var (_, matched) = NodeCompileShaping.CollectCompileSources(
            delivered, "Widget/Thing", NullLogger.Instance);

        matched.Should().Equal(
            "Lib/Shared/Source/Helper",
            "Widget/Thing/Source/Sub/Deep",
            "Widget/Thing/Source/Thing",
            "Widget/Thing/Test/ThingTests");
    }

    [Fact]
    public void TheCombinedCompileUnit_IsIdentical_UnderAnyDeliveryOrder()
    {
        MeshNode[] ascending =
        [
            Code("Pkg/T/Source/A", "class A {}"),
            Code("Pkg/T/Source/B", "class B {}"),
            Code("Pkg/T/Source/C", "class C {}"),
        ];
        var descending = ascending.Reverse().ToArray();

        var one = NodeCompileShaping.CombineSources(
            NodeCompileShaping.CollectCompileSources(ascending, "Pkg/T", NullLogger.Instance).Sources);
        var other = NodeCompileShaping.CombineSources(
            NodeCompileShaping.CollectCompileSources(descending, "Pkg/T", NullLogger.Instance).Sources);

        one!.Code.Should().Be(other!.Code);
        // …and the order is the SET's, not the first caller's.
        one.Code.Should().Be("class A {}\n\nclass B {}\n\nclass C {}");
    }
}
