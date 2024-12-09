using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Connection.Orleans
{
    public class MeshCatalog(IMessageHub hub, ILogger<MeshCatalog> logger, MeshConfiguration meshConfiguration) : IMeshCatalog
    {
        private readonly IDisposable deferral = hub.Defer(_ => true);
        private readonly IGrainFactory grainFactory = hub.ServiceProvider.GetRequiredService<IGrainFactory>();

        private MeshConfiguration Configuration { get; } = meshConfiguration;



        public Task<MeshNode> GetNodeAsync(string addressType, string id)
            => grainFactory.GetGrain<IMeshNodeGrain>(id).Get();

        public Task UpdateAsync(MeshNode node) => grainFactory.GetGrain<IMeshNodeGrain>(node.AddressId).Update(node);
        public async Task InstallModuleAsync(string fullPathToDll)
        {
            var assemblyLoadContext = new AssemblyLoadContext("ModuleRegistry");
            var assembly = assemblyLoadContext.LoadFromAssemblyPath(fullPathToDll);
            foreach (var node in assembly.GetCustomAttributes<MeshNodeAttribute>().SelectMany(a => a.Nodes))
                await grainFactory.GetGrain<IMeshNodeGrain>($"{node.Key}").Update(node);
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
                             .SelectMany(a => a.Nodes))
                {
                    logger.LogInformation("Initializing {Node}", node);
                    /*
                     * 1. Index all sources
                     * 2. Index xml docs
                     * 3. Lucene for articles ==> standard keywords such as ID, Title, Description (use AI to create short summary), user settings as specified in comments of article.
                     *    Idea for indexing: Create record entry by deserializing Markdown Tag and then complete with standard info such as Id, Title, etc.
                     * 4. Vector store for articles ==> use blazor rendering to create html (wait for layout areas to be injected) and index resulting html in vector store.
                     */
                    await grainFactory.GetGrain<IMeshNodeGrain>(node.Key).Update(node);
                }
                loadContext.Unload();
            }

            deferral.Dispose();
        }

        public Task<MeshArticle> GetArticleAsync(string addressType, string nodeId, string id, bool includeContent = false)
            => grainFactory.GetGrain<IArticleGrain>($"{addressType}/{nodeId}/{id}").Get(includeContent);

        public Task UpdateArticleAsync(MeshArticle meshArticle)
            => grainFactory.GetGrain<IArticleGrain>(meshArticle.Url).Update(meshArticle);
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
