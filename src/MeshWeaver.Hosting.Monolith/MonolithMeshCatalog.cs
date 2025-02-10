using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithMeshCatalog(IMessageHub hub, MeshConfiguration configuration) : MeshCatalogBase(hub, configuration)
{
    private readonly ILogger<MonolithMeshCatalog> logger = hub.ServiceProvider.GetRequiredService<ILogger<MonolithMeshCatalog>>();
    private readonly ConcurrentDictionary<Address, MeshNode> meshNodes = new();
    protected override Task<MeshNode> LoadMeshNode(Address address) => 
        Task.FromResult(meshNodes.GetValueOrDefault(address));

    public override Task UpdateAsync(MeshNode node)
    {
        meshNodes[node.Key] = node;
        // TODO V10: Delegate indexing to IMeshIndexService running on its own hub. (06.09.2024, Roland Bürgi)
        return Task.CompletedTask;
    }

    protected override Task UpdateNodeAsync(MeshNode node)
    {
        meshNodes[node.Key] = node;
        return Task.CompletedTask;
    }

}
