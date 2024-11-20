using MeshWeaver.Application;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using MeshWeaver.Northwind.Model;
using MeshWeaver.Northwind.ViewModel;
[assembly: NorthwindApplication]

namespace MeshWeaver.Northwind.ViewModel;


/// <summary>
/// This is the configuration of the Northwind application mesh node.
/// </summary>
public class NorthwindApplicationAttribute : MeshNodeAttribute
{
    /// <summary>
    /// Full configuration of the Northwind application mesh node.
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="address"></param>
    /// <returns></returns>
    public override IMessageHub Create(IServiceProvider serviceProvider, object address)
        => serviceProvider.CreateMessageHub(
            address,
            application =>
                application
                    .AddNorthwindViewModels()
                    .AddNorthwindEmployees()
                    .AddNorthwindOrders()
                    .AddNorthwindSuppliers()
                    .AddNorthwindProducts()
                    .AddNorthwindCustomers()
                    .AddNorthwindReferenceData()
        );


    /// <summary>
    /// Mesh catalog entry.
    /// </summary>
    public override MeshNode Node =>
        GetMeshNode(
            new ApplicationAddress("Northwind"),
            typeof(NorthwindApplicationAttribute).Assembly.Location
        );
}
