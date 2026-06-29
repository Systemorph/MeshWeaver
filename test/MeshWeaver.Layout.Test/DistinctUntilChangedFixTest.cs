using MeshWeaver.Layout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Integration test to verify the fix for DistinctUntilChanged() issue
/// </summary>
public class DistinctUntilChangedFixTest
{
    [Fact]
    public async Task DistinctUntilChanged_ShouldDetectContainerChanges()
    {
        // Arrange - Create a stream of container controls
        var containers = new[]
        {
            Controls.Stack.WithView(Controls.Html("Version 1"), "content"),
            Controls.Stack.WithView(Controls.Html("Version 2"), "content"), // Different content
            Controls.Stack.WithView(Controls.Html("Version 2"), "content"), // Same as previous
            Controls.Stack.WithView(Controls.Html("Version 3"), "content")  // Different again
        };

        // Act - Apply DistinctUntilChanged (simulating LayoutAreaHost behavior) and
        // materialize the FULL sequence before asserting. Subscribing and asserting on
        // the side-effect list synchronously assumed ToObservable()'s CurrentThreadScheduler
        // pushes every item before Subscribe returns — but the trampoline only guarantees
        // that when it is idle at Subscribe time; under the xUnit single-threaded
        // sync-context worker it can queue the emissions PAST the assert, yielding 0 items
        // (the flaky CI failure). ToList() emits once on completion of the finite sequence,
        // so awaiting it is deterministic regardless of trampoline state.
        var distinctContainers = await containers.ToObservable()
            .DistinctUntilChanged()
            .ToList()
            .Should().Emit();

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
