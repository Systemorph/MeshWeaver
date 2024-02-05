using OpenSmc.Messaging;

namespace OpenSmc.DataPlugin;

public record DataConfiguration
{
    internal Func<IMessageHub, IMessageHubPlugin> CreateSatellitePlugin { get; init; }
    internal WorkspaceConfiguration Workspace { get; init; }

    public DataConfiguration WithWorkspace(Func<WorkspaceConfiguration, WorkspaceConfiguration> configureWorkspace)
        => this with { Workspace = configureWorkspace(new WorkspaceConfiguration()) };

    public DataConfiguration WithPersistence(Func<DataPersistenceConfiguration, DataPersistenceConfiguration> configurePersistence)
        => this with { CreateSatellitePlugin = persistenceHub => new DataPersistencePlugin(persistenceHub, configurePersistence) };
}