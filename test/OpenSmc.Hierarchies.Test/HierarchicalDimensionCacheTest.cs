using FluentAssertions;
using FluentAssertions.Execution;
using OpenSmc.TestDomain;
using OpenSmc.TestDomain.SimpleData;
using Xunit;

namespace OpenSmc.Hierarchies.Test;

public class HierarchicalDimensionCacheTest
{
    [Fact]
    public void InitializationTest()
    {
        var querySource = new StaticDataFieldQuerySource();
        var hierarchies = querySource.ToHierarchicalDimensionCache();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>().Should().BeNull();
            hierarchies.Get<TestHierarchicalDimensionA>("A111").Should().BeNull();
        }
        hierarchies.Initialize<TestHierarchicalDimensionA>();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>().Should().NotBeNull();
            hierarchies.Get<TestHierarchicalDimensionA>("A111").Should().NotBeNull();
            hierarchies.Get<TestHierarchicalDimensionA>("A_Unknown").Should().BeNull();
            hierarchies.Get<TestHierarchicalDimensionA>(null).Should().BeNull();
        }
    }

    [Fact]
    public void LeafLevelTest()
    {
        var querySource = new StaticDataFieldQuerySource();
        var hierarchies = querySource.ToHierarchicalDimensionCache();
        hierarchies.Initialize<TestHierarchicalDimensionA>();
        hierarchies.Initialize<TestHierarchicalDimensionB>();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>("A111").Parent().SystemName.Should().Be("A11");
            hierarchies.Get<TestHierarchicalDimensionA>("A111").AncestorAtLevel(0).SystemName.Should().Be("A1");
            hierarchies.Get<TestHierarchicalDimensionA>("A111").AncestorAtLevel(1).SystemName.Should().Be("A11");
            hierarchies.Get<TestHierarchicalDimensionA>("A112").AncestorAtLevel(1).SystemName.Should().Be("A11");
            hierarchies.Get<TestHierarchicalDimensionA>("A111").AncestorAtLevel(2).SystemName.Should().Be("A111");
            hierarchies.Get<TestHierarchicalDimensionA>("A111").AncestorAtLevel(3).Should().BeNull();
            hierarchies.Get<TestHierarchicalDimensionA>("A111").Children().Should().BeEmpty();
        }
    }

    [Fact]
    public void IntermediateLevelTest()
    {
        var querySource = new StaticDataFieldQuerySource();
        var hierarchies = querySource.ToHierarchicalDimensionCache();
        hierarchies.Initialize<TestHierarchicalDimensionA>();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>("A11").Parent().SystemName.Should().Be("A1");
            hierarchies.Get<TestHierarchicalDimensionA>("A11").AncestorAtLevel(0).SystemName.Should().Be("A1");
            hierarchies.Get<TestHierarchicalDimensionA>("A11").AncestorAtLevel(1).SystemName.Should().Be("A11");
            hierarchies.Get<TestHierarchicalDimensionA>("A11").AncestorAtLevel(2).Should().BeNull();
            hierarchies.Get<TestHierarchicalDimensionA>("A11").Children().Select(x => x.SystemName).Should()
                       .BeEquivalentTo(new List<string> { "A111", "A112" });
        }
    }

    [Fact]
    public void RootLevelTest()
    {
        var querySource = new StaticDataFieldQuerySource();
        var hierarchies = querySource.ToHierarchicalDimensionCache();
        hierarchies.Initialize<TestHierarchicalDimensionA>();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>("A1").Parent().Should().BeNull();
            hierarchies.Get<TestHierarchicalDimensionA>("A1").Ancestors().Should().BeEmpty();
            hierarchies.Get<TestHierarchicalDimensionA>("A1").AncestorAtLevel(0).SystemName.Should().Be("A1");
            hierarchies.Get<TestHierarchicalDimensionA>("A1").AncestorAtLevel(2).Should().BeNull();
            hierarchies.Get<TestHierarchicalDimensionA>("A1").Children().Select(x => x.SystemName).Should()
                       .BeEquivalentTo(new List<string> { "A11", "A12" });
            hierarchies.Get<TestHierarchicalDimensionA>().Children(null).Select(x => x.SystemName).Should()
                       .BeEquivalentTo(new List<string> { "A1", "A2" });
        }
    }

    [Fact]
    public void LevelTest()
    {
        var querySource = new StaticDataFieldQuerySource();
        var hierarchies = querySource.ToHierarchicalDimensionCache();
        hierarchies.Initialize<TestHierarchicalDimensionA>();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>("A1").Level().Should().Be(0);
            hierarchies.Get<TestHierarchicalDimensionA>("A11").Level().Should().Be(1);
            hierarchies.Get<TestHierarchicalDimensionA>("A11").Level().Should().Be(1);
            hierarchies.Get<TestHierarchicalDimensionA>("A111").Level().Should().Be(2);
            hierarchies.Get<TestHierarchicalDimensionA>("A112").Level().Should().Be(2);
        }
    }

    [Fact]
    public void SeveralDimensionsLevelTest()
    {
        var querySource = new StaticDataFieldQuerySource();
        var hierarchies = querySource.ToHierarchicalDimensionCache();
        hierarchies.Initialize<TestHierarchicalDimensionA>();
        hierarchies.Initialize<TestHierarchicalDimensionB>();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>("A1").Level().Should().Be(0);
            hierarchies.Get<TestHierarchicalDimensionA>("A11").Level().Should().Be(1);
            hierarchies.Get<TestHierarchicalDimensionA>("A111").Level().Should().Be(2);
            hierarchies.Get<TestHierarchicalDimensionB>("B1").Level().Should().Be(0);
            hierarchies.Get<TestHierarchicalDimensionB>("B11").Level().Should().Be(1);
            hierarchies.Get<TestHierarchicalDimensionB>("B111").Level().Should().Be(2);
        }
    }

    [Fact]
    public void AncestorsTest()
    {
        var querySource = new StaticDataFieldQuerySource();
        var hierarchies = querySource.ToHierarchicalDimensionCache();
        hierarchies.Initialize<TestHierarchicalDimensionA>();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>("A112").Ancestors().Select(x => x.SystemName).Should()
                       .BeEquivalentTo(hierarchies.Get<TestHierarchicalDimensionA>("A111").Ancestors().Select(x => x.SystemName))
                       .And
                       .BeEquivalentTo(new List<string> { "A1", "A11" });
            hierarchies.Get<TestHierarchicalDimensionA>("A111").Ancestors(includeSelf: true).Select(x => x.SystemName).Should()
                       .BeEquivalentTo(new List<string> { "A1", "A11", "A111" });
        }
    }

    [Fact]
    public void DescendantsTest()
    {
        var querySource = new StaticDataFieldQuerySource();
        var hierarchies = querySource.ToHierarchicalDimensionCache();
        hierarchies.Initialize<TestHierarchicalDimensionA>();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>("A112").Descendants().Select(x => x.SystemName).Should()
                       .BeEquivalentTo(hierarchies.Get<TestHierarchicalDimensionA>("A111").Descendants().Select(x => x.SystemName))
                       .And
                       .BeEmpty();
            hierarchies.Get<TestHierarchicalDimensionA>("A1").Descendants(includeSelf: true).Select(x => x.SystemName).Should()
                       .BeEquivalentTo(new List<string> { "A1", "A11", "A12", "A111", "A112" });
        }
    }

    [Fact]
    public void DescendantsAtLevelTest()
    {
        var querySource = new StaticDataFieldQuerySource();
        var hierarchies = querySource.ToHierarchicalDimensionCache();
        hierarchies.Initialize<TestHierarchicalDimensionA>();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>("A1").DescendantsAtLevel(2).Select(x => x.SystemName).Should()
                       .BeEquivalentTo(new List<string> { "A111", "A112" });
            hierarchies.Get<TestHierarchicalDimensionA>("A1").DescendantsAtLevel(0).Select(x => x.SystemName).Should()
                       .BeEquivalentTo(new List<string> { "A1" });
            hierarchies.Get<TestHierarchicalDimensionA>("A11").DescendantsAtLevel(0).Should().BeEmpty();
        }
    }
    
    [Fact]
    public void SiblingsTest()
    {
        var querySource = new StaticDataFieldQuerySource();
        var hierarchies = querySource.ToHierarchicalDimensionCache();
        hierarchies.Initialize<TestHierarchicalDimensionA>();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>("A112").Siblings().Select(x => x.SystemName).Should()
                       .BeEquivalentTo(new List<string> { "A111" });
            hierarchies.Get<TestHierarchicalDimensionA>("A112").Siblings(includeSelf: true).Select(x => x.SystemName).Should()
                       .BeEquivalentTo(new List<string> { "A111", "A112" });
        }
    }

    [Fact]
    public void HierarchyApiTest()
    {
        var querySource = new StaticDataFieldQuerySource();
        var hierarchies = querySource.ToHierarchicalDimensionCache();
        hierarchies.Initialize<TestHierarchicalDimensionA>();
        var hierarchyA = hierarchies.Get<TestHierarchicalDimensionA>();
        hierarchyA.Get("A11").Parent.Should().Be(hierarchyA.Get("A12").Parent);
    }
}