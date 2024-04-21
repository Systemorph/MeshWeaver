using FluentAssertions;
using FluentAssertions.Execution;
using OpenSmc.Data;
using OpenSmc.Hub.Fixture;
using OpenSmc.Messaging;
using OpenSmc.TestDomain;
using OpenSmc.TestDomain.SimpleData;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Hierarchies.Test;

public class HierarchicalDimensionCacheTest(ITestOutputHelper output) : HubTestBase(output)
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

    [Fact]
    public async Task InitializationTest()
    {
        var workspace = await GetWorkspace();
        var hierarchies = workspace.State.ToHierarchicalDimensionCache();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>().Should().NotBeNull();
            hierarchies.Get<TestHierarchicalDimensionA>("A111").Should().NotBeNull();
            hierarchies.Get<TestHierarchicalDimensionA>("A_Unknown").Should().BeNull();
            hierarchies.Get<TestHierarchicalDimensionA>(null).Should().BeNull();
        }
    }

    private async Task<IWorkspace> GetWorkspace()
    {
        var workspace = GetHost().GetWorkspace();
        await workspace.Initialized;
        return workspace;
    }

    [Fact]
    public async Task LeafLevelTest()
    {
        var workspace = await GetWorkspace();
        var hierarchies = workspace.State.ToHierarchicalDimensionCache();
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
        var workspace = await GetWorkspace();
        var hierarchies = workspace.State.ToHierarchicalDimensionCache();
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
        var workspace = await GetWorkspace();
        var hierarchies = workspace.State.ToHierarchicalDimensionCache();
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
        var workspace = await GetWorkspace();
        var hierarchies = workspace.State.ToHierarchicalDimensionCache();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>("A1").Level.Should().Be(0);
            hierarchies.Get<TestHierarchicalDimensionA>("A11").Level.Should().Be(1);
            hierarchies.Get<TestHierarchicalDimensionA>("A11").Level.Should().Be(1);
            hierarchies.Get<TestHierarchicalDimensionA>("A111").Level.Should().Be(2);
            hierarchies.Get<TestHierarchicalDimensionA>("A112").Level.Should().Be(2);
        }
    }

    [Fact]
    public async Task SeveralDimensionsLevelTest()
    {
        var workspace = await GetWorkspace();
        var hierarchies = workspace.State.ToHierarchicalDimensionCache();
        using (new AssertionScope())
        {
            hierarchies.Get<TestHierarchicalDimensionA>("A1").Level.Should().Be(0);
            hierarchies.Get<TestHierarchicalDimensionA>("A11").Level.Should().Be(1);
            hierarchies.Get<TestHierarchicalDimensionA>("A111").Level.Should().Be(2);
            hierarchies.Get<TestHierarchicalDimensionB>("B1").Level.Should().Be(0);
            hierarchies.Get<TestHierarchicalDimensionB>("B11").Level.Should().Be(1);
            hierarchies.Get<TestHierarchicalDimensionB>("B111").Level.Should().Be(2);
        }
    }

    [Fact]
    public async Task HierarchyApiTest()
    {
        var workspace = await GetWorkspace();
        var hierarchies = workspace.State.ToHierarchicalDimensionCache();
        var hierarchyA = hierarchies.Get<TestHierarchicalDimensionA>();
        hierarchyA.Get("A11").Parent.Should().Be(hierarchyA.Get("A12").Parent);
    }
}
