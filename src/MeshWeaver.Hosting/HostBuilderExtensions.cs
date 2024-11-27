using MeshWeaver.Mesh.Contract;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting
{
    public static class HostBuilderExtensions
    {
        public static void UseMeshWeaver
        (
            this IHostApplicationBuilder hostBuilder,
            object address,
            Func<MeshHostBuilder, MeshBuilder> configuration = null)
        {
            var builder = new MeshHostBuilder(hostBuilder, address);
            if (configuration != null)
                builder = (MeshHostBuilder)configuration.Invoke(builder);

        }

    }
}
