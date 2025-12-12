using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.Insurance.Domain.InsuranceApplication]

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Mesh node attribute for Insurance application configuration.
/// Defines the root Insurance node and a pricing node for score-based matching.
/// </summary>
public class InsuranceApplicationAttribute : MeshNodeAttribute
{
    /// <summary>
    /// Pricing address type constant.
    /// </summary>
    public const string PricingType = "pricing";

    /// <summary>
    /// Root Insurance application address.
    /// </summary>
    public static readonly Address Address = AddressExtensions.CreateAppAddress("Insurance");

    /// <summary>
    /// Gets the mesh nodes for the Insurance application.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes =>
    [
        CreateFromHubConfiguration(
            Address.ToString(),
            nameof(InsuranceApplicationAttribute),
            InsuranceApplicationExtensions.ConfigureInsuranceApplication),
        // Pricing node - matches any pricing/* path using score-based matching
        new MeshNode(PricingType)
        {
            Name = "Pricing",
            Description = "Insurance pricing submissions",
            IconName = "Calculator",
            DisplayOrder = 100,
            HubConfiguration = InsuranceApplicationExtensions.ConfigureSinglePricingApplication,
            AutocompleteAddress = _ => Address
        }
    ];
}
