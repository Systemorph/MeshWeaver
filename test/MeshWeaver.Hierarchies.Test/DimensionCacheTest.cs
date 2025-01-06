using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Execution;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.TestDomain.SimpleData;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hierarchies.Test;

public class DimensionCacheTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data =>
                data.FromConfigurableDataSource(
                    "Test Data",
                    dataSource =>
                        dataSource
                            .WithType<TestHierarchicalDimensionA>(type =>
                                type.WithInitialData(TestHierarchicalDimensionA.Data)
                            )
                            .WithType<TestHierarchicalDimensionB>(type =>
                                type.WithInitialData(TestHierarchicalDimensionB.Data)
                            )
                )
            );
    }

    private async Task<DimensionCache> GetDimensionCacheAsync()
    {
        var workspace = GetHost().GetWorkspace();
        var stream = workspace.GetStream(
            typeof(TestHierarchicalDimensionA),
            typeof(TestHierarchicalDimensionB)
        );

        var ci = await ((IObservable<ChangeItem<EntityStore>>)stream).FirstAsync();
        return new(workspace, ci.Value);
    }

    [Fact]
    public async Task InitializationTest()
    {
        var hierarchies = await GetDimensionCacheAsync();
        using (new AssertionScope())
        {
            hierarchies.GetHierarchy<TestHierarchicalDimensionA>().Should().NotBeNull();
            hierarchies.Get<TestHierarchicalDimensionA>("A111").Should().NotBeNull();
            hierarchies.Get<TestHierarchicalDimensionA>("A_Unknown").Should().BeNull();
            hierarchies.Get<TestHierarchicalDimensionA>(null).Should().BeNull();
        }
    }


    [Fact]
    public async Task LeafLevelTest()
    {
        var hierarchies = await GetDimensionCacheAsync();
        using (new AssertionScope())
        {
            hierarchies.Parent<TestHierarchicalDimensionA>("A111").SystemName.Should().Be("A11");
            hierarchies
                .AncestorAtLevel<TestHierarchicalDimensionA>("A111", 0)
                .SystemName.Should()
                .Be("A1");
            hierarchies
                .AncestorAtLevel<TestHierarchicalDimensionA>("A111", 1)
                .SystemName.Should()
                .Be("A11");
            hierarchies
                .AncestorAtLevel<TestHierarchicalDimensionA>("A112", 1)
                .SystemName.Should()
                .Be("A11");
            hierarchies
                .AncestorAtLevel<TestHierarchicalDimensionA>("A111", 2)
                .SystemName.Should()
                .Be("A111");
            hierarchies.AncestorAtLevel<TestHierarchicalDimensionA>("A111", 3).Should().BeNull();
        }
    }

    [Fact]
    public async Task IntermediateLevelTest()
    {
        var hierarchies = await GetDimensionCacheAsync();
        using (new AssertionScope())
        {
            hierarchies.Parent<TestHierarchicalDimensionA>("A11").SystemName.Should().Be("A1");
            hierarchies
                .AncestorAtLevel<TestHierarchicalDimensionA>("A11", 0)
                .SystemName.Should()
                .Be("A1");
            hierarchies
                .AncestorAtLevel<TestHierarchicalDimensionA>("A11", 1)
                .SystemName.Should()
                .Be("A11");
            hierarchies.AncestorAtLevel<TestHierarchicalDimensionA>("A11", 2).Should().BeNull();
        }
    }

    [Fact]
    public async Task RootLevelTest()
    {
        var hierarchies = await GetDimensionCacheAsync();
        using (new AssertionScope())
        {
            hierarchies.Parent<TestHierarchicalDimensionA>("A1").Should().BeNull();
            hierarchies
                .AncestorAtLevel<TestHierarchicalDimensionA>("A1", 0)
                .SystemName.Should()
                .Be("A1");
            hierarchies.AncestorAtLevel<TestHierarchicalDimensionA>("A1", 2).Should().BeNull();
        }
    }

    [Fact]
    public async Task LevelTest()
    {
        var hierarchies = await GetDimensionCacheAsync();
        using (new AssertionScope())
        {
            hierarchies.GetHierarchy<TestHierarchicalDimensionA>("A1").Level.Should().Be(0);
            hierarchies.GetHierarchy<TestHierarchicalDimensionA>("A11").Level.Should().Be(1);
            hierarchies.GetHierarchy<TestHierarchicalDimensionA>("A11").Level.Should().Be(1);
            hierarchies.GetHierarchy<TestHierarchicalDimensionA>("A111").Level.Should().Be(2);
            hierarchies.GetHierarchy<TestHierarchicalDimensionA>("A112").Level.Should().Be(2);
        }
    }

    [Fact]
    public async Task SeveralDimensionsLevelTest()
    {
        var hierarchies = await GetDimensionCacheAsync();
        using (new AssertionScope())
        {
            hierarchies.GetHierarchy<TestHierarchicalDimensionA>("A1").Level.Should().Be(0);
            hierarchies.GetHierarchy<TestHierarchicalDimensionA>("A11").Level.Should().Be(1);
            hierarchies.GetHierarchy<TestHierarchicalDimensionA>("A111").Level.Should().Be(2);
            hierarchies.GetHierarchy<TestHierarchicalDimensionB>("B1").Level.Should().Be(0);
            hierarchies.GetHierarchy<TestHierarchicalDimensionB>("B11").Level.Should().Be(1);
            hierarchies.GetHierarchy<TestHierarchicalDimensionB>("B111").Level.Should().Be(2);
        }
    }

    [Fact]
    public async Task HierarchyApiTest()
    {
        var hierarchies = await GetDimensionCacheAsync();
        var hierarchyA = hierarchies.GetHierarchy<TestHierarchicalDimensionA>();
        hierarchyA.Get("A11").Parent.Should().Be(hierarchyA.Get("A12").Parent);
    }
}
