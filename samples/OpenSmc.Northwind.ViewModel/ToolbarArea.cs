using System.Reactive.Linq;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Northwind.ViewModel
{
    /// <summary>
    /// Defines a static class within the OpenSmc.Northwind.ViewModel namespace for creating and managing a toolbar area. This area is specifically designed to capture and display year information, allowing for dynamic data binding and interaction.
    /// </summary>
    public static class ToolbarArea
    {
        /// <summary>
        /// Represents a simple toolbar entry that captures a specific year.
        /// </summary>
        public record Toolbar
        {
            /// <summary>
            /// The year that the toolbar entry represents.
            /// </summary>
            public int Year { get; init; }
        }

        /// <summary>
        /// Creates a toolbar within a specified layout area and binds it with an observable collection of year options.
        /// </summary>
        /// <param name="area">The layout area host where the toolbar will be created.</param>
        /// <param name="years">An observable collection of year options to bind to the toolbar.</param>
        /// <returns>A dynamically created toolbar control bound with the provided year options.</returns>
        /// <remarks>
        /// This method utilizes reactive extensions to dynamically bind the provided year options to a toolbar control. It ensures that the toolbar updates in response to changes in the observable collection, displaying the maximum year available.
        /// </remarks>
        public static object Toolbar(this LayoutAreaHost area, IObservable<Option<int>[]> years)
        {
            return Controls.Toolbar()
                .WithView(
                    (a, _) =>
                        years.Select(y =>
                            a.Bind(
                                new Toolbar(y.Max(x => x.Item)),
                                nameof(ViewModel.Toolbar),
                                tb => Controls.Select(tb.Year).WithOptions(y)
                            )
                        )
                );
        }
    }
}
