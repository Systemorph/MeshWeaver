using MeshWeaver.Application;
using MeshWeaver.Mesh;
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
    private static readonly ApplicationAddress Address = new ("Northwind");

    /// <summary>
    /// Full configuration of the Northwind application mesh node.
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="node"></param>
    /// <returns></returns>
    public override IMessageHub Create(IServiceProvider serviceProvider, MeshNode node)
        => CreateIf(node.Matches(Address),
            () =>
                serviceProvider.CreateMessageHub(
                    Address,
                    application =>
                        application
                            .AddNorthwindViewModels()
                            .AddNorthwindEmployees()
                            .AddNorthwindOrders()
                            .AddNorthwindSuppliers()
                            .AddNorthwindProducts()
                            .AddNorthwindCustomers()
                            .AddNorthwindReferenceData()
                ));


    /// <summary>
    /// Mesh catalog entry.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes =>
        [MeshExtensions.GetMeshNode(
            Address, typeof(NorthwindApplicationAttribute).Assembly.Location
        )];
}
