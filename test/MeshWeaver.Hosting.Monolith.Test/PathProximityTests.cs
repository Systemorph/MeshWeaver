using FluentAssertions;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class PathProximityTests
{
    [Theory]
    [InlineData(new[] { "A", "B" }, new[] { "A", "B" }, 2)]
    [InlineData(new[] { "A", "B" }, new[] { "A", "C" }, 1)]
    [InlineData(new[] { "A", "B" }, new[] { "X", "Y" }, 0)]
    [InlineData(new string[0], new[] { "A" }, 0)]
    [InlineData(new[] { "A" }, new string[0], 0)]
    [InlineData(new string[0], new string[0], 0)]
    [InlineData(new[] { "Systemorph", "Marketing" }, new[] { "Systemorph", "Marketing" }, 2)]
    [InlineData(new[] { "Systemorph", "Marketing" }, new[] { "Systemorph", "Projects" }, 1)]
    [InlineData(new[] { "Systemorph", "Marketing" }, new[] { "ACME", "Projects" }, 0)]
    public void LongestCommonPrefixLength_ReturnsExpected(string[] a, string[] b, int expected)
    {
        PathProximity.LongestCommonPrefixLength(a, b).Should().Be(expected);
    }

    [Theory]
    [InlineData(new[] { "A", "B" }, new[] { "A", "B" }, 0)]       // Same node
    [InlineData(new[] { "A", "B" }, new[] { "A", "B", "C" }, 1)]  // Child
    [InlineData(new[] { "A", "B" }, new[] { "A", "C" }, 2)]       // Sibling
    [InlineData(new[] { "Systemorph", "Marketing" }, new[] { "ACME", "Projects" }, 4)] // Distant
    [InlineData(new string[0], new[] { "A", "B" }, 2)]            // Root to depth 2
    public void SegmentDistance_ReturnsExpected(string[] a, string[] b, int expected)
    {
        PathProximity.SegmentDistance(a, b).Should().Be(expected);
    }

    [Fact]
    public void ComputeBoost_NullContext_ReturnsZero()
    {
        PathProximity.ComputeBoost(null, "Systemorph/Projects").Should().Be(0);
    }

    [Fact]
    public void ComputeBoost_EmptyContext_ReturnsZero()
    {
        PathProximity.ComputeBoost("", "Systemorph/Projects").Should().Be(0);
    }

    [Fact]
    public void ComputeBoost_SameNode_ReturnsMaxBoost()
    {
        PathProximity.ComputeBoost("Systemorph/Marketing", "Systemorph/Marketing")
            .Should().Be(PathProximity.MaxBoost);
    }

    [Theory]
    [InlineData("Systemorph/Marketing", "Systemorph/Marketing", 40.0)]         // distance=0
    [InlineData("Systemorph/Marketing", "Systemorph/Marketing/Campaign1", 20.0)] // distance=1
    [InlineData("Systemorph/Marketing", "Systemorph/Projects", 13.333333333333334)] // distance=2
    [InlineData("Systemorph/Marketing", "Demos/ACME/Projects", 6.666666666666667)]   // distance=5 (3-segment result)
    public void ComputeBoost_ProximityExamples(string context, string result, double expected)
    {
        PathProximity.ComputeBoost(context, result).Should().BeApproximately(expected, 0.01);
    }

    [Fact]
    public void ComputeBoost_CloserResultGetsHigherBoost()
    {
        var context = "Systemorph/Marketing";
        var closeBoost = PathProximity.ComputeBoost(context, "Systemorph/Projects");
        var farBoost = PathProximity.ComputeBoost(context, "Demos/ACME/Projects");

        closeBoost.Should().BeGreaterThan(farBoost);
    }

    [Fact]
    public void ComputeBoost_NullResultPath_ReturnsBoost()
    {
        // Root-level node (empty path) should still get some boost
        var boost = PathProximity.ComputeBoost("Systemorph/Marketing", null);
        boost.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ComputeBoost_NeverExceedsMaxBoost()
    {
        PathProximity.ComputeBoost("A", "A").Should().BeLessThanOrEqualTo(PathProximity.MaxBoost);
        PathProximity.ComputeBoost("A/B/C", "A/B/C").Should().BeLessThanOrEqualTo(PathProximity.MaxBoost);
    }

    [Fact]
    public void LongestCommonPrefixLength_IsCaseInsensitive()
    {
        PathProximity.LongestCommonPrefixLength(
            new[] { "Systemorph" }, new[] { "systemorph" }).Should().Be(1);
    }
}
