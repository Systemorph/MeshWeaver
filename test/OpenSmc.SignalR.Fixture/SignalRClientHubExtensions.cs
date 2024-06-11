using Microsoft.AspNetCore.SignalR.Client;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.SignalR.Fixture;

public static class SignalRClientHubExtensions
{
    public static MessageHubConfiguration AddSignalRClient(this MessageHubConfiguration config, Func<SignalRClientConfiguration, SignalRClientConfiguration> clientConfiguration)
        => config
            .Set(clientConfiguration)
            .WithTypes(typeof(RawJson)) // HACK V10: currently we might push RawJson to client side so we do this workaround to be able to deserialize on client SignalR layer side. Later on we might change to unwrap from RawJson on SignalR server side and this might be removed then. (2024/06/11, Dmitry Kalabin)
            .AddPlugin<SignalRClientPlugin>();
}

public record SignalRClientConfiguration
{
    internal Func<IHubConnectionBuilder, IHubConnectionBuilder> hubConnectionBuilderConfig;
    public SignalRClientConfiguration WithHubConnectionConfiguration(Func<IHubConnectionBuilder, IHubConnectionBuilder> hubConnectionBuilderConfig)
        => this with { hubConnectionBuilderConfig = hubConnectionBuilderConfig, };
}
