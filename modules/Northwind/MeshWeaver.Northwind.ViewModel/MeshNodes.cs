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
        =>
        [
#if DEBUG
            Northwind
#else
            Northwind
#endif
        ];
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
        StartupScript = @$"#r ""nuget:{typeof(NorthwindApplicationAttribute).FullName}""
{typeof(NamedOptionExtensions).FullName}.{nameof(NorthwindApplicationExtensions.CreateNorthwind)}
(Mesh, new {typeof(ApplicationAddress).FullName}(""{nameof(Northwind)}"");"
    };

    //public static readonly MeshNode NorthwindDevelopment = new(
    //    ApplicationAddress.TypeName,
    //    nameof(Northwind),
    //    nameof(Northwind),
    //    typeof(NorthwindApplicationAttribute).FullName
    //);
}

/// <summary>
/// Extensions for creating the northwind application
/// </summary>
public static class NorthwindApplicationExtensions
{
    /// <summary>
    /// Full configuration of the Northwind application mesh node.
    /// </summary>
    /// <param name="meshHub"></param>
    /// <param name="address"></param>
    /// <returns></returns>
    public static IMessageHub CreateNorthwind(this IMessageHub meshHub, object address)
        => meshHub.ServiceProvider.CreateMessageHub(
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

}
