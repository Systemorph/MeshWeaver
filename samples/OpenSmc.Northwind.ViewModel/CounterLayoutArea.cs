using OpenSmc.Layout;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Northwind.ViewModel
{
    /// <summary>
    /// This is an example of a view which uses local state.
    /// </summary>
    public static class CounterLayoutArea
    {
        /// <summary>
        /// The api for adding it as a view.
        /// </summary>
        /// <param name="area"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public static object Counter(this LayoutAreaHost area, RenderingContext context)
        {
            var counter = 0;
            return Controls
                .Stack()
                .WithView(
                    "Button",
                    Controls
                        .Button("Increase Counter")
                        .WithClickAction(ctx =>
                            ctx.Layout.UpdateLayout(
                                $"{context.Area}/{nameof(Counter)}",
                                Counter(++counter)
                            )
                        )
                )
                .WithView(nameof(Counter), Counter(counter));
        }

        /// <summary>
        /// How to render the counter
        /// </summary>
        /// <param name="counter"></param>
        /// <returns></returns>
        private static object Counter(int counter) => Controls.Title(counter.ToString(), 1);

    }
}
