using System.Reflection;
using OpenSmc.Messaging;
using static OpenSmc.Data.DataPluginExtensions;
using static OpenSmc.Import.ImportExtensions;

namespace OpenSmc.Northwind;

public static class NorthwindHubConfiguration
{
    private static readonly Assembly NorthwindAssembly = typeof(NorthwindHubConfiguration).Assembly;

    public static IMessageHub GetReferenceDataHub(
        this IServiceProvider serviceProvider,
        object address
    )
    {
        return serviceProvider.CreateMessageHub(
            address,
            config =>
                config.AddData(data =>
                    data.FromImportSource(
                        NorthwindAssembly.GetEmbeddedResource("Files.categories.csv"),
                        config => config.WithType<Category>()
                    )
                )
        );
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
