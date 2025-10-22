using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.Insurance.Domain.InsuranceApplication]

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Mesh node attribute for Insurance application configuration.
/// Defines the root Insurance node. Individual pricing nodes are created dynamically via factory.
/// </summary>
public class InsuranceApplicationAttribute : MeshNodeAttribute
{
    /// <summary>
    /// Root Insurance application address.
    /// </summary>
    public static readonly ApplicationAddress Address = new("Insurance");

    /// <summary>
    /// Gets the mesh nodes for the Insurance application.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes =>
    [
        CreateFromHubConfiguration(
            Address,
            nameof(InsuranceApplicationAttribute),
            InsuranceApplicationExtensions.ConfigureInsuranceApplication)
    ];

    /// <summary>
    /// Gets the node factories for dynamic pricing nodes (one per PricingAddress).
    /// </summary>
    public override IEnumerable<Func<Address, MeshNode?>> NodeFactories =>
    [
        address =>
            address.Type == PricingAddress.TypeName
                ? new MeshNode(address.Type, address.Id, address.ToString())
                {
                    HubConfiguration = InsuranceApplicationExtensions.ConfigureSinglePricingApplication
                }
                : null
    ];
}
