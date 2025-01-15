using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.ViewModel
{
    /// <summary>
    /// Represents a simple toolbar entry that captures a specific year.
    /// </summary>
    /// <param name="Year">  The year that the toolbar entry represents.</param>
    public record Toolbar(int Year);

    /// <summary>
    /// Defines a static class within the MeshWeaver.Northwind.ViewModel namespace for creating and managing a toolbar area. This area is specifically designed to capture and display year information, allowing for dynamic data binding and interaction.
    /// </summary>
    public static class ToolbarArea
    {
        /// <summary>
        /// Creates a toolbar within a specified layout area and binds it with an observable collection of year options.
        /// </summary>
        /// <returns>A dynamically created toolbar control bound with the provided year options.</returns>
        /// <remarks>
        /// This method utilizes reactive extensions to dynamically bind the provided year options to a toolbar control. It ensures that the toolbar updates in response to changes in the observable collection, displaying the maximum year available.
        /// </remarks>
        public static object Toolbar(this LayoutAreaHost layoutArea)
        {
            var years = GetAllYearsOfOrders(layoutArea);

            return Controls.Toolbar
                .WithView(
                    (_, _) =>
                        years.Select(y =>
                            Template.Bind(
                                new Toolbar(y.Max(x => (int)x.GetItem())),
                                tb => Controls.Select(tb.Year,y), nameof(ViewModel.Toolbar))
                        )
                );
        }
        /// <summary>
        /// Gets all the years for which there area orders.
        /// </summary>
        /// <param name="layoutArea"></param>
        /// <returns></returns>
        public static IObservable<Option[]> GetAllYearsOfOrders(this LayoutAreaHost layoutArea)
        {
            var years = layoutArea
                .Workspace
                .GetObservable<Order>()
                .DistinctUntilChanged()
                .Select(x =>
                    x.Select(y => y.OrderDate.Year)
                        .Distinct()
                        .OrderByDescending(year => year)
                        .Select(year => new Option<int>(year, year.ToString()))
                        .Prepend(new Option<int>(0, "All Time"))
                        .ToArray()
                )
                .DistinctUntilChanged(x => string.Join(',', x.Select(y => y.Item)));
            return years;
        }

    }
}
