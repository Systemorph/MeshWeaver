using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Notebook.Client
{
    public class MeshWeaverKernelExtension : IKernelExtension
    {
        public async Task OnLoadAsync(Kernel kernel)
        {
            var serviceProvider = CreateServiceProvider();
            kernel.RegisterMeshWeaverCommands(serviceProvider);

            await kernel.SendAsync(new DisplayValue(new("MeshWeaver commands registered.", "text/markdown")));
        }

        private IServiceProvider CreateServiceProvider()
        {
            var serviceCollection = new ServiceCollection();

            // Add necessary services to the service collection
            // serviceCollection.AddSingleton<SomeService>();

            return serviceCollection.BuildServiceProvider();
        }
    }
}
