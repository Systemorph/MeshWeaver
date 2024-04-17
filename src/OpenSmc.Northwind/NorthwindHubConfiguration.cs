using OpenSmc.Messaging;
using static OpenSmc.Data.DataPluginExtensions;

namespace OpenSmc.Northwind;

public static class NorthwindHubConfiguration
{
    public static IMessageHub GetReferenceDataHub(this IServiceProvider serviceProvider, object address)
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

