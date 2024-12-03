using MeshWeaver.Mesh;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting
{
    public static class HostBuilderExtensions
    {
        public static void UseMeshWeaver
        (
            this IHostApplicationBuilder hostBuilder,
            object address,
            Func<MeshHostApplicationBuilder, MeshBuilder> configuration = null)
        {
            var builder = new MeshHostApplicationBuilder(hostBuilder, address);
            if (configuration != null)
                builder = (MeshHostApplicationBuilder)configuration.Invoke(builder);

        }

    }
}
