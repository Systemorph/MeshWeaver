using MeshWeaver.Messaging;
using MeshWeaver.Northwind.Model;

namespace MeshWeaver.Northwind.ViewModel;

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
                .AddNorthwindViewModels()
                .AddNorthwindEmployees()
                .AddNorthwindOrders()
                .AddNorthwindSuppliers()
                .AddNorthwindProducts()
                .AddNorthwindCustomers()
                .AddNorthwindReferenceData();
}
