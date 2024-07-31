using System.Reactive.Linq;
using OpenSmc.Layout;

namespace OpenSmc.Northwind.ViewModel
{
    /// <summary>
    /// Represents a simple toolbar entry that captures a specific year.
    /// </summary>
    /// <param name="Year">  The year that the toolbar entry represents.</param>
    public record Toolbar(int Year);

    /// <summary>
    /// Defines a static class within the OpenSmc.Northwind.ViewModel namespace for creating and managing a toolbar area. This area is specifically designed to capture and display year information, allowing for dynamic data binding and interaction.
    /// </summary>
    public static class ToolbarArea
    {
        /// <summary>
        /// Creates a toolbar within a specified layout area and binds it with an observable collection of year options.
        /// </summary>
        /// <param name="years">An observable collection of year options to bind to the toolbar.</param>
        /// <returns>A dynamically created toolbar control bound with the provided year options.</returns>
        /// <remarks>
        /// This method utilizes reactive extensions to dynamically bind the provided year options to a toolbar control. It ensures that the toolbar updates in response to changes in the observable collection, displaying the maximum year available.
        /// </remarks>
        public static object Toolbar(IObservable<Option[]> years)
        {
            return Controls.Toolbar
                .WithView(
                    (_, _) =>
                        years.Select(y =>
                            Template.Bind(
                                new Toolbar(y.Max(x => (int)x.Item)),
                                nameof(ViewModel.Toolbar),
                                tb => Controls.Select(tb.Year).WithOptions(y)
                            )
                        )
                );
        }
    }
}
