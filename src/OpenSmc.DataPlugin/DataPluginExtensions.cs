using OpenSmc.Messaging;

namespace OpenSmc.DataPlugin;

public static class DataPluginExtensions
{
    public static MessageHubConfiguration AddData(this MessageHubConfiguration configuration,
        Func<DataPluginConfiguration, DataPluginConfiguration> dataConfiguration)
        => configuration.AddPlugin(hub => new DataPlugin(hub, configuration, dataConfiguration));
}