using MeshWeaver.Mesh;

[assembly: MeshWeaver.Insurance.Domain.InsuranceApplication]

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Mesh node attribute for Insurance application configuration.
/// Defines the root Insurance node and the Pricing Catalog node.
/// </summary>
public class InsuranceApplicationAttribute : MeshNodeAttribute
{
    /// <summary>
    /// Root Insurance application address.
    /// </summary>
    public static readonly ApplicationAddress Address = new("Insurance");

    /// <summary>
    /// Pricing catalog application address.
    /// </summary>
    public static readonly ApplicationAddress PricingCatalogAddress = new("InsurancePricingCatalog");

    /// <summary>
    /// Gets the mesh nodes for the Insurance application.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes =>
    [
        CreateFromHubConfiguration(
            Address,
            nameof(InsuranceApplicationAttribute),
            InsuranceApplicationExtensions.ConfigureInsuranceApplication),
        CreateFromHubConfiguration(
            PricingCatalogAddress,
            nameof(PricingCatalogAddress),
            InsuranceApplicationExtensions.ConfigurePricingCatalogApplication)
    ];
}
