using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Data;
using MeshWeaver.Import.Configuration;

namespace MeshWeaver.Import.Implementation;

public record ImportUnpartitionedDataSource(Source Source, IWorkspace Workspace)
    : GenericUnpartitionedDataSource<ImportUnpartitionedDataSource>(Source, Workspace)
{
    private ImportRequest ImportRequest { get; init; } = new(Source) { TargetDataSource = null! };

    public ImportUnpartitionedDataSource WithRequest(Func<ImportRequest, ImportRequest> config) =>
        this with
        {
            ImportRequest = config.Invoke(ImportRequest)
        };


    protected override async Task<EntityStore> GetInitialValue(ISynchronizationStream<EntityStore>? stream, CancellationToken cancellationToken)
    {
        var importManager = Workspace.Hub.ServiceProvider.GetRequiredService<ImportManager>();
        var store = await importManager.ImportInstancesAsync(
            ImportRequest,
            new(Data.Serialization.ActivityCategory.Import, Workspace.Hub),
            cancellationToken
        );
        return store;
    }

    private ImmutableList<
        Func<ImportConfiguration, ImportConfiguration>
    > Configurations { get; init; } =
        ImmutableList<Func<ImportConfiguration, ImportConfiguration>>.Empty;

    public ImportUnpartitionedDataSource WithImportConfiguration(
        Func<ImportConfiguration, ImportConfiguration> config
    ) => this with { Configurations = Configurations.Add(config) };
}
