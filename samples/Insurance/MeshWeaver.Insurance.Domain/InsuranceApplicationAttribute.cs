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
            Address,
            nameof(InsuranceApplicationAttribute),
            InsuranceApplicationExtensions.ConfigureInsuranceApplication)
    ];

    /// <summary>
    /// Gets namespaces that describe available address types for autocomplete.
    /// The pricing namespace creates dynamic pricing nodes and routes autocomplete to Insurance application.
    /// Pricing addresses have format: pricing/company/year (e.g., pricing/Microsoft/2026)
    /// </summary>
    public override IEnumerable<MeshNamespace> Namespaces =>
    [
        new MeshNamespace(PricingType, "Pricing")
        {
            Description = "Insurance pricing submissions",
            IconName = "Calculator",
            DisplayOrder = 100,
            MinSegments = 2, // company + year
            AutocompleteAddress = _ => Address,
            Factory = address =>
                address.Type == PricingType
                    ? new MeshNode(address, address.ToString())
                    {
                        HubConfiguration = InsuranceApplicationExtensions.ConfigureSinglePricingApplication
                    }
                    : null
        }
    ];
}
