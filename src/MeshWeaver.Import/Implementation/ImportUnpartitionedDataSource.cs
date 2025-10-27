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



    protected override async Task<EntityStore> GetInitialValueAsync(ISynchronizationStream<EntityStore> stream, CancellationToken cancellationToken)
    {
        var importManager = Workspace.Hub.ServiceProvider.GetRequiredService<ImportManager>();
        var store = await importManager.ImportInstancesAsync(
            ImportRequest,
            null,
            cancellationToken
        );
        return store;
    }

    private ImmutableList<
        Func<ImportBuilder, ImportBuilder>
    > Configurations { get; init; } =
        ImmutableList<Func<ImportBuilder, ImportBuilder>>.Empty;

    public ImportUnpartitionedDataSource WithImportConfiguration(
        Func<ImportBuilder, ImportBuilder> config
    ) => this with { Configurations = Configurations.Add(config) };
}
