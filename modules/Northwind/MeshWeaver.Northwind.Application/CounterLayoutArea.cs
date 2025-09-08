using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates an interactive counter demonstration showing local state management in layout areas.
/// Displays a clickable "Increase Counter" button with a live counter value that updates
/// each time the button is pressed, demonstrating real-time UI state changes.
/// </summary>
public static class CounterLayoutArea
{
    /// <summary>
    /// Renders an interactive counter interface with a button and numeric display.
    /// Shows a "Increase Counter" button above a large title displaying the current count (starting at 0).
    /// Each button click increments the counter by 1 and immediately updates the displayed number.
    /// Demonstrates dynamic UI updates and local state management within a single layout area.
    /// </summary>
    /// <param name="host">The layout area host to which the counter view will be added.</param>
    /// <param name="context">The rendering context for the view.</param>
    /// <returns>A vertical stack containing an action button and counter display title.</returns>

    public static UiControl Counter(this LayoutAreaHost host, RenderingContext context)
    {
        // this is actually the only place we keep the state
        var counter = 0;
        return Controls
            .Stack
            .WithView(Controls
                .Button("Increase Counter")
                .WithClickAction(ctx =>
                    ctx.Host.UpdateArea(
                        new($"{context.Area}/{nameof(Counter)}"),
                        Counter(++counter)
                    )
                ), "Button")
            .WithView(Counter(counter), nameof(Counter));
    }

    /// <summary>
    /// Renders the counter value as a title within the view.
    /// </summary>
    /// <param name="counter">The current value of the counter to be displayed.</param>
    /// <returns>A view component representing the counter value as a title.</returns>
    /// <remarks>
    /// This method is utilized to dynamically update the displayed counter value within the UI. It converts the integer counter value to a string and displays it as a title of level 1.
    /// </remarks>
    private static UiControl Counter(int counter) => Controls.Title(counter.ToString(), 1);
}
