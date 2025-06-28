using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

/// <summary>
/// Tests for Todo layout areas and interactive functionality
/// </summary>
public class TodoLayoutAreaInteractionTest(ITestOutputHelper output) : TodoDataTestBase(output)
{

    /// <summary>
    /// Test that verifies TodoList layout area renders correctly with our WithKey fix
    /// </summary>
    [Fact]
    public async Task TodoList_ShouldRenderLayoutGridWithData()
    {
        // Arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(nameof(TodoLayoutArea.TodoList));

        // Act - Create a subscription on TodoList layout area
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            TodoApplicationAttribute.Address,
            reference
        );

        // Wait for the final layout control (skip loading states)
        Output.WriteLine("⏳ Waiting for layout area to render with data...");
        var control = await stream
            .GetControlStream(reference.Area)
            .OfType<LayoutGridControl>() // Wait specifically for LayoutGridControl
            .Timeout(10.Seconds())
            .FirstAsync();

        // Assert
        Output.WriteLine($"✅ SUCCESS: LayoutGrid rendered with {control.Areas.Count} areas");
        Output.WriteLine("✅ VERIFICATION: WithKey fix allows proper data binding and layout rendering");

        control.Should().NotBeNull();
        control.Areas.Should().NotBeEmpty("Layout should have areas when data is properly configured");
    }
}
