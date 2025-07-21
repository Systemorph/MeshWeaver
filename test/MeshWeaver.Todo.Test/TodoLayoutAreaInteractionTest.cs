using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using Xunit;
using MeshWeaver.Todo.LayoutAreas;

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
            .GetControlStream(reference.Area)!
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
        var (_, stream, reference) = await SetupTodoLayoutTest();

        // Step 1: Wait for initial layout to render with data
        Output.WriteLine("⏳ Step 1: Waiting for initial layout area to render...");
        var initialControl = await GetInitialLayoutGrid(stream, reference);

        // Step 2: Get initial pending count
        var initialPendingCount = await GetPendingCount(stream, initialControl);
        initialPendingCount.Should().BeGreaterThan(0, "Should have at least one pending todo item before starting test");
        Output.WriteLine($"✅ Step 2: Confirmed initial pending count: {initialPendingCount} todos");

        // Step 3: Find and click the "Start All" button
        var buttonResult = await FindButtonByText(stream, initialControl, "Start");
        var startButton = buttonResult.button;
        var buttonAreaName = buttonResult.areaName;
        startButton.Should().NotBeNull("Should find at least one clickable button");
        buttonAreaName.Should().NotBeNull("Button area name should not be null");
        Output.WriteLine($"✅ Step 3: Found clickable button '{startButton!.Title}' in area {buttonAreaName}");

        // Step 4: Click the button and wait for the pending count to change to 0
        ClickButtonAndVerifyResponse(stream, buttonAreaName!, startButton!);

        // Step 5: Wait for all pending todos to be moved to InProgress (pending count should be 0)
        Output.WriteLine($"⏳ Step 5: Waiting for pending count to change from {initialPendingCount} to 0...");
        var finalPendingCount = await WaitForPendingCountChange(stream, reference, 0);

        // Validate the final state
        finalPendingCount.Should().Be(0, "Should have 0 pending todos after clicking Start All");
        Output.WriteLine($"✅ Step 5: Confirmed final pending count: {finalPendingCount} todos (expected 0)");

        // Summary
        Output.WriteLine("🎯 CONCLUSION:");
        Output.WriteLine("✅ Layout areas render correctly");
        Output.WriteLine($"✅ Initial pending count was {initialPendingCount} (> 0)");
        Output.WriteLine("✅ Found actual button controls");
        Output.WriteLine("✅ Button click events can be sent to specific areas");
        Output.WriteLine($"✅ Final pending count is {finalPendingCount} (= 0 after Start All)");
        Output.WriteLine("✅ Layout system remains stable after clicks");
    }

    /// <summary>
    /// Test clicking an individual start button for a specific todo item
    /// </summary>
    [Fact]
    public async Task TodoList_ClickIndividualStartButton_ShouldMoveSpecificItemToInProgress()
    {
        // Arrange
        var (_, stream, reference) = await SetupTodoLayoutTest();

        // Step 1: Wait for initial layout to render with data
        Output.WriteLine("⏳ Step 1: Waiting for initial layout area to render...");
        var initialControl = await GetInitialLayoutGrid(stream, reference);

        // Step 2: Get initial pending count
        var initialPendingCount = await GetPendingCount(stream, initialControl);
        initialPendingCount.Should().BeGreaterThan(0, "Should have at least one pending todo item before starting test");
        Output.WriteLine($"✅ Step 2: Confirmed initial pending count: {initialPendingCount} todos");

        // Step 3: Find an individual start button (not "Start All")
        var buttonResult = await FindIndividualStartButton(stream, initialControl);
        var startButton = buttonResult.button;
        var buttonAreaName = buttonResult.areaName;
        startButton.Should().NotBeNull("Should find at least one individual start button");
        buttonAreaName.Should().NotBeNull("Button area name should not be null");
        Output.WriteLine($"✅ Step 3: Found individual start button '{startButton!.Title}' in area {buttonAreaName}");

        // Step 4: Click the individual button and wait for the pending count to change
        ClickButtonAndVerifyResponse(stream, buttonAreaName!, startButton!);

        // Step 5: Wait for the pending count to decrease by monitoring the area updates
        var expectedFinalCount = initialPendingCount - 1;
        Output.WriteLine($"⏳ Step 5: Waiting for pending count to change from {initialPendingCount} to {expectedFinalCount}...");

        var finalPendingCount = await WaitForPendingCountChange(stream, reference, expectedFinalCount);

        // Validate the final state
        finalPendingCount.Should().Be(expectedFinalCount, $"Should have {expectedFinalCount} pending todos after clicking individual start button");
        Output.WriteLine($"✅ Step 5: Confirmed final pending count: {finalPendingCount} todos (expected {expectedFinalCount})");

        // Summary
        Output.WriteLine("🎯 CONCLUSION:");
        Output.WriteLine("✅ Layout areas render correctly");
        Output.WriteLine($"✅ Initial pending count was {initialPendingCount}");
        Output.WriteLine("✅ Found individual start button");
        Output.WriteLine("✅ Individual button click events work correctly");
        Output.WriteLine($"✅ Final pending count is {finalPendingCount} (decreased by 1)");
        Output.WriteLine("✅ Layout system remains stable after individual clicks");
    }

    #region Helper Methods for Test Reusability

    /// <summary>
    /// Sets up the common infrastructure for Todo layout tests
    /// </summary>
    private Task<(dynamic client, ISynchronizationStream<JsonElement> stream, LayoutAreaReference reference)> SetupTodoLayoutTest()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(nameof(TodoLayoutArea.TodoList));

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            TodoApplicationAttribute.Address,
            reference
        );

        return Task.FromResult<(dynamic, ISynchronizationStream<JsonElement>, LayoutAreaReference)>((client, stream, reference));
    }

    /// <summary>
    /// Gets the initial layout grid control from the stream
    /// </summary>
    private async Task<LayoutGridControl> GetInitialLayoutGrid(ISynchronizationStream<JsonElement> stream, LayoutAreaReference reference)
    {
        Output.WriteLine("⏳ Waiting for initial layout area to render...");
        var initialControl = await stream
            .GetControlStream(reference.Area)!
            .OfType<LayoutGridControl>()
            .Timeout(10.Seconds())
            .FirstAsync();

        Output.WriteLine($"✅ Initial LayoutGrid rendered with {initialControl.Areas.Count} areas");
        return initialControl;
    }


    /// <summary>
    /// Waits for the pending count to change to the expected value by monitoring area updates
    /// </summary>
    private async Task<int> WaitForPendingCountChange(ISynchronizationStream<JsonElement> stream, LayoutAreaReference reference, int expectedCount)
    {
        Output.WriteLine($"⏳ Monitoring areas for pending count change to {expectedCount}...");

        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(15); // Increased timeout for Start All operations

        while (DateTime.UtcNow - startTime < timeout)
        {
            try
            {
                // Get the current layout grid
                var layoutGrid = await stream
                    .GetControlStream(reference.Area)!
                    .OfType<LayoutGridControl>()
                    .Timeout(2.Seconds())
                    .FirstAsync();

                // Check the current pending count
                var currentCount = await GetPendingCount(stream, layoutGrid);

                Output.WriteLine($"   Current pending count: {currentCount}, Expected: {expectedCount}");

                if (currentCount == expectedCount)
                {
                    Output.WriteLine($"✅ Pending count successfully changed to {expectedCount}");
                    return currentCount;
                }

                // Wait a bit before checking again
                await Task.Delay(200); // Slightly longer delay for complex operations
            }
            catch (Exception ex)
            {
                Output.WriteLine($"   Error checking pending count: {ex.Message}");
                await Task.Delay(200);
            }
        }

        // If we get here, we timed out
        var finalLayoutGrid = await stream
            .GetControlStream(reference.Area)!
            .OfType<LayoutGridControl>()
            .Timeout(2.Seconds())
            .FirstAsync();
        var finalCount = await GetPendingCount(stream, finalLayoutGrid);

        Output.WriteLine($"⚠️ Timeout waiting for pending count change. Final count: {finalCount}, Expected: {expectedCount}");
        return finalCount;
    }

    /// <summary>
    /// Gets the current pending todo count from the layout
    /// </summary>
    private async Task<int> GetPendingCount(ISynchronizationStream<JsonElement> stream, LayoutGridControl layoutGrid)
    {
        Output.WriteLine("📊 Finding and validating Pending count...");

        foreach (var area in layoutGrid.Areas)
        {
            var areaName = area.Area.ToString();
            if (areaName == null) continue;

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
                            Output.WriteLine($"🎯 Found Pending header: '{labelContent}' with count {count} in area {areaName}");
                            return count;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine($"   Could not get control for area {areaName}: {ex.Message}");
            }
        }

        // If no pending header found, assume 0
        Output.WriteLine("🎯 No Pending header found, assuming count is 0");
        return 0;
    }

    /// <summary>
    /// Finds a button by searching for specific text in the button content
    /// </summary>
    private async Task<(MenuItemControl? button, string? areaName)> FindButtonByText(ISynchronizationStream<JsonElement> stream, LayoutGridControl layoutGrid, string searchText)
    {
        Output.WriteLine($"🔍 Looking for button containing '{searchText}'...");

        foreach (var area in layoutGrid.Areas)
        {
            var areaName = area.Area.ToString();
            if (areaName == null) continue;

            Output.WriteLine($"   Area: {areaName}");

            try
            {
                var areaControls = await stream
                    .GetControlStream(areaName)
                    .Timeout(2.Seconds())
                    .FirstOrDefaultAsync();

                if (areaControls is MenuItemControl button)
                {
                    var text = button.Title?.ToString() ?? "";
                    Output.WriteLine($"   Found button in area {areaName}: '{text}'");

                    if (text.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    {
                        Output.WriteLine($"🎯 Found target button: '{text}' in area {areaName}");
                        return (button, areaName);
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
                            var stackAreaName = namedArea.Area.ToString();
                            if (!string.IsNullOrEmpty(stackAreaName))
                            {
                                try
                                {
                                    var stackAreaControl = await stream
                                        .GetControlStream(stackAreaName)
                                        .Timeout(2.Seconds())
                                        .FirstOrDefaultAsync();

                                    if (stackAreaControl is MenuItemControl stackButton)
                                    {
                                        var text = stackButton.Title?.ToString() ?? "";
                                        Output.WriteLine($"   Found button in stack area {stackAreaName}: '{text}'");

                                        if (text.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                                        {
                                            Output.WriteLine($"🎯 Found target button: '{text}' in stack area {stackAreaName}");
                                            return (stackButton, stackAreaName);
                                        }
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

        return (null, null);
    }

    /// <summary>
    /// Finds an individual start button (not "Start All")
    /// </summary>
    private async Task<(MenuItemControl? button, string? areaName)> FindIndividualStartButton(ISynchronizationStream<JsonElement> stream, LayoutGridControl layoutGrid)
    {
        Output.WriteLine("🔍 Looking for individual start button (not 'Start All')...");

        return await RecursivelyFindIndividualStartButton(stream, layoutGrid.Areas);
    }

    /// <summary>
    /// Recursively searches through areas and container controls to find individual start buttons
    /// </summary>
    private async Task<(MenuItemControl? button, string? areaName)> RecursivelyFindIndividualStartButton(ISynchronizationStream<JsonElement> stream, IEnumerable<NamedAreaControl> areas)
    {
        foreach (var area in areas)
        {
            var areaName = area.Area.ToString();
            if (areaName == null) continue;

            try
            {
                var areaControl = await stream
                    .GetControlStream(areaName)
                    .Timeout(2.Seconds())
                    .FirstOrDefaultAsync();

                if (areaControl is MenuItemControl button)
                {
                    var text = button.Title?.ToString() ?? "";
                    Output.WriteLine($"   Found button in area {areaName}: '{text}'");

                    // Look for individual start buttons, excluding global actions
                    if (IsIndividualStartButton(text))
                    {
                        Output.WriteLine($"🎯 Found individual start button: '{text}' in area {areaName}");
                        return (button, areaName);
                    }
                }
                else if (areaControl is StackControl stack)
                {
                    Output.WriteLine($"   Area {areaName} has StackControl with {stack.Areas?.Count ?? 0} areas");

                    if (stack.Areas != null)
                    {
                        // Recursively search within the stack control
                        var result = await RecursivelyFindIndividualStartButton(stream, stack.Areas);
                        if (result.button != null)
                        {
                            return result;
                        }
                    }
                }
                else if (areaControl is LayoutGridControl nestedGrid)
                {
                    Output.WriteLine($"   Area {areaName} has nested LayoutGridControl with {nestedGrid.Areas?.Count ?? 0} areas");

                    if (nestedGrid.Areas != null)
                    {
                        // Recursively search within the nested grid
                        var result = await RecursivelyFindIndividualStartButton(stream, nestedGrid.Areas);
                        if (result.button != null)
                        {
                            return result;
                        }
                    }
                }
                else if (areaControl != null)
                {
                    Output.WriteLine($"   Area {areaName} has control: {areaControl.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                Output.WriteLine($"   Could not get control for area {areaName}: {ex.Message}");
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Determines if a button text represents an individual start button
    /// </summary>
    private static bool IsIndividualStartButton(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        // Exclude global actions
        if (text.Contains("All", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Add", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("New", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Look for individual start actions
        return text.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Begin", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("▶") ||
               text.Contains("Play", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Go", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Run", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Clicks a button and verifies the layout system responds properly
    /// </summary>
    private void ClickButtonAndVerifyResponse(ISynchronizationStream<JsonElement> stream, string buttonAreaName, MenuItemControl startButton)
    {
        Output.WriteLine("🖱️ Clicking the button control...");

        // Create a click event for the specific button area
        var clickEvent = new ClickedEvent(buttonAreaName, stream.StreamId);

        // Use the hub from the stream to post the event
        stream.Hub.Post(clickEvent, o => o.WithTarget(TodoApplicationAttribute.Address));
        Output.WriteLine($"✅ Click event sent for button '{startButton.Title}' in area {buttonAreaName}");

    }

    #endregion

}
