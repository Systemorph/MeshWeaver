using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Connection.Orleans
{
    public class MeshCatalog(IMessageHub hub, ILogger<MeshCatalog> logger, MeshConfiguration meshConfiguration) 
        : MeshCatalogBase(meshConfiguration)
    {
        private readonly IGrainFactory grainFactory = hub.ServiceProvider.GetRequiredService<IGrainFactory>();

        protected override Task<MeshNode> LoadMeshNode(string addressType, string id)
            => grainFactory.GetGrain<IMeshNodeGrain>(GetAddressString(addressType, id)).Get();

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

        protected override async Task InitializeNodeAsync(MeshNode node)
        {
            logger.LogInformation("Starting initialization of Mesh Catalog for {Number} of assemblies:", Configuration.InstallAtStartup.Count);
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
}
