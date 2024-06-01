using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;

namespace OpenSmc.Blazor;

public static class BlazorClientExtensions
{
    public static MessageHubConfiguration AddBlazorClient(this MessageHubConfiguration config) =>
        config.AddBlazorClient(x => x);

    public static MessageHubConfiguration AddBlazorClient(this MessageHubConfiguration config,
        Func<BlazorClientConfiguration, BlazorClientConfiguration> configuration)
    {
        return config
            .WithServices(services => services.AddScoped<IBlazorClient, BlazorClient>())
            .Set(config.GetConfigurationFunctions().Add(configuration));
    }

    internal static ImmutableList<Func<BlazorClientConfiguration, BlazorClientConfiguration>> GetConfigurationFunctions(this MessageHubConfiguration config)
        => config.Get<ImmutableList<Func<BlazorClientConfiguration, BlazorClientConfiguration>>>() 
           ?? ImmutableList<Func<BlazorClientConfiguration, BlazorClientConfiguration>>.Empty;
}
