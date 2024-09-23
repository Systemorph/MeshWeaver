using FluentAssertions;
using MeshWeaver.Hub.Fixture;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;

public class LayoutSynchronizationTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Fact]
    public void TestEqualityLabel()
    {
        var instance = new LabelControl("1");
        var instance2 = new LabelControl("1");
        var hash1 = instance.GetHashCode();
        var hash2 = instance2.GetHashCode();
        hash1.Should().Be(hash2);

        var equals = instance.Equals(instance2);
        equals.Should().BeTrue();
    }

    [Fact]
    public void TestEqualityNavMenu()
    {
        var instance = CreateNavMenu;
        var instance2 = CreateNavMenu;
        instance.Should().Be(instance2);
    }

    private NavMenuControl CreateNavMenu => Controls.NavMenu.WithNavLink("1", "1").WithNavLink("2", "2");
    [Fact]
    public void TestEqualityStack()
    {
        var instance = CreateStack;
        var instance2 = CreateStack;
        instance.Equals(instance2).Should().BeTrue();
    }

    private LayoutStackControl CreateStack => Controls.Stack.WithView("1", "1")
        .WithView("2", "2");


}
