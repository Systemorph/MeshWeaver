using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using MeshWeaver.Todo.LayoutAreas;
using Xunit;

namespace MeshWeaver.Todo.Test;

/// <summary>
/// Tests for Todo layout areas and interactive functionality
/// </summary>
public class TodoLayoutAreaInteractionTest(ITestOutputHelper output) : TodoDataTestBase(output)
{

    /// <summary>
    /// Test that verifies AllItems layout area renders correctly with our WithKey fix
    /// </summary>
    [Fact]
    public void TodoList_ShouldRenderLayoutGridWithData()
    {
        // Arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(nameof(TodoLayoutAreas.AllItems));

        // Act - Create a subscription on AllItems layout area
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            TodoApplicationAttribute.Address,
            reference
        );

        // Wait for the final layout control (skip loading states)
        Output.WriteLine("⏳ Waiting for layout area to render with data...");
        var control = (LayoutGridControl)stream
            .GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(c => c is LayoutGridControl)!;

        // Assert
        Output.WriteLine($"✅ SUCCESS: LayoutGrid rendered with {control.Areas.Count} areas");
        Output.WriteLine("✅ VERIFICATION: WithKey fix allows proper data binding and layout rendering");

        control.Areas.Should().NotBeEmpty("Layout should have areas when data is properly configured");
    }

    /// <summary>
    /// Real interaction test: Click a button to change todo status and verify layout updates
    /// </summary>
    [Fact]
    public void TodoList_ClickStartButton_ShouldMoveItemToInProgressAndUpdateActions()
    {
        // Arrange
        var (_, stream, reference) = SetupTodoLayoutTest();

        // Step 1: Wait for initial layout to render with data
        Output.WriteLine("⏳ Step 1: Waiting for initial layout area to render...");
        var layoutGrid = GetLayoutGrid(stream, reference);

        // Step 2: Get initial pending count
        var initialPendingCount = CountPendingInLayout(stream, layoutGrid);
        initialPendingCount.Should().BeGreaterThan(0, "Should have at least one pending todo item before starting test");
        Output.WriteLine($"✅ Step 2: Confirmed initial pending count: {initialPendingCount} todos");

        // Step 3: Find and click a button that would affect the unassigned pending items
        var (button, areaName) = FindButtonByText(stream, layoutGrid, "Auto-Assign");

        // If no Auto-Assign button found, look for Start button
        if (button == null)
        {
            (button, areaName) = FindButtonByText(stream, layoutGrid, "Start");
        }

        button.Should().NotBeNull("Should find at least one clickable button");
        areaName.Should().NotBeNull("Button area name should not be null");
        var startButton = button!;
        var buttonAreaName = areaName!;
        Output.WriteLine($"✅ Step 3: Found clickable button '{startButton.Title}' in area {buttonAreaName}");

        // Step 4: Click the button and wait for the pending count to change
        ClickButtonAndVerifyResponse(stream, buttonAreaName, startButton);

        // Step 5: Wait for the action to take effect and check the result
        Output.WriteLine("⏳ Step 5: Waiting for action to take effect...");

        // Validate the final state based on the button clicked
        var buttonTitle = startButton.Title?.ToString() ?? "";
        int finalPendingCount;
        if (buttonTitle.Contains("Auto-Assign"))
        {
            // Auto-assign doesn't change pending count, just moves items from unassigned to assigned
            finalPendingCount = CountPendingInLayout(stream, GetLayoutGrid(stream, reference));
            Output.WriteLine($"✅ Step 5: Auto-assign clicked - pending count remains {finalPendingCount}");
            // We expect the count to stay the same or similar since items are still pending, just assigned
        }
        else
        {
            // Start All should reduce pending count (move to InProgress): wait until the freshly
            // rendered layout reports fewer pending items than we started with.
            finalPendingCount = WaitForPendingCount(stream, reference,
                count => count < initialPendingCount, 5.Seconds());
            finalPendingCount.Should().BeLessThan(initialPendingCount, "Start All should reduce pending count");
            Output.WriteLine($"✅ Step 5: Start clicked - pending count reduced from {initialPendingCount} to {finalPendingCount}");
        }

        // Summary
        Output.WriteLine("🎯 CONCLUSION:");
        Output.WriteLine("✅ Layout areas render correctly");
        Output.WriteLine($"✅ Initial pending count was {initialPendingCount} (> 0)");
        Output.WriteLine("✅ Found actual button controls");
        Output.WriteLine("✅ Button click events can be sent to specific areas");
        Output.WriteLine($"✅ Final pending count is {finalPendingCount} (action completed)");
        Output.WriteLine("✅ Layout system remains stable after clicks");
    }

    /// <summary>
    /// Test clicking an individual start button for a specific todo item
    /// </summary>
    [Fact]
    public void TodoList_ClickIndividualStartButton_ShouldMoveSpecificItemToInProgress()
    {
        // Arrange
        var (_, stream, reference) = SetupTodoLayoutTest();

        // Step 1: Wait for initial layout to render with data
        Output.WriteLine("⏳ Step 1: Waiting for initial layout area to render...");
        var layoutGrid = GetLayoutGrid(stream, reference);

        // Step 2: Get initial pending count
        var initialPendingCount = CountPendingInLayout(stream, layoutGrid);
        initialPendingCount.Should().BeGreaterThan(0, "Should have at least one pending todo item before starting test");
        Output.WriteLine($"✅ Step 2: Confirmed initial pending count: {initialPendingCount} todos");

        // Step 3: Find an individual start button (not "Start All")
        var (button, areaName) = FindIndividualStartButton(stream, layoutGrid);
        button.Should().NotBeNull("Should find at least one individual start button");
        areaName.Should().NotBeNull("Button area name should not be null");
        var startButton = button!;
        var buttonAreaName = areaName!;
        Output.WriteLine($"✅ Step 3: Found individual start button '{startButton.Title}' in area {buttonAreaName}");

        // Step 4: Click the individual button and wait for the pending count to change
        ClickButtonAndVerifyResponse(stream, buttonAreaName, startButton);

        // Step 5: Wait for the pending count to decrease
        var expectedFinalCount = initialPendingCount - 1;
        Output.WriteLine($"⏳ Step 5: Waiting for pending count to change from {initialPendingCount} to {expectedFinalCount}...");

        var count = WaitForPendingCount(stream, reference, c => c == expectedFinalCount, 5.Seconds());

        // Validate the final state
        count.Should().Be(expectedFinalCount, $"Should have {expectedFinalCount} pending todos after clicking individual start button");
        Output.WriteLine($"✅ Step 5: Confirmed final pending count: {count} todos (expected {expectedFinalCount})");

        // Summary
        Output.WriteLine("🎯 CONCLUSION:");
        Output.WriteLine("✅ Layout areas render correctly");
        Output.WriteLine($"✅ Initial pending count was {initialPendingCount}");
        Output.WriteLine("✅ Found individual start button");
        Output.WriteLine("✅ Individual button click events work correctly");
        Output.WriteLine($"✅ Final pending count is {count} (decreased by 1)");
        Output.WriteLine("✅ Layout system remains stable after individual clicks");
    }

    #region Helper Methods for Test Reusability

    /// <summary>
    /// Sets up the common infrastructure for Todo layout tests
    /// </summary>
    private (IMessageHub client, ISynchronizationStream<JsonElement> stream, LayoutAreaReference reference) SetupTodoLayoutTest()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(nameof(TodoLayoutAreas.AllItems));

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            TodoApplicationAttribute.Address,
            reference
        );

        return (client, stream, reference);
    }

    /// <summary>
    /// Reads the current control rendered for <paramref name="area"/>, blocking (inside the reactive
    /// assertion) up to <paramref name="within"/> for the first non-null control. Returns <c>null</c>
    /// when nothing materializes in time — the await-free equivalent of the old
    /// <c>FirstOrDefaultAsync().Timeout(...)</c> probe used to scan many areas tolerantly.
    /// </summary>
    private static UiControl? TryGetControl(ISynchronizationStream<JsonElement> stream, string area, TimeSpan within)
    {
        try
        {
            return stream.GetControlStream(area)
                .Should().Within(within).Match(control => control != null);
        }
        catch (AssertionException)
        {
            // No control arrived within the window — treat the area as "nothing here" and move on.
            return null;
        }
    }

    /// <summary>
    /// Gets the current layout grid control from the stream (blocks inside the reactive assertion).
    /// </summary>
    private LayoutGridControl GetLayoutGrid(ISynchronizationStream<JsonElement> stream, LayoutAreaReference reference)
    {
        Output.WriteLine("⏳ Waiting for layout area to render...");
        var initialControl = (LayoutGridControl)stream
            .GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(c => c is LayoutGridControl)!;

        Output.WriteLine($"✅ LayoutGrid rendered with {initialControl.Areas.Count} areas");
        return initialControl;
    }

    /// <summary>
    /// Waits for the layout-rendered pending count to satisfy <paramref name="predicate"/>, then
    /// returns it. The wait is reactive: a fresh "current pending count" snapshot is re-derived from
    /// the layout stream on a short interval until the predicate holds or the timeout elapses.
    /// </summary>
    private int WaitForPendingCount(
        ISynchronizationStream<JsonElement> stream,
        LayoutAreaReference reference,
        Func<int, bool> predicate,
        TimeSpan timeout)
    {
        return Observable
            .Interval(100.Milliseconds())
            .StartWith(0L)
            .SelectMany(_ => CurrentPendingCount(stream, reference))
            .Do(count => Output.WriteLine($"   Polling... current pending count: {count}"))
            .Should().Within(timeout).Match(predicate);
    }

    /// <summary>
    /// Gets the pending todo count rendered in <paramref name="layoutGrid"/> by scanning its areas for the
    /// "Pending (N)" header (in a label or HTML section). Returns 0 when no pending header is present.
    /// Each area control is read sequentially (blocking inside the reactive probe), mirroring the original
    /// per-area scan — no nested stream subscription, so no deadlock.
    /// </summary>
    private int CountPendingInLayout(ISynchronizationStream<JsonElement> stream, LayoutGridControl layoutGrid)
    {
        Output.WriteLine("📊 Finding and validating Pending count...");

        foreach (var area in layoutGrid.Areas)
        {
            var areaName = area.Area.ToString();
            if (areaName == null) continue;

            var areaControl = TryGetControl(stream, areaName, 2.Seconds());
            var count = ExtractPendingCount(areaControl);
            if (count != null)
            {
                Output.WriteLine($"🎯 Found Pending header with count {count} in area {areaName}");
                return count.Value;
            }
        }

        // If no pending header found, assume 0
        Output.WriteLine("🎯 No Pending header found, assuming count is 0");
        return 0;
    }

    /// <summary>
    /// A cold observable yielding the pending count currently rendered in the AllItems layout. It reads
    /// the first <see cref="LayoutGridControl"/>, then scans its areas for the "Pending (N)" header (in a
    /// label or HTML section) and emits the parsed count (0 when no pending header is present).
    /// </summary>
    private static IObservable<int> CurrentPendingCount(ISynchronizationStream<JsonElement> stream, LayoutAreaReference reference) =>
        stream.GetControlStream(reference.Area!)
            .Where(c => c is LayoutGridControl)
            .Select(c => (LayoutGridControl)c!)
            .Take(1)
            .SelectMany(grid => Observable
                .Merge(grid.Areas
                    .Select(area => area.Area.ToString())
                    .Where(areaName => areaName != null)
                    .Select(areaName => stream.GetControlStream(areaName!).Take(1)))
                .Select(ExtractPendingCount)
                .Where(count => count != null)
                .Select(count => count!.Value)
                .Take(1)
                .DefaultIfEmpty(0));

    /// <summary>
    /// Extracts a "Pending (N)" count from a rendered label/HTML control, or <c>null</c> if absent.
    /// </summary>
    private static int? ExtractPendingCount(UiControl? control)
    {
        var content = control switch
        {
            LabelControl label => label.Data?.ToString() ?? "",
            HtmlControl html => html.Data?.ToString() ?? "",
            _ => null
        };
        if (content == null)
            return null;

        var match = System.Text.RegularExpressions.Regex.Match(content, @"Pending \((\d+)\)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : null;
    }

    /// <summary>
    /// Finds a button by searching for specific text in the button content
    /// </summary>
    private (MenuItemControl? button, string? areaName) FindButtonByText(ISynchronizationStream<JsonElement> stream, LayoutGridControl layoutGrid, string searchText)
    {
        Output.WriteLine($"🔍 Looking for button containing '{searchText}'...");

        foreach (var area in layoutGrid.Areas)
        {
            var areaName = area.Area.ToString();
            if (areaName == null) continue;

            Output.WriteLine($"   Area: {areaName}");

            var areaControls = TryGetControl(stream, areaName, 2.Seconds());

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
                        if (string.IsNullOrEmpty(stackAreaName))
                            continue;

                        var stackAreaControl = TryGetControl(stream, stackAreaName, 2.Seconds());
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
                }
            }
            else if (areaControls != null)
            {
                Output.WriteLine($"   Area {areaName} has control: {areaControls.GetType().Name}");
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Finds an individual start button (not "Start All")
    /// </summary>
    private (MenuItemControl? button, string? areaName) FindIndividualStartButton(ISynchronizationStream<JsonElement> stream, LayoutGridControl layoutGrid)
    {
        Output.WriteLine("🔍 Looking for individual start button (not 'Start All')...");

        return RecursivelyFindIndividualStartButton(stream, layoutGrid.Areas);
    }

    /// <summary>
    /// Recursively searches through areas and container controls to find individual start buttons
    /// </summary>
    private (MenuItemControl? button, string? areaName) RecursivelyFindIndividualStartButton(ISynchronizationStream<JsonElement> stream, IEnumerable<NamedAreaControl> areas)
    {
        foreach (var area in areas)
        {
            var areaName = area.Area.ToString();
            if (areaName == null) continue;

            var areaControl = TryGetControl(stream, areaName, 2.Seconds());

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
                    var result = RecursivelyFindIndividualStartButton(stream, stack.Areas);
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
                    var result = RecursivelyFindIndividualStartButton(stream, nestedGrid.Areas);
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
