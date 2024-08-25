using MeshWeaver.Application;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Contract;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace MeshWeaver.Orleans
{
    public static class OrleansExtensions
    {
        public const string Storage = "storage";
        public const string StreamProvider = "SMS";


        public static MessageHubConfiguration ConfigureOrleans(this MessageHubConfiguration configuration)
            => configuration.WithTypes(typeof(ApplicationAddress))
                .WithServices(services => services.AddScoped<IMeshCatalog, MeshCatalog>());

    }
}
