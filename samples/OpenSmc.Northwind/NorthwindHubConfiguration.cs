using System.Reflection;
using OpenSmc.Data;
using OpenSmc.Messaging;
using static OpenSmc.Data.DataPluginExtensions;
using static OpenSmc.Import.ImportExtensions;

namespace OpenSmc.Northwind;

public static class NorthwindHubConfiguration
{
    private static readonly Assembly NorthwindAssembly = typeof(NorthwindHubConfiguration).Assembly;

    public static MessageHubConfiguration AddNorthwindReferenceDataFromFile(
        this MessageHubConfiguration configuration
    )
    {
        return configuration
            .AddImport()
            .AddData(data =>
                data.FromEmbeddedResource<Category>(
                    "Files.categories.csv",
                    config => config.WithType<Category>()
                )
            );
    }

    public static TDataSource AddNorthwindReferenceData<TDataSource>(this TDataSource dataSource)
        where TDataSource : DataSource<TDataSource>
    {
        return dataSource.WithType<Category>();
    }

    public static IMessageHub GetCustomerHub(this IServiceProvider serviceProvider, object address)
    {
        return serviceProvider.CreateMessageHub(address, config => config.AddData(data => data));
    }
}

public class ReferenceDataAddress;

public class CustomerAddress;

public class ProductAddress;

public class EmployeeAddress;

public class OrderAddress;

public class ShipperAddress;

public class SupplierAddress;
