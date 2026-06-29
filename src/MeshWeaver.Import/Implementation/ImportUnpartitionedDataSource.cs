using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Data;
using MeshWeaver.Import.Configuration;

namespace MeshWeaver.Import.Implementation;

/// <summary>
/// An unpartitioned data source whose initial value is produced by running an import: it
/// builds an <see cref="ImportRequest"/> for the given <c>Source</c> and imports it
/// into the workspace on first synchronization.
/// </summary>
/// <param name="Source">The source (e.g. embedded resource) to import.</param>
/// <param name="Workspace">The workspace this data source belongs to.</param>
public record ImportUnpartitionedDataSource(Source Source, IWorkspace Workspace)
    : GenericUnpartitionedDataSource<ImportUnpartitionedDataSource>(Source, Workspace)
{
    private ImportRequest ImportRequest { get; init; } = new(Source) { TargetDataSource = null! };

    /// <summary>
    /// Customizes the import request used to populate this data source.
    /// </summary>
    /// <param name="config">Transforms the current import request.</param>
    /// <returns>A new data source with the adjusted request.</returns>
    public ImportUnpartitionedDataSource WithRequest(Func<ImportRequest, ImportRequest> config) =>
        this with
        {
            ImportRequest = config.Invoke(ImportRequest)
        };



    /// <summary>
    /// Produces the data source's initial value by running the configured import once.
    /// </summary>
    /// <param name="stream">The synchronization stream requesting the initial value.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>The imported <c>EntityStore</c>.</returns>
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

    /// <summary>
    /// Adds an import-builder configuration (formats, mappings, validations) applied when importing.
    /// </summary>
    /// <param name="config">Transforms the import builder.</param>
    /// <returns>A new data source with the configuration appended.</returns>
    public ImportUnpartitionedDataSource WithImportConfiguration(
        Func<ImportBuilder, ImportBuilder> config
    ) => this with { Configurations = Configurations.Add(config) };
}
