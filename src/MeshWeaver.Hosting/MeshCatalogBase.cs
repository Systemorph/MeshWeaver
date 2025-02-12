using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;

namespace MeshWeaver.Hosting;

public abstract class MeshCatalogBase : IMeshCatalog
{
    public MeshConfiguration Configuration { get; }
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MemoryCacheEntryOptions cacheOptions = new(){SlidingExpiration = TimeSpan.FromMinutes(5)};
    private readonly IMessageHub persistence;
    private readonly IDisposable deferral;

    protected MeshCatalogBase(IMessageHub hub, MeshConfiguration configuration)
    {
        Configuration = configuration;
        persistence = hub.GetHostedHub(new PersistenceAddress());
        deferral = persistence.Defer(_ => true);
        foreach (var assemblyLocation in Configuration.InstallAtStartup)
        {
            var assembly = Assembly.LoadFrom(assemblyLocation);
            foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().SelectMany(a => a.Nodes))
            {
                UpdateNode(node);
            }
        }

    }

    public async Task<MeshNode> GetNodeAsync(Address address)
    {
        if (cache.TryGetValue(address.ToString(), out var ret))
            return (MeshNode)ret;
        var node = Configuration.Nodes.GetValueOrDefault(address.ToString())
               ??
               Configuration.MeshNodeFactories
                   .Select(f => f.Invoke(address))
                   .FirstOrDefault(x => x != null)
               ??
               await LoadMeshNode(address);
        if (node is null)
            return null;
        cache.Set(node.Key, node, cacheOptions);
        return UpdateNode(node);
    }

    private MeshNode UpdateNode(MeshNode node)
    {
        cache.Set(node.Key, node, cacheOptions);
        persistence.InvokeAsync(_ => UpdateNodeAsync(node));
        return node;
    }

    protected abstract Task<MeshNode> LoadMeshNode(Address address);


    public abstract Task UpdateAsync(MeshNode node);
    public void StartSync()
    {
        deferral.Dispose();
    }


    protected abstract Task UpdateNodeAsync(MeshNode node);
}
