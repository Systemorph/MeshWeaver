using System.Reactive.Linq;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.ViewModel;

public static class NorthwindDataCubeLayoutAreaExtensions
{
    private const string NorthwindDataCube = nameof(NorthwindDataCube);

    public static IObservable<IEnumerable<NorthwindDataCube>> GetNorthwindDataCubeData(this LayoutAreaHost area)
        => area.GetOrAddVariable(NorthwindDataCube,
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



}
