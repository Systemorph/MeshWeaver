using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #727 defect 1: the markdown node's side menu must honour <see cref="MeshNode.Order"/> (nulls
/// last, per the field's contract) then name — not sort by name only, which disagreed with the
/// graph navigator and every other child list in the codebase.
/// </summary>
public class MarkdownSideMenuOrderTest
{
    [Fact]
    public void OrderSubNodes_SortsByOrderThenName_WithNullsLast()
    {
        // Names deliberately sort OPPOSITE to Order, so a name-only sort produces a different result.
        var children = new[]
        {
            new MeshNode("c", "ns") { Name = "Zulu", Order = 10 },
            new MeshNode("b", "ns") { Name = "Yankee", Order = 20 },
            new MeshNode("a", "ns") { Name = "Xray", Order = 30 },
            new MeshNode("m", "ns") { Name = "Bravo" },   // no Order -> last
            new MeshNode("n", "ns") { Name = "Alpha" },   // no Order -> last, alphabetical among nulls
        };

        var ordered = MarkdownOverviewLayoutArea.OrderSubNodes(children);

        ordered.Select(n => n.Name).Should()
            .Equal("Zulu", "Yankee", "Xray", "Alpha", "Bravo");
    }
}
