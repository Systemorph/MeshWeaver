using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Activities;
using OpenSmc.Messaging;

namespace OpenSmc.Import
{
    public static class ImportRegistryExtensions
    {
        public static MessageHubConfiguration AddImport(this MessageHubConfiguration configuration,
            Func<ImportConfiguration, ImportConfiguration> importConfiguration)
        {
            var importAddress = new ImportAddress(configuration.Address);
            return configuration.WithServices(services => services.AddSingleton<IActivityService, ActivityService>())
                    .WithRoutes(routes => routes.RouteMessage<ImportRequest>(_ => importAddress))
                    .WithHostedHub(importAddress,
                        c => c.AddPlugin(h => new ImportPlugin(h, importConfiguration)))
                //.AddPlugin(hub => new MappingService(hub, importConfiguration.Invoke(new())))
                ;
        }
    }

    /*
     * Create a .Contract folder
     * create ImportRequest (all options for import), must be serializable
     * types should go to type registry (manually or from dataplugin)
     *
     * format specification goes to add plugin
     *
     * hubs (tests use only hubs)
     *
     */
}
