using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Interface for services that initialize the mesh catalog.
/// Implementations load configuration, compile types, and register NodeTypeConfigurations.
/// This runs once when the mesh catalog is created, before any child nodes are loaded.
/// </summary>
public interface IMeshCatalogInitializer
{
    /// <summary>
    /// Initializes the mesh catalog by loading configuration and registering NodeTypeConfigurations.
    /// </summary>
    Task InitializeAsync(IMessageHub hub, CancellationToken ct = default);
}
