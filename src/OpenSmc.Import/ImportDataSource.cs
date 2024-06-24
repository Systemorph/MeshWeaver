using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;

namespace OpenSmc.Import;

public record ImportDataSource(Source Source, IWorkspace Workspace)
    : GenericDataSource<ImportDataSource>(Source, Workspace)
{
    private ILogger logger = Workspace.Hub.ServiceProvider.GetRequiredService<
        ILogger<ImportDataSource>
    >();
    private ImportRequest ImportRequest { get; init; } = new(Source);

    public ImportDataSource WithRequest(Func<ImportRequest, ImportRequest> config) =>
        this with
        {
            ImportRequest = config.Invoke(ImportRequest)
        };

    public override void Initialize(WorkspaceState state)
    {
        var config = new ImportConfiguration(
            Hub,
            MappedTypes,
            Hub.ServiceProvider.GetRequiredService<ILogger<ImportDataSource>>()
        );
        config = Configurations.Aggregate(config, (c, f) => f.Invoke(c));
        ImportManager importManager = new(config);
        var reference = GetReference();
        var ret = Workspace.GetStream(Id, reference);
        Hub.Schedule(async cancellationToken =>
        {
            var (s, hasError) = await importManager.ImportAsync(
                ImportRequest,
                state,
                logger,
                cancellationToken
            );

            ret.Update(_ => new ChangeItem<EntityStore>(
                Id,
                reference,
                s.Reduce(reference),
                Id,
                null,
                Hub.Version
            ));
        });
        Streams = Streams.Add(ret);
    }

    private ImmutableList<
        Func<ImportConfiguration, ImportConfiguration>
    > Configurations { get; init; } =
        ImmutableList<Func<ImportConfiguration, ImportConfiguration>>.Empty;

    public ImportDataSource WithImportConfiguration(
        Func<ImportConfiguration, ImportConfiguration> config
    ) => this with { Configurations = Configurations.Add(config) };
}
