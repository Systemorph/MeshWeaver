using Microsoft.AspNetCore.SignalR.Client;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.SignalR.Fixture;

public static class SignalRClientHubExtensions
{
    public static MessageHubConfiguration AddSignalRClient(this MessageHubConfiguration config, Func<SignalRClientConfiguration, SignalRClientConfiguration> clientConfiguration)
        => config
            .Set(clientConfiguration)
            .WithTypes(typeof(RawJson))
            .WithSerialization(serialization =>
                serialization.WithOptions(options =>
                {
                    if (!options.Converters.Any(c => c is RawJsonConverter))
                        options.Converters.Insert(0, new RawJsonConverter());
                })
            )
            .AddPlugin<SignalRClientPlugin>();
}

public record SignalRClientConfiguration
{
    internal Func<IHubConnectionBuilder, IHubConnectionBuilder> hubConnectionBuilderConfig;
    public SignalRClientConfiguration WithHubConnectionConfiguration(Func<IHubConnectionBuilder, IHubConnectionBuilder> hubConnectionBuilderConfig)
        => this with { hubConnectionBuilderConfig = hubConnectionBuilderConfig, };
}
