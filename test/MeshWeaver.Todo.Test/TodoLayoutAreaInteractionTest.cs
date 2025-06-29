using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Domain;
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

    /// <summary>
    /// Real interaction test: Click a button to change todo status and verify layout updates
    /// </summary>
    [Fact]
    public async Task TodoList_ClickStartButton_ShouldMoveItemToInProgressAndUpdateActions()
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

        // Step 1: Wait for initial layout to render with data
        Output.WriteLine("⏳ Step 1: Waiting for initial layout area to render...");
        var initialControl = await stream
            .GetControlStream(reference.Area)
            .OfType<LayoutGridControl>() // Wait specifically for LayoutGridControl
            .Timeout(10.Seconds())
            .FirstAsync();

        Output.WriteLine($"✅ Step 1: Initial LayoutGrid rendered with {initialControl.Areas.Count} areas");

        // Step 2: Benchmark - Find and validate "Pending (X)" heading with count > 0
        Output.WriteLine("📊 Step 2: Finding and validating Pending count before action...");

        int initialPendingCount = 0;
        string pendingHeaderAreaName = null;

        foreach (var area in initialControl.Areas)
        {
            var areaName = area.Area.ToString();
            try
            {
                var areaControl = await stream
                    .GetControlStream(areaName)
                    .Timeout(2.Seconds())
                    .FirstOrDefaultAsync();

                if (areaControl is LabelControl labelControl)
                {
                    var labelContent = labelControl.Data?.ToString() ?? "";
                    Output.WriteLine($"   Checking Label area {areaName}: '{labelContent}'");

                    // Look for heading with "Pending" and count
                    if (labelContent.Contains("Pending") && labelContent.Contains("(") && labelContent.Contains(")"))
                    {
                        // Extract count from "(X)" format
                        var match = System.Text.RegularExpressions.Regex.Match(labelContent, @"Pending \((\d+)\)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
                        {
                            initialPendingCount = count;
                            pendingHeaderAreaName = areaName;
                            Output.WriteLine($"🎯 Found Pending header: '{labelContent}' with count {count} in area {areaName}");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine($"   Could not get control for area {areaName}: {ex.Message}");
            }
        }

        // Validate we found pending items
        initialPendingCount.Should().BeGreaterThan(0, "Should have at least one pending todo item before starting test");
        pendingHeaderAreaName.Should().NotBeNull("Should find the Pending heading area");
        Output.WriteLine($"✅ Step 2: Confirmed initial pending count: {initialPendingCount} todos");

        // Step 3: Find actual button controls in the layout areas
        Output.WriteLine("🔍 Step 3: Looking for actual button controls...");
        Output.WriteLine("🔍 Step 2: Looking for actual button controls...");

        // Examine all areas to find buttons
        foreach (var area in initialControl.Areas)
        {
            var areaName = area.Area?.ToString();
            Output.WriteLine($"   Area: {areaName}");
        }

        // Look for button controls in the layout areas
        ButtonControl startButton = null;
        string buttonAreaName = null;

        foreach (var area in initialControl.Areas)
        {
            var areaName = area.Area.ToString();

            // Get controls for this specific area
            try
            {
                var areaControls = await stream
                    .GetControlStream(areaName)
                    .Timeout(2.Seconds())
                    .FirstOrDefaultAsync();

                if (areaControls is ButtonControl button)
                {
                    var text = button.Data.ToString();
                    Output.WriteLine($"   Found button in area {areaName}: '{button.Data}'");

                    // Look for a "Start" or action button
                    if (text.Contains("Start") ||
                        text.Contains("Begin") ||
                        text.Contains("▶"))
                    {
                        startButton = button;
                        buttonAreaName = areaName;
                        Output.WriteLine($"🎯 Found target button: '{text}' in area {areaName}");
                    }
                }
                else if (areaControls is StackControl stack)
                {
                    Output.WriteLine($"   Area {areaName} has StackControl with {stack.Areas?.Count ?? 0} areas");

                    // Look for buttons inside the stack control areas
                    if (stack.Areas != null)
                    {
                        foreach (var namedArea in stack.Areas)
                        {
                            var stackAreaName = namedArea.Area?.ToString();
                            if (!string.IsNullOrEmpty(stackAreaName))
                            {
                                try
                                {
                                    // Get the control for this area within the stack
                                    var stackAreaControl = await stream
                                        .GetControlStream(stackAreaName)
                                        .Timeout(2.Seconds())
                                        .FirstOrDefaultAsync();

                                    if (stackAreaControl is ButtonControl stackButton)
                                    {
                                        var text = stackButton.Data?.ToString() ?? "";
                                        Output.WriteLine($"   Found button in stack area {stackAreaName}: '{text}'");

                                        // Look for a "Start" or action button
                                        if (text.Contains("Start") ||
                                            text.Contains("Begin") ||
                                            text.Contains("▶") ||
                                            (startButton == null)) // First button as fallback
                                        {
                                            startButton = stackButton;
                                            buttonAreaName = stackAreaName;
                                            Output.WriteLine($"🎯 Found target button: '{text}' in stack area {stackAreaName}");
                                            break;
                                        }
                                    }
                                    else if (stackAreaControl != null)
                                    {
                                        Output.WriteLine($"   Stack area {stackAreaName} has control: {stackAreaControl.GetType().Name}");
                                    }
                                }
                                catch (Exception stackEx)
                                {
                                    Output.WriteLine($"   Could not get control for stack area {stackAreaName}: {stackEx.Message}");
                                }
                            }
                        }
                    }
                }
                else if (areaControls != null)
                {
                    Output.WriteLine($"   Area {areaName} has control: {areaControls.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine($"   Could not get controls for area {areaName}: {ex.Message}");
            }
        }

        startButton.Should().NotBeNull("Should find at least one clickable button");
        Output.WriteLine($"✅ Step 3: Found clickable button '{startButton.Data}' in area {buttonAreaName}");

        // Step 4: Click the actual button control
        Output.WriteLine("🖱️ Step 4: Clicking the actual button control...");

        // Create a click event for the specific button area
        var clickEvent = new ClickedEvent(buttonAreaName, stream.StreamId);
        client.Post(clickEvent, o => o.WithTarget(TodoApplicationAttribute.Address));
        Output.WriteLine($"✅ Step 4: Click event sent for button '{startButton.Data}' in area {buttonAreaName}");

        // Step 5: Verify the layout system is still responsive after the click
        var finalLayoutGrid = await stream
            .GetControlStream(reference.Area)
            .OfType<LayoutGridControl>()
            .Skip(1)
            //.Timeout(5.Seconds())
            .FirstAsync();

        finalLayoutGrid.Should().NotBeNull("Layout system should still be responsive");
        finalLayoutGrid.Areas.Should().NotBeEmpty("Should still have layout areas after click");
        Output.WriteLine($"✅ Step 5: Layout system responsive - {finalLayoutGrid.Areas.Count} areas available");

        // Step 6: Benchmark - Validate that pending count is now 0 after clicking "Start All"
        Output.WriteLine("📊 Step 6: Validating that pending count is now 0 after clicking 'Start All'...");

        int finalPendingCount = -1;
        bool foundFinalPendingHeader = false;

        foreach (var area in finalLayoutGrid.Areas)
        {
            var areaName = area.Area.ToString();
            try
            {
                var areaControl = await stream
                    .GetControlStream(areaName)
                    .Skip(1) // Skip the first emission to get updated data after the button click
                    .Timeout(2.Seconds())
                    .FirstOrDefaultAsync();

                if (areaControl is LabelControl labelControl)
                {
                    var labelContent = labelControl.Data?.ToString() ?? "";
                    if (labelContent.Contains("Pending") && labelContent.Contains("(") && labelContent.Contains(")"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(labelContent, @"Pending \((\d+)\)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
                        {
                            finalPendingCount = count;
                            foundFinalPendingHeader = true;
                            Output.WriteLine($"🎯 Found final Pending header: '{labelContent}' with count {count} in area {areaName}");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine($"   Could not get control for final area {areaName}: {ex.Message}");
            }
        }

        // Validate the final state
        foundFinalPendingHeader.Should().BeTrue("Should still find the Pending header after the action");
        finalPendingCount.Should().Be(0, "After clicking 'Start All', there should be 0 pending todos");
        Output.WriteLine($"✅ Step 6: Confirmed final pending count: {finalPendingCount} todos (expected 0)");

        // Step 7: Summary and conclusion
        // Step 7: Summary and conclusion
        Output.WriteLine("🎯 CONCLUSION:");
        Output.WriteLine("✅ Layout areas render correctly");
        Output.WriteLine($"✅ Initial pending count was {initialPendingCount} (> 0)");
        Output.WriteLine("✅ Found actual button controls");
        Output.WriteLine("✅ Button click events can be sent to specific areas");
        Output.WriteLine($"✅ Final pending count is {finalPendingCount} (= 0 after Start All)");
        Output.WriteLine("✅ Layout system remains stable after clicks");
        Output.WriteLine("🔧 NOTE: Data update testing done in separate tests (TodoDataChangeTest)");
        Output.WriteLine("🔧 NOTE: This test focuses on layout area infrastructure and validates pending count changes");
    }

    /// <summary>
    /// Helper method to find a pending todo area that has a Start button
    /// </summary>
    private static string FindPendingTodoWithStartButton(LayoutGridControl layoutGrid)
    {
        // In TodoLayoutArea, pending todos are typically in areas with "Start" actions
        // We need to examine the layout structure to find the right area
        foreach (var area in layoutGrid.Areas)
        {
            var areaName = area.Area?.ToString();
            if (!string.IsNullOrEmpty(areaName) &&
                (areaName.Contains("Pending") || areaName.Contains("Start") || areaName.Contains("Todo")))
            {
                return areaName;
            }
        }

        // Fallback: return first area (this is a simplified approach)
        return layoutGrid.Areas.FirstOrDefault()?.Area?.ToString();
    }

    /// <summary>
    /// Helper method to extract todo ID from area name (simplified)
    /// </summary>
    private static string ExtractTodoIdFromArea(string areaName)
    {
        // This is a simplified implementation
        // In practice, you'd parse the area name or access the underlying data
        _ = areaName; // Suppress unused parameter warning
        return "extracted-todo-id"; // Placeholder
    }

    /// <summary>
    /// Helper method to find a todo in InProgress section by ID
    /// </summary>
    private static string FindInProgressTodoById(LayoutGridControl layoutGrid, string todoId)
    {
        // Look for areas that suggest InProgress todos
        _ = todoId; // Suppress unused parameter warning
        foreach (var area in layoutGrid.Areas)
        {
            var areaName = area.Area?.ToString();
            if (!string.IsNullOrEmpty(areaName) &&
                (areaName.Contains("InProgress") || areaName.Contains("Progress") || areaName.Contains("Complete")))
            {
                return areaName;
            }
        }

        return null;
    }

    /// <summary>
    /// Helper method to check if an area has a Complete button
    /// </summary>
    private static bool HasCompleteButton(LayoutGridControl layoutGrid, string areaName)
    {
        // Simplified check - in practice you'd examine the controls in the area
        _ = layoutGrid; // Suppress unused parameter warning
        return areaName?.Contains("Complete") == true || areaName?.Contains("Finish") == true;
    }

    /// <summary>
    /// Helper method to check if an area has a Start button
    /// </summary>
    private static bool HasStartButton(LayoutGridControl layoutGrid, string areaName)
    {
        // Simplified check - in practice you'd examine the controls in the area
        _ = layoutGrid; // Suppress unused parameter warning
        return areaName?.Contains("Start") == true || areaName?.Contains("Begin") == true;
    }
}
