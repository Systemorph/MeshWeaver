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
    /// <summary>
    /// Mesh catalog entry.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes
        => [Northwind];
    /// <summary>
    /// Main definition of the mesh node.
    /// </summary>
    public static readonly MeshNode Northwind = new(
        ApplicationAddress.TypeName,
        nameof(Northwind),
        nameof(Northwind),
        typeof(NorthwindApplicationAttribute).FullName
    )
    {
        HubConfiguration = NorthwindApplicationExtensions.ConfigureHub,
        ArticlePath = "Markdown"
    };
}

/// <summary>
/// Extensions for creating the northwind application
/// </summary>
public static class NorthwindApplicationExtensions
{
    /// <summary>
    /// Full configuration of the Northwind application mesh node.
    /// </summary>    /// <returns></returns>
    public static MessageHubConfiguration ConfigureHub(MessageHubConfiguration application)

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
