using System.Reflection;
using OpenSmc.Data;
using OpenSmc.Import;
using OpenSmc.Messaging;
using OpenSmc.Northwind.Domain;

namespace OpenSmc.Northwind.Model;

public static class NorthwindDataConfiguration
{
    public static readonly Assembly MyAssembly = typeof(NorthwindDataConfiguration).Assembly;

    public static MessageHubConfiguration AddNorthwindReferenceData(
        this MessageHubConfiguration configuration
    )
    {
        return configuration
            .AddImport()
            .AddData(data =>
                data.FromEmbeddedResource<Category>(
                        new EmbeddedResource(MyAssembly, "Files.categories.csv"),
                        config => config.WithType<Category>()
                    )
                    .FromEmbeddedResource<Region>(
                        new EmbeddedResource(MyAssembly, "Files.regions.csv"),
                        config => config.WithType<Region>()
                    )
                    .FromEmbeddedResource<Territory>(
                        new EmbeddedResource(MyAssembly, "Files.territories.csv"),
                        config => config.WithType<Territory>()
                    )
            );
    }

    public static MessageHubConfiguration AddNorthwindOrders(
        this MessageHubConfiguration configuration
    )
    {
        return configuration
            .AddImport()
            .AddData(data =>
                data.FromEmbeddedResource<Order>(
                        new EmbeddedResource(MyAssembly, "Files.orders.csv"),
                        config => config.WithType<Order>()
                    )
                    .FromEmbeddedResource<OrderDetails>(
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
                data.FromEmbeddedResource<Supplier>(
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
                data.FromEmbeddedResource<Employee>(
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
                data.FromEmbeddedResource<Product>(
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
                data.FromEmbeddedResource<Customer>(
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
