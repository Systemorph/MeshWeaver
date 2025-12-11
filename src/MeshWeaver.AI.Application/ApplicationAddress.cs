using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Application;

/// <summary>
/// Application addresses for the AI system
/// </summary>
public static class ApplicationAddress
{
    /// <summary>
    /// Address for the Agents application
    /// </summary>
    public static Address Agents => AddressExtensions.CreateAppAddress("Agents");
}
