using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application
{

    /// <summary>
    /// Defines a static class within the MeshWeaver.Northwind.ViewModel namespace for creating and managing a toolbar area. This area is specifically designed to capture and display year information, allowing for dynamic data binding and interaction.
    /// </summary>
    public static class ToolbarUtils
    {
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
