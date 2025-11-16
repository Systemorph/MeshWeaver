using System.Reactive.Linq;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Northwind.Model;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Extensions for creating the northwind application
/// </summary>
public static class NorthwindApplicationExtensions
{
    /// <summary>
    /// Full configuration of the Northwind application mesh node.
    /// </summary>    /// <returns></returns>
    public static MessageHubConfiguration ConfigureNorthwindApplication(MessageHubConfiguration application)

        =>
            application
                .AddEmbeddedResourceContentCollection("Northwind", typeof(NorthwindApplicationAttribute).Assembly, "Markdown")
                .AddNorthwindViewModels()
                .AddNorthwindEmployees()
                .AddNorthwindOrders()
                .AddNorthwindSuppliers()
                .AddNorthwindProducts()
                .AddNorthwindCustomers()
                .AddNorthwindReferenceData()
                .AddNorthwindDataCube()
                .AddArticles();

    /// <summary>
    /// Adds a virtual data source for the Northwind data cube that combines all dimensions.
    /// </summary>
    public static MessageHubConfiguration AddNorthwindDataCube(
        this MessageHubConfiguration configuration
    )
    {
        return configuration.AddData(dataContext =>
            dataContext.WithVirtualDataSource("NorthwindDataCube", virtualDs =>
                virtualDs.WithVirtualType<NorthwindDataCube>(workspace =>
                {
                    // Get streams for all the source data
                    var ordersStream = workspace.GetStream(typeof(Order));
                    var orderDetailsStream = workspace.GetStream(typeof(OrderDetails));
                    var productsStream = workspace.GetStream(typeof(Product));
                    var customersStream = workspace.GetStream(typeof(Customer));
                    var employeesStream = workspace.GetStream(typeof(Employee));
                    var suppliersStream = workspace.GetStream(typeof(Supplier));
                    var categoriesStream = workspace.GetStream(typeof(Category));
                    var regionsStream = workspace.GetStream(typeof(Region));

                    // Combine all streams and create data cube instances with display names
                    return Observable.CombineLatest(
                        ordersStream,
                        orderDetailsStream,
                        productsStream,
                        customersStream,
                        employeesStream,
                        suppliersStream,
                        categoriesStream,
                        regionsStream,
                        (orders, orderDetails, products, customers, employees, suppliers, categories, regions) =>
                        {
                            // Create lookup dictionaries for display names
                            var customerLookup = customers.Value!.GetData<Customer>()
                                .ToDictionary(c => c.CustomerId, c => ((INamed)c).DisplayName);
                            var employeeLookup = employees.Value!.GetData<Employee>()
                                .ToDictionary(e => e.EmployeeId, e => ((INamed)e).DisplayName);
                            var supplierLookup = suppliers.Value!.GetData<Supplier>()
                                .ToDictionary(s => s.SupplierId, s => ((INamed)s).DisplayName);
                            var categoryLookup = categories.Value!.GetData<Category>()
                                .ToDictionary(c => c.CategoryId, c => ((INamed)c).DisplayName);
                            var regionLookup = regions.Value!.GetData<Region>()
                                .ToDictionary(r => r.RegionId, r => ((INamed)r).DisplayName);

                            // Join and create data cube instances
                            return orders.Value!.GetData<Order>()
                                .Join(
                                    orderDetails.Value!.GetData<OrderDetails>(),
                                    o => o.OrderId,
                                    d => d.OrderId,
                                    (order, detail) => (order, detail)
                                )
                                .Join(
                                    products.Value!.GetData<Product>(),
                                    od => od.detail.ProductId,
                                    p => p.ProductId,
                                    (od, product) => (od.order, od.detail, product)
                                )
                                .Select(data =>
                                {
                                    var cube = new NorthwindDataCube(data.order, data.detail, data.product);
                                    // Populate display names
                                    return cube with
                                    {
                                        CustomerName = data.order.CustomerId != null && customerLookup.TryGetValue(data.order.CustomerId, out var custName)
                                            ? custName
                                            : data.order.CustomerId,
                                        EmployeeName = employeeLookup.TryGetValue(data.order.EmployeeId, out var empName)
                                            ? empName
                                            : data.order.EmployeeId.ToString(),
                                        SupplierName = supplierLookup.TryGetValue(data.product.SupplierId, out var suppName)
                                            ? suppName
                                            : data.product.SupplierId.ToString(),
                                        CategoryName = categoryLookup.TryGetValue(data.product.CategoryId, out var catName)
                                            ? catName
                                            : data.product.CategoryId.ToString(),
                                        RegionName = data.order.ShipRegion
                                    };
                                })
                                .AsEnumerable();
                        }
                    ).DistinctUntilChanged();
                })
            )
        );
    }
}
