using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Import;
using MeshWeaver.Messaging;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Model;

/// <summary>
/// Provides extension methods for configuring the Northwind application's data layer, specifically for importing reference data and orders.
/// </summary>
/// <remarks>
/// This static class enhances the <c>MessageHubConfiguration</c> with methods to add Northwind-specific data configurations, including importing categories, regions, territories, and orders from embedded CSV files.
/// </remarks>
public static class NorthwindDataConfiguration
{
    /// <summary>
    /// Holds a reference to the assembly where the <c>NorthwindDataConfiguration</c> class is defined.
    /// </summary>
    public static readonly Assembly MyAssembly = typeof(NorthwindDataConfiguration).Assembly;

    /// <summary>
    /// Configures the message hub to include Northwind reference data by importing categories, regions, and territories from embedded CSV files.
    /// </summary>
    /// <param name="configuration">The message hub configuration to be enhanced with Northwind reference data import capabilities.</param>
    /// <returns>
    /// The <c>MessageHubConfiguration</c> instance, allowing for fluent configuration.
    /// </returns>
    /// <remarks>
    /// This method adds support for importing reference data into the Northwind application. It leverages embedded resources for categories, regions, and territories, configuring the data import process for each type.
    /// </remarks>
    public static MessageHubConfiguration AddNorthwindReferenceData(
        this MessageHubConfiguration configuration
    )
    {
        return configuration
            .AddImport()
            .AddData(data =>
                data.FromEmbeddedResource(
                        new EmbeddedResource(MyAssembly, "Files.categories.csv"),
                        config => config.WithType<Category>()
                    )
                    .FromEmbeddedResource(
                        new EmbeddedResource(MyAssembly, "Files.regions.csv"),
                        config => config.WithType<Region>()
                    )
                    .FromEmbeddedResource(
                        new EmbeddedResource(MyAssembly, "Files.territories.csv"),
                        config => config.WithType<Territory>()
                    )
            );
    }

    /// <summary>
    /// Placeholder for a method intended to configure the message hub to include Northwind orders data.
    /// </summary>
    /// <param name="configuration">The message hub configuration to be enhanced with Northwind orders data import capabilities.</param>
    /// <returns>
    /// The <c>MessageHubConfiguration</c> instance, allowing for fluent configuration.
    /// </returns>
    /// <remarks>
    /// This method is expected to add support for importing orders data into the Northwind application. The actual implementation details are not provided in the excerpt.
    /// </remarks>
    public static MessageHubConfiguration AddNorthwindOrders(
        this MessageHubConfiguration configuration
    )
    {
        return configuration
            .AddImport()
            .AddData(data =>
                data.FromEmbeddedResource(
                        new EmbeddedResource(MyAssembly, "Files.orders.csv"),
                        config => config.WithType<Order>()
                    )
                    .FromEmbeddedResource(
                        new EmbeddedResource(MyAssembly, "Files.orders_details.csv"),
                        conf => conf.WithType<OrderDetails>()
                    )
            );
    }

    public static MessageHubConfiguration AddNorthwindSuppliers(
        this MessageHubConfiguration configuration
    )
    {
        return configuration
            .AddImport()
            .AddData(data =>
                data.FromEmbeddedResource(
                    new EmbeddedResource(MyAssembly, "Files.suppliers.csv"),
                    config => config.WithType<Supplier>()
                )
            );
    }

    public static MessageHubConfiguration AddNorthwindEmployees(
        this MessageHubConfiguration configuration
    )
    {
        return configuration
            .AddImport()
            .AddData(data =>
                data.FromEmbeddedResource(
                    new EmbeddedResource(MyAssembly, "Files.employees.csv"),
                    config => config.WithType<Employee>()
                )
            );
    }

    public static MessageHubConfiguration AddNorthwindProducts(
        this MessageHubConfiguration configuration
    )
    {
        return configuration
            .AddImport()
            .AddData(data =>
                data.FromEmbeddedResource(
                    new EmbeddedResource(MyAssembly, "Files.products.csv"),
                    config => config.WithType<Product>()
                )
            );
    }

    public static MessageHubConfiguration AddNorthwindCustomers(
        this MessageHubConfiguration configuration
    )
    {
        return configuration
            .AddImport()
            .AddData(data =>
                data.FromEmbeddedResource(
                    new EmbeddedResource(MyAssembly, "Files.customers.csv"),
                    config => config.WithType<Customer>()
                )
            );
    }

    public static TDataSource AddNorthwindDomain<TDataSource>(this TDataSource dataSource)
        where TDataSource : DataSource<TDataSource> =>
        NorthwindDomain
            .ReferenceDataTypes.Concat(NorthwindDomain.OperationalTypes)
            .Aggregate(dataSource, (ds, t) => ds.WithType(t, x => x));

    public static TDataSource AddNorthwindReferenceData<TDataSource>(this TDataSource dataSource)
        where TDataSource : DataSource<TDataSource>
    {
        return dataSource.WithType<Category>().WithType<Region>().WithType<Territory>();
    }

    public static IMessageHub GetCustomerHub(this IServiceProvider serviceProvider, object address)
    {
        return serviceProvider.CreateMessageHub(address, config => config.AddData(data => data));
    }
}
