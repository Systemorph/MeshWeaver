using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Orleans;

public class OrleansMeshCatalog(IMessageHub hub, ILogger<OrleansMeshCatalog> logger, MeshConfiguration meshConfiguration) 
    : MeshCatalogBase(hub, meshConfiguration)
{
    private readonly IGrainFactory grainFactory = hub.ServiceProvider.GetRequiredService<IGrainFactory>();

    protected override Task<MeshNode> LoadMeshNode(Address address)
        => grainFactory.GetGrain<IMeshNodeGrain>(address.ToString()).Get();

    private string GetAddressString(string addressType, string id)
    {
        return $"{addressType}/{id}";
    }

    public override Task UpdateAsync(MeshNode node)
        => grainFactory.GetGrain<IMeshNodeGrain>(GetAddressString(node.AddressType, node.AddressId)).Update(node);

    public async Task InstallModuleAsync(string fullPathToDll)
    {
        var assemblyLoadContext = new AssemblyLoadContext("ModuleRegistry");
        var assembly = assemblyLoadContext.LoadFromAssemblyPath(fullPathToDll);
        foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().SelectMany(a => a.Nodes))
            await grainFactory.GetGrain<IMeshNodeGrain>($"{node.Key}").Update(node);
    }

    protected override async Task UpdateNodeAsync(MeshNode node)
    {
        logger.LogInformation("Updating Mesh Catalog for {Node}", node.Key);
        await grainFactory.GetGrain<IMeshNodeGrain>(node.Key).Update(node);
    }

}

public class ModulesAssemblyLoadContext(string basePath) : AssemblyLoadContext(true){

    protected override Assembly Load(AssemblyName assemblyName)
    {
        // Check if the assembly is already loaded
        var loadedAssembly = AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(a => a.GetName().Name == assemblyName.Name);

        if (loadedAssembly != null)
            return loadedAssembly;


        var assemblyPath = Path.Combine(Directory.GetCurrentDirectory(), $"{assemblyName.Name}.dll");
        if (File.Exists(assemblyPath))
            return LoadFromAssemblyPath(assemblyPath);

        assemblyPath = Path.Combine(basePath, $"{assemblyName.Name}.dll");
        if (File.Exists(assemblyPath))
            return LoadFromAssemblyPath(assemblyPath);


        return null;
    }
}
