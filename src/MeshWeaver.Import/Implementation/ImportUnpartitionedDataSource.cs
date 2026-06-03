using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
        // Initial-value lifecycle edge — bridge the import observable to the
        // framework's Task contract via FirstAsync (no further mesh work after).
        return await importManager.ImportInstances(ImportRequest, null, cancellationToken)
            .FirstAsync()
            .ToTask(cancellationToken);
    }

    private ImmutableList<
        Func<ImportBuilder, ImportBuilder>
    > Configurations { get; init; } =
        ImmutableList<Func<ImportBuilder, ImportBuilder>>.Empty;

    public ImportUnpartitionedDataSource WithImportConfiguration(
        Func<ImportBuilder, ImportBuilder> config
    ) => this with { Configurations = Configurations.Add(config) };
}
