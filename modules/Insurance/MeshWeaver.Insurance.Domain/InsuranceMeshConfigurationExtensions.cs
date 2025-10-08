using MeshWeaver.Mesh;

namespace MeshWeaver.Insurance.Domain;

public static class InsuranceMeshConfigurationExtensions
{
    /// <summary>
    /// Registers dynamic insurance pricing mesh nodes (one per PricingAddress).
    /// </summary>
    public static MeshBuilder AddInsurancePricing(this MeshBuilder mesh)
        => mesh
            .AddMeshNodeFactory(address =>
                address.Type == PricingAddress.TypeName
                    ? new MeshNode(address.Type, address.Id, address.ToString())
                    {
                        HubConfiguration = InsuranceApplicationExtensions.ConfigureSinglePricingApplication
                    }
                    : null);
}
