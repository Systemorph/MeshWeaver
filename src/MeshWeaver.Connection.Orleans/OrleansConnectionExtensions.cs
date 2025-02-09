using MeshWeaver.Articles;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Serialization;

namespace MeshWeaver.Connection.Orleans
{
    public static class OrleansConnectionExtensions
    {
        internal static MeshHostBuilder CreateOrleansConnectionBuilder(this IHostBuilder hostBuilder)
        {
            var builder = new MeshHostBuilder(hostBuilder, new OrleansAddress());
            builder.ConfigureServices(services => services.AddSerializer(serializerBuilder =>
                {
                    serializerBuilder.AddJsonSerializer(
                        _ => true,
                        _ => true,
                        ob =>
                            ob.PostConfigure<IMessageHub>(
                                (o, hub) => o.SerializerOptions = hub.JsonSerializerOptions
                            )
                    );
                })
            );
            builder.ConfigureHub(conf => conf
                .WithTypes(typeof(Article), typeof(StreamInfo))
                .AddMeshTypes()
            );
            builder.Host.ConfigureServices(services =>
            {
                services.AddOrleansMeshServices();
            });

            return builder;
        }
        public static void AddOrleansMeshServices(this IServiceCollection services)
        {
            services
                .AddSingleton<IRoutingService, OrleansRoutingService>()
                .AddSingleton<IMeshCatalog, MeshCatalog>();
        }
    }
}
