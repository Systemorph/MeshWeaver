using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Import.Configuration;

namespace MeshWeaver.Import.Implementation;

public record ImportDataSource(Source Source, IWorkspace Workspace)
    : GenericDataSource<ImportDataSource>(Source, Workspace)
{
    private ImportRequest ImportRequest { get; init; } = new(Source);

    public ImportDataSource WithRequest(Func<ImportRequest, ImportRequest> config) =>
        this with
        {
            ImportRequest = config.Invoke(ImportRequest)
        };


    protected override Task<EntityStore> GetInitialValue(ISynchronizationStream<EntityStore> stream, CancellationToken cancellationToken)
    {
        var config = new ImportConfiguration(
            Workspace,
            MappedTypes,
            Hub.ServiceProvider.GetRequiredService<ILogger<ImportDataSource>>()
        );
        config = Configurations.Aggregate(config, (c, f) => f.Invoke(c));
        ImportManager importManager = new(config);

        return importManager.Initialize(
            ImportRequest,
            GetReference(),
            cancellationToken
        );
    }

    private ImmutableList<
        Func<ImportConfiguration, ImportConfiguration>
    > Configurations { get; init; } =
        ImmutableList<Func<ImportConfiguration, ImportConfiguration>>.Empty;

    public ImportDataSource WithImportConfiguration(
        Func<ImportConfiguration, ImportConfiguration> config
    ) => this with { Configurations = Configurations.Add(config) };
}
