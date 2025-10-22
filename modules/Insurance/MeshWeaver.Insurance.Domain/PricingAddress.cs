using MeshWeaver.Messaging;

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Address type for insurance pricing operations.
/// Routes messages to specific pricing contexts.
/// </summary>
public record PricingAddress(string Id) : Address(TypeName, Id)
{
    /// <summary>
    /// Type name for pricing addresses.
    /// </summary>
    public const string TypeName = "pricing";
}
