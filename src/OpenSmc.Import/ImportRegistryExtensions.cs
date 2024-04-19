using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using AngleSharp.Io;
using DocumentFormat.OpenXml.Bibliography;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Activities;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;

namespace OpenSmc.Import;

public static class ImportExtensions
{
    public static MessageHubConfiguration AddImport(this MessageHubConfiguration configuration) =>
        configuration.AddImport(x => x);

    public static MessageHubConfiguration AddImport(
        this MessageHubConfiguration configuration,
        Func<ImportConfiguration, ImportConfiguration> importConfiguration
    ) =>
        configuration
            .WithServices(services => services.AddSingleton<IActivityService, ActivityService>())
            .AddActivities()
            .AddPlugin<ImportPlugin>(plugin =>
                plugin.WithFactory(() => new(plugin.Hub, importConfiguration))
            );

    public static DataContext FromImportSource(
        this DataContext dataContext,
        Source source,
        Func<ImportDataSource, ImportDataSource> configuration
    ) =>
        dataContext.WithDataSourceBuilder(
            source,
            hub =>
                configuration.Invoke(
                    new(source, new ImportConfiguration(hub, new Workspace(hub, source)).Build())
                )
        );

    public static StreamSource GetEmbeddedResource(this Assembly assembly, string resourceName) =>
        new StreamSource(resourceName, assembly.GetManifestResourceStream(resourceName));
}

public record ImportDataSource(Source Source, ImportConfiguration Configuration)
    : GenericDataSource<ImportDataSource>(Source, Configuration.Hub)
{
    private readonly ImportManager importManager = new(Configuration);

    private ImportRequest ImportRequest { get; init; } = new(Source);

    public ImportDataSource WithRequest(Func<ImportRequest, ImportRequest> config) =>
        this with
        {
            ImportRequest = config.Invoke(ImportRequest)
        };

    public override IEnumerable<ChangeStream<EntityStore>> Initialize()
    {
        var ret = GetInitialChangeStream();
        Hub.Schedule(async cancellationToken =>
        {
            var log = await importManager.ImportAsync(ImportRequest, cancellationToken);

            foreach (var changeStream in ret)
                changeStream.Initialize(importManager.Configuration.Workspace.State.Store);
        });
        return ret;
    }
}
