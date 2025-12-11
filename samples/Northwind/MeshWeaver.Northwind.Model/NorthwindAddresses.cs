using MeshWeaver.Messaging;

namespace MeshWeaver.Northwind.Model;

/// <summary>
/// Defines address types and factory methods for Northwind domain addresses.
/// </summary>
public static class NorthwindAddresses
{
    public const string ReferenceDataType = "reference-data";
    public const string CustomerType = "customers";
    public const string ProductType = "products";
    public const string EmployeeType = "employees";
    public const string OrderType = "orders";
    public const string ShipperType = "shippers";
    public const string SupplierType = "suppliers";

    public static Address ReferenceData() => new Address(ReferenceDataType, "singleton");
    public static Address Customer() => new Address(CustomerType, "singleton");
    public static Address Product() => new Address(ProductType, "singleton");
    public static Address Employee() => new Address(EmployeeType, "singleton");
    public static Address Order() => new Address(OrderType, "singleton");
    public static Address Shipper() => new Address(ShipperType, "singleton");
    public static Address Supplier() => new Address(SupplierType, "singleton");
}
