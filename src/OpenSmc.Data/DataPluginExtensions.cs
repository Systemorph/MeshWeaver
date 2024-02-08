﻿using OpenSmc.Messaging;

namespace OpenSmc.Data;

public static class DataPluginExtensions
{
    public static MessageHubConfiguration AddData(this MessageHubConfiguration configuration,
        Func<DataConfiguration, DataConfiguration> configureData)
    {
        return configuration.AddPlugin(hub => new DataPlugin(hub, configureData));
    }
}