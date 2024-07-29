using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Import.Configuration;

namespace OpenSmc.Import.Implementation;

public record ImportDataSource(Source Source, IWorkspace Workspace)
    : GenericDataSource<ImportDataSource>(Source, Workspace)
{
    private readonly ILogger logger = Workspace.Hub.ServiceProvider.GetRequiredService<
        ILogger<ImportDataSource>
    >();
    private ImportRequest ImportRequest { get; init; } = new(Source);

    public ImportDataSource WithRequest(Func<ImportRequest, ImportRequest> config) =>
        this with
        {
            ImportRequest = config.Invoke(ImportRequest)
        };

    protected override ISynchronizationStream<
        EntityStore,
        CollectionsReference
    > SetupDataSourceStream(WorkspaceState state)
    {
        var ret = base.SetupDataSourceStream(state);
        var config = new ImportConfiguration(
            Workspace,
            MappedTypes,
            Hub.ServiceProvider.GetRequiredService<ILogger<ImportDataSource>>()
        );
        config = Configurations.Aggregate(config, (c, f) => f.Invoke(c));
        ImportManager importManager = new(config);

        Hub.InvokeAsync(async cancellationToken =>
        {
            var (s, _) = await importManager.ImportAsync(
                ImportRequest,
                state,
                logger,
                cancellationToken
            );

            ret.Update(_ => new ChangeItem<EntityStore>(
                Id,
                ret.Reference,
                s.Reduce(ret.Reference),
                Id,
                null,
                Hub.Version
            ));
        });
        return ret;
    }

    private ImmutableList<
        Func<ImportConfiguration, ImportConfiguration>
    > Configurations { get; init; } =
        ImmutableList<Func<ImportConfiguration, ImportConfiguration>>.Empty;

    public ImportDataSource WithImportConfiguration(
        Func<ImportConfiguration, ImportConfiguration> config
    ) => this with { Configurations = Configurations.Add(config) };
}
