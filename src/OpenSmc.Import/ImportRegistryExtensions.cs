using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.DependencyInjection;

using OpenSmc.Activities;
using OpenSmc.Messaging;

namespace OpenSmc.Import
{
    public static class ImportRegistryExtensions
    {
        public static MessageHubConfiguration AddImport(this MessageHubConfiguration configuration)
        => configuration.AddImport(x => x);
        public static MessageHubConfiguration AddImport(
            this MessageHubConfiguration configuration,
            Func<ImportConfiguration, ImportConfiguration> importConfiguration)
        {
            return configuration
                    .WithServices(services => services.AddSingleton<IActivityService, ActivityService>())
                    .AddActivities()
                    .AddPlugin<ImportPlugin>(plugin => plugin.WithFactory(() => new(plugin.Hub, importConfiguration)))
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
