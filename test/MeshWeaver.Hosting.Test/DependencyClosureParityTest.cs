using System;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The in-memory dependency closure (<see cref="DependencyClosure"/>) answers every
/// <see cref="DependencyClosureCorpus"/> case exactly — membership, depth and path — the corpus the
/// PostgreSQL closure table is held to as well (core <c>Doc/Architecture/DependencyGraph</c>).
/// </summary>
public class DependencyClosureParityTest
{
    public static TheoryData<int> CaseIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < DependencyClosureCorpus.Cases.Length; i++)
            data.Add(i);
        return data;
    }

    private static string Render(DependencyClosureRow row) => $"{row.Node}|{row.Depth}|{string.Join(">", row.Path)}";

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void EveryCorpusCase_IsAnsweredExactly(int index)
    {
        var @case = DependencyClosureCorpus.Cases[index];
        var closure = DependencyClosure.Of(@case.Edges);
        var rows = @case.Direction switch
        {
            "dependents" => closure.Dependents(@case.Start, @case.MaxDepth),
            "dependencies" => closure.Dependencies(@case.Start, @case.MaxDepth),
            _ => throw new ArgumentOutOfRangeException(nameof(index), @case.Direction, "unknown direction"),
        };
        rows.Select(Render).Should().Equal(@case.Expected, @case.Note);
    }

    [Fact]
    public void Path_FollowsTheSameNextHops_AndIsNullWhereThereIsNoDependency()
    {
        var closure = DependencyClosure.Of(DependencyClosureCorpus.Diamond);
        closure.Path("u", "w").Should().NotBeNull();
        closure.Path("u", "w")!.Value.Should().Equal("u", "x", "y", "w");
        closure.Path("w", "u").Should().BeNull("w does not depend on u");
        closure.Path("x", "x")!.Value.Should().Equal("x");
    }

    [Fact]
    public void NoNodeIsItsOwnDependent_EvenInACycle_AndSelfEdgesAreIgnored()
    {
        var closure = DependencyClosure.Of([.. DependencyClosureCorpus.Cycle, ("e", "e")]);
        foreach (var node in new[] { "a", "b", "c", "d", "e" })
        {
            closure.Dependents(node).Should().NotContain(r => r.Node == node);
            closure.Dependencies(node).Should().NotContain(r => r.Node == node);
        }
        closure.Dependents("e").Should().BeEmpty("a self-edge is not a dependency");
    }

    [Fact]
    public void ARandomGraph_DependentsAndDependenciesAreMirrorImages()
    {
        // Every (A depends-on-transitively B) pair appears once from each side with the same depth —
        // a control that the two directions are computed from one rule, not two.
        var random = new Random(20261026);
        var edges = Enumerable.Range(0, 60)
            .Select(_ => ("n" + random.Next(15), "n" + random.Next(15)))
            .ToImmutableArray();
        var closure = DependencyClosure.Of(edges);
        var nodes = Enumerable.Range(0, 15).Select(i => "n" + i).ToArray();
        var forward = nodes.SelectMany(s => closure.Dependencies(s).Select(r => (Dependent: s, Dependency: r.Node, r.Depth)));
        var backward = nodes.SelectMany(t => closure.Dependents(t).Select(r => (Dependent: r.Node, Dependency: t, r.Depth)));
        forward.OrderBy(x => x.Dependent, StringComparer.Ordinal).ThenBy(x => x.Dependency, StringComparer.Ordinal)
            .Should().Equal(backward.OrderBy(x => x.Dependent, StringComparer.Ordinal).ThenBy(x => x.Dependency, StringComparer.Ordinal));
        foreach (var s in nodes)
            foreach (var row in closure.Dependencies(s))
            {
                row.Path.First().Should().Be(s);
                row.Path.Last().Should().Be(row.Node);
                row.Path.Length.Should().Be(row.Depth + 1, "a path of depth d has d edges");
            }
    }
}
