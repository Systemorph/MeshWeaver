using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;

public static class BlazorClientExtensions
{
    public static MessageHubConfiguration AddBlazorClient(this MessageHubConfiguration config) =>
        config.AddBlazorClient(x => x);

    public static MessageHubConfiguration AddBlazorClient(this MessageHubConfiguration config,
        Func<BlazorConfiguration, BlazorConfiguration> configuration)
    {
        return config
            .WithServices(services => services.AddScoped<IBlazorServer, BlazorServer>())
            .Set(config.GetConfigurationFunctions().Add(configuration));
    }

    internal static ImmutableList<Func<BlazorConfiguration, BlazorConfiguration>> GetConfigurationFunctions(this MessageHubConfiguration config)
        => config.Get<ImmutableList<Func<BlazorConfiguration, BlazorConfiguration>>>() 
           ?? ImmutableList<Func<BlazorConfiguration, BlazorConfiguration>>.Empty;
}
