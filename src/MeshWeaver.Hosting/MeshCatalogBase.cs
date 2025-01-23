using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting;

public abstract class MeshCatalogBase(MeshConfiguration configuration) : IMeshCatalog
{
    public MeshConfiguration Configuration { get; } = configuration;


    public async Task<MeshNode> GetNodeAsync(string addressType, string id)
        => 
            Configuration.Nodes.GetValueOrDefault((addressType, id))
            ??
            Configuration.MeshNodeFactories
                .Select(f => f.Invoke(addressType, id))
                .FirstOrDefault(x => x != null)
            ?? 
            await LoadMeshNode(addressType, id)
    ;

    protected abstract Task<MeshNode> LoadMeshNode(string addressType, string id);

    public abstract Task UpdateAsync(MeshNode node);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var assemblyLocation in Configuration.InstallAtStartup)
        {
            var assembly = Assembly.LoadFrom(assemblyLocation);
            foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().SelectMany(a => a.Nodes))
                await InitializeNodeAsync(node);
        }
    }

    protected abstract Task InitializeNodeAsync(MeshNode node);
}
