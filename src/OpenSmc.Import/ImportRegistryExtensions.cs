using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Messaging;

namespace OpenSmc.Import
{
    public static class ImportRegistryExtensions
    {
        public static MessageHubConfiguration AddImport(
            this MessageHubConfiguration configuration,
            Func<DataContext, DataContext> data,
            Func<ImportConfiguration, ImportConfiguration> importConfiguration)
        {
            return configuration
                    .AddData(data)
                    .WithServices(services => services.AddSingleton<IActivityService, ActivityService>())
                    .AddPlugin(h => new ImportPlugin(h, importConfiguration))
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
