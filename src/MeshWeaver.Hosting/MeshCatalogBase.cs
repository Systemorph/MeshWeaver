using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

public abstract class MeshCatalogBase : IMeshCatalog
{
    public MeshConfiguration Configuration { get; }
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MemoryCacheEntryOptions cacheOptions = new(){SlidingExpiration = TimeSpan.FromMinutes(5)};
    private readonly IMessageHub persistence;
    private readonly ILogger<MeshCatalogBase> logger;
    protected MeshCatalogBase(IMessageHub hub, MeshConfiguration configuration)
    {
        Configuration = configuration;
        logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshCatalogBase>>();
        persistence = hub.GetHostedHub(new PersistenceAddress())!;
        foreach (var assemblyLocation in Configuration.InstallAtStartup)
        {
            var assembly = Assembly.LoadFrom(assemblyLocation);
            foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().SelectMany(a => a.Nodes))
            {
                UpdateNode(node);
            }
        }

    }

    public async Task<MeshNode?> GetNodeAsync(Address address)
    {
        if (cache.TryGetValue(address.ToString(), out var ret))
            return (MeshNode?)ret;
        var node = Configuration.Nodes.GetValueOrDefault(address.ToString())
               ?? Configuration.Nodes.GetValueOrDefault(address.Type)
               ?? Configuration.MeshNodeFactories
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
        persistence.InvokeAsync(_ => UpdateNodeAsync(node), ex =>
        {
            logger.LogError(ex, "unable to update mesh catalog");
            return Task.CompletedTask;
        });
        return node;
    }

    protected abstract Task<MeshNode?> LoadMeshNode(Address address);


    public abstract Task UpdateAsync(MeshNode node);

    private readonly Dictionary<string, StreamInfo> channelTypes = new()
    {
        { ApplicationAddress.TypeName, new(StreamType.Channel, StreamProviders.Hub, ChannelNames.Hub) },
        { KernelAddress.TypeName, new(StreamType.Channel, StreamProviders.Hub, ChannelNames.Hub) }
    };
    public Task<StreamInfo> GetStreamInfoAsync(Address address)
    {
        return Task.FromResult(channelTypes.GetValueOrDefault(address.Type) ?? new StreamInfo(StreamType.Stream, StreamProviders.Memory, address.ToString()));
    }


    protected abstract Task UpdateNodeAsync(MeshNode node);
}
