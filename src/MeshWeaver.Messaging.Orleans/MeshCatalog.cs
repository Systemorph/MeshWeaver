using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Application;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Contract;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace MeshWeaver.Orleans
{
    public class MeshCatalog(IServiceProvider serviceProvider) : IMeshCatalog
    {
        private readonly IGrainFactory grainFactory = serviceProvider.GetRequiredService<IGrainFactory>();

        private MeshNodeInfoConfiguration Configuration { get; set; } = StandardConfiguration(serviceProvider);

        public static MeshNodeInfoConfiguration StandardConfiguration(IServiceProvider serviceProvider)
        {
            return new MeshNodeInfoConfiguration()
                .WithModuleMapping(o => o is not ApplicationAddress ? null : SerializationExtensions.GetId(o))
                .WithModuleMapping(SerializationExtensions.GetTypeName);
        }

        // TODO V10: Put this somewhere outside in the config and read in constructor (25.08.2024, Roland Bürgi)
        public void Configure(Func<MeshNodeInfoConfiguration, MeshNodeInfoConfiguration> config)
        {
            Configuration = config(Configuration);
        }
        public string GetMeshNodeId(object address)
            => Configuration.ModuleLoaders
                .Select(loader => loader(address))
                .FirstOrDefault(x => x != null);

        public Task<MeshNode> GetNodeAsync(object address)=> grainFactory.GetGrain<IMeshNodeGrain>(GetMeshNodeId(address)).Get();

        public Task UpdateMeshNodeAsync(MeshNode node) => grainFactory.GetGrain<IMeshCatalogGrain>(node.Id).Update(node);
        public async Task InstallModuleAsync(string fullPathToDll)
        {
            var assemblyLoadContext = new AssemblyLoadContext("ModuleRegistry");
            var assembly = assemblyLoadContext.LoadFromAssemblyPath(fullPathToDll);
            foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().Select(a => a.Node))
                await grainFactory.GetGrain<IMeshNodeGrain>(node.Id).Update(node);
        }
    }
}
