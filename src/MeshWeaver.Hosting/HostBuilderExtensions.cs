using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting
{
    /// <summary>
    /// Extension methods for bootstrapping MeshWeaver onto a host application builder.
    /// </summary>
    public static class HostBuilderExtensions
    {
        /// <summary>
        /// Registers MeshWeaver on the host, creating a mesh root hub at the given address.
        /// </summary>
        /// <param name="hostBuilder">The host application builder to attach MeshWeaver to.</param>
        /// <param name="address">The mesh address for the root hub being created.</param>
        /// <param name="configuration">Optional callback to further configure the mesh builder.</param>
        /// <returns>The configured <see cref="MeshHostApplicationBuilder"/>.</returns>
        public static MeshHostApplicationBuilder UseMeshWeaver
        (
            this IHostApplicationBuilder hostBuilder,
            Address address,
            Func<MeshHostApplicationBuilder, MeshBuilder>? configuration = null)
        {
            var builder = new MeshHostApplicationBuilder(hostBuilder, address);
            if (configuration != null)
                builder = (MeshHostApplicationBuilder)configuration.Invoke(builder);
            return builder;
        }

    }
}
