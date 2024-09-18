using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.ViewModel;

/// <summary>
/// Provides extension methods for handling Northwind data cube layout areas.
/// </summary>
public static class NorthwindDataCubeLayoutAreaExtensions
{
    private const string NorthwindDataCube = nameof(NorthwindDataCube);

    /// <summary>
    /// Retrieves Northwind data cube data for the specified layout area.
    /// </summary>
    /// <param name="area">The layout area host.</param>
    /// <returns>An observable sequence of Northwind data cubes.</returns>
    public static IObservable<IEnumerable<NorthwindDataCube>> GetNorthwindDataCubeData(
        this LayoutAreaHost area)
        => area
            .GetOrAddVariable(NorthwindDataCube,
                () => area
                    .Workspace.ReduceToTypes(typeof(Order), typeof(OrderDetails), typeof(Product))
                    .DistinctUntilChanged()
                    .Select(x =>
                        x.Value.GetData<Order>()
                            .Join(
                                x.Value.GetData<OrderDetails>(),
                                o => o.OrderId,
                                d => d.OrderId,
                                (order, detail) => (order, detail)
                            )
                            .Join(
                                x.Value.GetData<Product>(),
                                od => od.detail.ProductId,
                                p => p.ProductId,
                                (od, product) => (od.order, od.detail, product)
                            )
                            .Select(data => new NorthwindDataCube(data.order, data.detail, data.product))
                    )
            );

    /// <summary>
    /// Retrieves Northwind data cubes filtered by the specified year.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <returns>An observable sequence of Northwind data cubes filtered by year.</returns>
    public static IObservable<IEnumerable<NorthwindDataCube>> YearlyNorthwindData(this LayoutAreaHost host)
    {
        var data = host.GetNorthwindDataCubeData();

        var yearString = host.GetQueryStringParamValue("Year");

        if (!string.IsNullOrEmpty(yearString) && int.TryParse(yearString, out var year))
        {
            data = data.Select(d => d.Where(x => x.OrderDate.Year == year));
        }

        return data;
    }

    /// <summary>
    /// Retrieves Northwind data cubes filtered by the specified year and the previous year.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <returns>An observable sequence of Northwind data cubes filtered by the specified year and the previous year.</returns>
    public static IObservable<IEnumerable<NorthwindDataCube>> WithPrevYearNorthwindData(this LayoutAreaHost host)
    {
        var data = host.GetNorthwindDataCubeData();

        var yearString = host.GetQueryStringParamValue("Year");

        if (!string.IsNullOrEmpty(yearString) && int.TryParse(yearString, out var year))
        {
            data = data.Select(d => d.Where(x => x.OrderDate.Year == year || x.OrderDate.Year == year - 1));
        }

        return data;
    }
}
