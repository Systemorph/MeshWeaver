using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting
{
    public static class HostBuilderExtensions
    {
        public static MeshHostApplicationBuilder UseMeshWeaver
        (
            this IHostApplicationBuilder hostBuilder,
            Address address,
            Func<MeshHostApplicationBuilder, MeshBuilder> configuration = null)
        {
            var builder = new MeshHostApplicationBuilder(hostBuilder, address);
            if (configuration != null)
                builder = (MeshHostApplicationBuilder)configuration.Invoke(builder);
            return builder;
        }

    }
}
