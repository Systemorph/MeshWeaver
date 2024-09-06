using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Orleans.Client
{
    public class MeshCatalog(IMessageHub hub, ILogger<MeshCatalog> logger) : IMeshCatalog
    {
        private readonly IDisposable deferral = hub.Defer(_ => true);
        private readonly IGrainFactory grainFactory = hub.ServiceProvider.GetRequiredService<IGrainFactory>();

        private MeshConfiguration Configuration { get; } = hub.Configuration.GetMeshContext();


        public string GetNodeId(object address)
            => Configuration.AddressToMeshNodeMappers
                .Select(loader => loader(address))
                .FirstOrDefault(x => x != null);

        public Task<MeshNode> GetNodeAsync(object address)=> 
            GetNodeAsync(GetNodeId(address));

        public Task<MeshNode> GetNodeAsync(string id)
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
            logger.LogInformation("Starting initialization of Mesh Catalog for {Number} of assemblies:", Configuration.InstallAtStartup.Count);
            foreach (var assemblyLocation in Configuration.InstallAtStartup)
            {
                var basePath = Path.GetDirectoryName(assemblyLocation);
                var loadContext = new ModulesAssemblyLoadContext(basePath);
                var assembly = loadContext.LoadFromAssemblyPath(assemblyLocation);
                foreach (var node in assembly
                             .GetCustomAttributes()
                             .OfType<MeshNodeAttribute>()
                             .Select(a => a.Node))
                {
                    logger.LogInformation("Initializing {Node}", node);
                    /*
                     * 1. Index all sources
                     * 2. Index xml docs
                     * 3. Lucene for articles ==> standard keywords such as ID, Title, Description (use AI to create short summary), user settings as specified in comments of article.
                     *    Idea for indexing: Create record entry by deserializing Markdown Tag and then complete with standard info such as Id, Title, etc.
                     * 4. Vector store for articles ==> use blazor rendering to create html (wait for layout areas to be injected) and index resulting html in vector store.
                     */
                    await grainFactory.GetGrain<IMeshNodeGrain>(node.Id).Update(node);
                }
                loadContext.Unload();
            }

            deferral.Dispose();
        }

        public Task<ArticleEntry> GetArticleAsync(string id)
            => grainFactory.GetGrain<IArticleGrain>(id).Get();

        public Task UpdateArticleAsync(ArticleEntry article)
            => grainFactory.GetGrain<IArticleGrain>(article.Id).Update(article);
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
