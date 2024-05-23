using Microsoft.AspNetCore.SignalR.Client;
using OpenSmc.Messaging;

namespace OpenSmc.SignalR.Fixture;

public static class SignalRClientHubExtensions
{
    public static MessageHubConfiguration AddSignalRClient(this MessageHubConfiguration config, Func<SignalRClientConfiguration, SignalRClientConfiguration> clientConfiguration)
        => config
            .Set(clientConfiguration)
            .AddPlugin<SignalRClientPlugin>();
}

public record SignalRClientConfiguration
{
    internal Func<IHubConnectionBuilder, IHubConnectionBuilder> hubConnectionBuilderConfig;
    public SignalRClientConfiguration WithHubConnectionConfiguration(Func<IHubConnectionBuilder, IHubConnectionBuilder> hubConnectionBuilderConfig)
        => this with { hubConnectionBuilderConfig = hubConnectionBuilderConfig, };
}
