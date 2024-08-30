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

        private MeshConfiguration Configuration { get; } = hub.Configuration.GetMeshContext();


        public string GetNodeId(object address)
            => Configuration.AddressToMeshNodeMappers
                .Select(loader => loader(address))
                .FirstOrDefault(x => x != null);

        public Task<MeshNode> GetNodeAsync(object address)=> 
        GetNodeById(GetNodeId(address));

        public Task<MeshNode> GetNodeById(string id)
            => grainFactory.GetGrain<IMeshNodeGrain>(id).Get();

        public Task UpdateAsync(MeshNode node) => grainFactory.GetGrain<IMeshNodeGrain>(node.Id).Update(node);
        public async Task InstallModuleAsync(string fullPathToDll)
        {
            var assemblyLoadContext = new AssemblyLoadContext("ModuleRegistry");
            var assembly = assemblyLoadContext.LoadFromAssemblyPath(fullPathToDll);
            foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().Select(a => a.Node))
                await grainFactory.GetGrain<IMeshNodeGrain>(node.Id).Update(node);
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            foreach (var assemblyLocation in Configuration.InstallAtStartup)
            {
                var loadContext = new CollectibleAssemblyLoadContext();
                var assembly = loadContext.LoadFromAssemblyPath(assemblyLocation);
                foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().Select(a => a.Node))
                    await grainFactory.GetGrain<IMeshNodeGrain>(node.Id).Update(node);
                loadContext.Unload();
            }
        }

        public Task<ArticleEntry> GetArticle(string id)
            => grainFactory.GetGrain<IArticleGrain>(id).Get();

        public Task UpdateArticle(ArticleEntry article)
            => grainFactory.GetGrain<IArticleGrain>(article.Id).Update(article);
    }
}

public class CollectibleAssemblyLoadContext() : AssemblyLoadContext(true){}
