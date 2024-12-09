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
    private static readonly ApplicationAddress Address = new (Northwind);
    public const string Northwind = nameof(Northwind);
    /// <summary>
    /// Full configuration of the Northwind application mesh node.
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="node"></param>
    /// <returns></returns>
    public override IMessageHub Create(IMessageHub meshHub, MeshNode node)
        => meshHub.ServiceProvider.CreateMessageHub(
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
                );


    public override bool Matches(IMessageHub meshHub, MeshNode meshNode)
    => meshNode.AddressType == ApplicationAddress.TypeName && meshNode.AddressId == Northwind;

    /// <summary>
    /// Mesh catalog entry.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes =>
        [MeshExtensions.GetMeshNode(
            ApplicationAddress.TypeName, Northwind, typeof(NorthwindApplicationAttribute).Assembly.Location
        )];
}
