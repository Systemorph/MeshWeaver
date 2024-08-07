using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Demo.ViewModel;

/// <summary>
/// This is an example of a view which uses local state.
/// </summary>
public static class CounterLayoutArea
{
    /// <summary>
    /// Adds a counter view to the specified layout area.
    /// </summary>
    /// <param name="host">The layout area host to which the counter view will be added.</param>
    /// <param name="context">The rendering context for the view.</param>
    /// <returns>An object representing the configured view within the layout.</returns>

    public static object Counter(this LayoutAreaHost host, RenderingContext context)
    {
        // this is actually the only place we keep the state
        var counter = 0;
        return Controls
            .Stack
            .WithView(
                "Button",
                Controls
                    .Button("Increase Counter")
                    .WithClickAction(ctx =>
                        ctx.Host.UpdateArea(
                            new($"{context.Area}/{nameof(Counter)}"),
                            Counter(++counter)
                        )
                    )
            )
            .WithView(nameof(Counter), Counter(counter));
    }

    /// <summary>
    /// Renders the counter value as a title within the view.
    /// </summary>
    /// <param name="counter">The current value of the counter to be displayed.</param>
    /// <returns>A view component representing the counter value as a title.</returns>
    /// <remarks>
    /// This method is utilized to dynamically update the displayed counter value within the UI. It converts the integer counter value to a string and displays it as a title of level 1.
    /// </remarks>
    private static object Counter(int counter) => Controls.Title(counter.ToString(), 1);
}
