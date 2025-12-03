using MeshWeaver.Messaging;

namespace MeshWeaver.Northwind.Model
{
    /// <summary>
    /// Defines various address classes used throughout the MeshWeaver Northwind application model.
    /// </summary>
    /// <remarks>
    /// This file contains declarations for address-related classes that represent different types of addresses within the Northwind application, such as addresses for reference data, customers, products, employees, orders, shippers, and suppliers.
    /// </remarks>
    public record ReferenceDataAddress() : Address("reference-data", "singleton");

    /// <summary>
    /// Represents a customer's address within the Northwind application.
    /// </summary>
    public record CustomerAddress() : Address("customers", "singleton");

    /// <summary>
    /// Represents an address related to a product within the Northwind application.
    /// </summary>
    public record ProductAddress() : Address("products", "singleton");

    /// <summary>
    /// Represents an employee's address within the Northwind application.
    /// </summary>
    public record EmployeeAddress() : Address("employees", "singleton");

    /// <summary>
    /// Represents an address related to an order within the Northwind application.
    /// </summary>
    public record OrderAddress() : Address("orders", "singleton");

    /// <summary>
    /// Represents a shipper's address within the Northwind application.
    /// </summary>
    public record ShipperAddress() : Address("shippers", "singleton");

    /// <summary>
    /// Represents a supplier's address within the Northwind application.
    /// </summary>
    public record SupplierAddress() : Address("suppliers", "singleton");
}
