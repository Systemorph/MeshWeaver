using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Orleans.Client
{
    public class MeshCatalog(IMessageHub hub) : IMeshCatalog
    {
        private readonly IGrainFactory grainFactory = hub.ServiceProvider.GetRequiredService<IGrainFactory>();

        private OrleansMeshContext Context { get; } = hub.Configuration.GetMeshContext();


        public string GetMeshNodeId(object address)
            => Context.AddressToMeshNodeMappers
                .Select(loader => loader(address))
                .FirstOrDefault(x => x != null);

        public Task<MeshNode> GetNodeAsync(object address)=> grainFactory.GetGrain<IMeshNodeGrain>(GetMeshNodeId(address)).Get();

        public Task UpdateMeshNodeAsync(MeshNode node) => grainFactory.GetGrain<IMeshNodeGrain>(node.Id).Update(node);
        public async Task InstallModuleAsync(string fullPathToDll)
        {
            var assemblyLoadContext = new AssemblyLoadContext("ModuleRegistry");
            var assembly = assemblyLoadContext.LoadFromAssemblyPath(fullPathToDll);
            foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().Select(a => a.Node))
                await grainFactory.GetGrain<IMeshNodeGrain>(node.Id).Update(node);
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            foreach (var assemblyLocation in Context.InstallAtStartup)
            {
                var loadContext = new AssemblyLoadContext(assemblyLocation);
                var assembly = loadContext.LoadFromAssemblyPath(assemblyLocation);
                foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().Select(a => a.Node))
                    await grainFactory.GetGrain<IMeshNodeGrain>(node.Id).Update(node);
                loadContext.Unload();
            }
        }
    }
}
