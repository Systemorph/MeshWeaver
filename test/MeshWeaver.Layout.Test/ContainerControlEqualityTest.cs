using FluentAssertions;
using MeshWeaver.Layout;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Tests to verify that ContainerControl equality correctly detects changes in sub-objects/views.
/// This addresses the issue where DistinctUntilChanged() in LayoutAreaHost doesn't detect
/// changes in container controls when their child views change.
/// </summary>
public class ContainerControlEqualityTest(ITestOutputHelper output)
{
    private readonly ITestOutputHelper output = output;

    [Fact]
    public void StackControl_WithDifferentViews_ShouldNotBeEqual()
    {
        // Arrange - Create two stack controls with different views
        var stack1 = Controls.Stack
            .WithView(Controls.Html("Hello"), "area1")
            .WithView(Controls.Html("World"), "area2");

        var stack2 = Controls.Stack
            .WithView(Controls.Html("Hello"), "area1")
            .WithView(Controls.Html("Changed"), "area2"); // Different content

        // Act & Assert
        output.WriteLine($"Stack1 Areas: {stack1.Areas.Count}");
        output.WriteLine($"Stack2 Areas: {stack2.Areas.Count}");

        foreach (var area in stack1.Areas)
        {
            output.WriteLine($"Stack1 Area: {area.Id}");
        }

        foreach (var area in stack2.Areas)
        {
            output.WriteLine($"Stack2 Area: {area.Id}");
        }

        // The stacks should NOT be equal because they have different views
        stack1.Equals(stack2).Should().BeFalse("because the views have different content");
        stack1.GetHashCode().Should().NotBe(stack2.GetHashCode(), "because different objects should have different hash codes");
    }

    [Fact]
    public void StackControl_WithSameViews_ShouldBeEqual()
    {
        // Arrange - Create two identical stack controls
        var stack1 = Controls.Stack
            .WithView(Controls.Html("Hello"), "area1")
            .WithView(Controls.Html("World"), "area2");

        var stack2 = Controls.Stack
            .WithView(Controls.Html("Hello"), "area1")
            .WithView(Controls.Html("World"), "area2");

        // Act & Assert
        output.WriteLine($"Stack1 Areas: {stack1.Areas.Count}");
        output.WriteLine($"Stack2 Areas: {stack2.Areas.Count}");

        // The stacks should be equal because they have identical views
        stack1.Equals(stack2).Should().BeTrue("because the views have identical content");
        stack1.GetHashCode().Should().Be(stack2.GetHashCode(), "because equal objects should have the same hash code");
    }

    [Fact]
    public void LayoutGridControl_WithDifferentViews_ShouldNotBeEqual()
    {
        // Arrange - Create two layout grid controls with different views
        var grid1 = Controls.LayoutGrid
            .WithView(Controls.Label("Item 1"), "item1")
            .WithView(Controls.Label("Item 2"), "item2");

        var grid2 = Controls.LayoutGrid
            .WithView(Controls.Label("Item 1"), "item1")
            .WithView(Controls.Label("Different Item"), "item2"); // Different content

        // Act & Assert
        output.WriteLine($"Grid1 Areas: {grid1.Areas.Count}");
        output.WriteLine($"Grid2 Areas: {grid2.Areas.Count}");

        // The grids should NOT be equal because they have different views
        grid1.Equals(grid2).Should().BeFalse("because the views have different content");
        grid1.GetHashCode().Should().NotBe(grid2.GetHashCode(), "because different objects should have different hash codes");
    }

    [Fact]
    public void StackControl_WithDifferentViewTypes_ShouldNotBeEqual()
    {
        // Arrange - Create two stack controls with different view types
        var stack1 = Controls.Stack
            .WithView(Controls.Html("Content"), "area1");

        var stack2 = Controls.Stack
            .WithView(Controls.Label("Content"), "area1"); // Different control type

        // Act & Assert
        output.WriteLine($"Stack1 first view type: {stack1.Areas[0].GetType()}");
        output.WriteLine($"Stack2 first view type: {stack2.Areas[0].GetType()}");

        // The stacks should NOT be equal because they have different view types
        stack1.Equals(stack2).Should().BeFalse("because the views have different types");
        stack1.GetHashCode().Should().NotBe(stack2.GetHashCode(), "because different objects should have different hash codes");
    }

    [Fact]
    public void StackControl_WithNestedContainerChange_ShouldNotBeEqual()
    {
        // Arrange - Create nested container controls with different inner content
        var innerStack1 = Controls.Stack
            .WithView(Controls.Html("Inner Content 1"), "inner1");

        var innerStack2 = Controls.Stack
            .WithView(Controls.Html("Inner Content 2"), "inner1"); // Different inner content

        var outerStack1 = Controls.Stack
            .WithView(innerStack1, "nested");

        var outerStack2 = Controls.Stack
            .WithView(innerStack2, "nested");

        // Act & Assert
        output.WriteLine($"OuterStack1 nested areas: {outerStack1.Areas.Count}");
        output.WriteLine($"OuterStack2 nested areas: {outerStack2.Areas.Count}");

        // The outer stacks should NOT be equal because their nested content is different
        outerStack1.Equals(outerStack2).Should().BeFalse("because the nested views have different content");
        outerStack1.GetHashCode().Should().NotBe(outerStack2.GetHashCode(), "because different objects should have different hash codes");
    }

    [Fact]
    public void StackControl_WithDifferentAreas_ShouldNotBeEqual()
    {
        // Arrange - Create two stack controls with different areas but no views
        // This simulates the case where areas are deserialized or set directly
        var area1 = new NamedAreaControl("area1") { Id = "area1" };
        var area2 = new NamedAreaControl("area2") { Id = "area2" };
        var area3 = new NamedAreaControl("area3") { Id = "area3" };

        var stack1 = Controls.Stack with
        {
            Areas = [area1, area2]
        };

        var stack2 = Controls.Stack with
        {
            Areas = [area1, area3] // Different second area
        };

        // Act & Assert
        output.WriteLine($"Stack1 Areas: {string.Join(", ", stack1.Areas.Select(a => a.Id))}");
        output.WriteLine($"Stack2 Areas: {string.Join(", ", stack2.Areas.Select(a => a.Id))}");

        // The stacks should NOT be equal because they have different areas
        stack1.Equals(stack2).Should().BeFalse("because the areas are different");
        stack1.GetHashCode().Should().NotBe(stack2.GetHashCode(), "because different objects should have different hash codes");
    }

    [Fact]
    public void StackControl_WithSameAreas_ShouldBeEqual()
    {
        // Arrange - Create two stack controls with identical areas but no views
        var area1 = new NamedAreaControl("area1") { Id = "area1" };
        var area2 = new NamedAreaControl("area2") { Id = "area2" };

        var stack1 = Controls.Stack with
        {
            Areas = [area1, area2]
        };

        var stack2 = Controls.Stack with
        {
            Areas = [area1, area2] // Same areas
        };

        // Act & Assert
        output.WriteLine($"Stack1 Areas: {string.Join(", ", stack1.Areas.Select(a => a.Id))}");
        output.WriteLine($"Stack2 Areas: {string.Join(", ", stack2.Areas.Select(a => a.Id))}");

        // The stacks should be equal because they have identical areas
        stack1.Equals(stack2).Should().BeTrue("because the areas are identical");
        stack1.GetHashCode().Should().Be(stack2.GetHashCode(), "because equal objects should have the same hash code");
    }
}
