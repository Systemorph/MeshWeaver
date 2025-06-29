using FluentAssertions;
using MeshWeaver.Layout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Integration test to verify the fix for DistinctUntilChanged() issue
/// </summary>
public class DistinctUntilChangedFixTest
{
    [Fact]
    public void DistinctUntilChanged_ShouldDetectContainerChanges()
    {
        // Arrange - Create a stream of container controls
        var containers = new[]
        {
            Controls.Stack.WithView(Controls.Html("Version 1"), "content"),
            Controls.Stack.WithView(Controls.Html("Version 2"), "content"), // Different content
            Controls.Stack.WithView(Controls.Html("Version 2"), "content"), // Same as previous
            Controls.Stack.WithView(Controls.Html("Version 3"), "content")  // Different again
        };

        var distinctContainers = new List<StackControl>();

        // Act - Apply DistinctUntilChanged (simulating LayoutAreaHost behavior)
        containers.ToObservable()
            .DistinctUntilChanged()
            .Subscribe(container => distinctContainers.Add(container));

        // Assert - Should only get 3 distinct containers (not 4)
        // The duplicate "Version 2" should be filtered out
        distinctContainers.Should().HaveCount(3, "because DistinctUntilChanged should filter out the duplicate");

        // Verify the content is actually different between the distinct items
        var first = distinctContainers[0];
        var second = distinctContainers[1];
        var third = distinctContainers[2];

        first.Should().NotBe(second, "because they have different content");
        second.Should().NotBe(third, "because they have different content");
        first.Should().NotBe(third, "because they have different content");
    }
}
