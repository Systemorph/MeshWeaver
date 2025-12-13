using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Mesh catalog that loads nodes from the persistence service.
/// </summary>
public class InMemoryMeshCatalog(
    IMessageHub hub,
    MeshConfiguration configuration,
    IUnifiedPathRegistry pathRegistry,
    IPersistenceService persistenceService)
    : MeshCatalogBase(hub, configuration, pathRegistry, persistenceService)
{
    private readonly ILogger<InMemoryMeshCatalog> logger = hub.ServiceProvider.GetRequiredService<ILogger<InMemoryMeshCatalog>>();

    /// <summary>
    /// Loads a mesh node from the persistence service.
    /// </summary>
    protected override Task<MeshNode?> LoadMeshNode(Address address) =>
        Persistence.GetNodeAsync(address.ToString());

    /// <summary>
    /// Updates a node in the persistence service.
    /// </summary>
    public override Task UpdateAsync(MeshNode node) =>
        Persistence.SaveNodeAsync(node);

    /// <summary>
    /// Updates a node in the persistence service.
    /// </summary>
    protected override Task UpdateNodeAsync(MeshNode node) =>
        Persistence.SaveNodeAsync(node);
}
