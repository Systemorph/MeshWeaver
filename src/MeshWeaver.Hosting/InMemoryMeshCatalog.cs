using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

public class InMemoryMeshCatalog(IMessageHub hub, MeshConfiguration configuration) : MeshCatalogBase(hub, configuration)
{
    private readonly ILogger<InMemoryMeshCatalog> logger = hub.ServiceProvider.GetRequiredService<ILogger<InMemoryMeshCatalog>>();
    private readonly ConcurrentDictionary<Address, MeshNode> meshNodes = new();
    protected override Task<MeshNode> LoadMeshNode(Address address) => 
        Task.FromResult(meshNodes.GetValueOrDefault(address));

    public override Task UpdateAsync(MeshNode node)
    {
        meshNodes[node.Key] = node;
        return Task.CompletedTask;
    }

    protected override Task UpdateNodeAsync(MeshNode node)
    {
        meshNodes[node.Key] = node;
        return Task.CompletedTask;
    }

}
