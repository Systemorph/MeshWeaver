using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;

namespace OpenSmc.Application.SignalR;

public static class SignalRExtensions
{
    public const string DefaultSignalREndpoint = "/signalR/application";

    public static IServiceCollection ConfigureApplicationSignalR(this IServiceCollection services, Func<MessageHubConfiguration, MessageHubConfiguration> configuration = null)
    {
        configuration ??= ConfigureSignalRHub;
        services.AddSingleton(sp => sp.CreateMessageHub(new SignalRAddress(), configuration));
        services.AddSignalR(o =>
            {
                o.EnableDetailedErrors = true; // TODO: False for Prod environment (2021/05/14, Alexander Yolokhov)
                o.MaximumReceiveMessageSize = 400000; // TODO: see what's recommended size (2021/12/07, Alexander Kravets)
            })
            .AddJsonProtocolFrom((JsonHubProtocolOptions o, IMessageHub<SignalRAddress> hub) =>
            {
                o.PayloadSerializerOptions = hub.JsonSerializerOptions;
            });
        services.AddSingleton<GroupsSubscriptions<string>>();

        return services;
    }

    public static TBuilder AddJsonProtocolFromHub<TBuilder>(this TBuilder builder, Action<JsonHubProtocolOptions, IMessageHub> configuration) where TBuilder : ISignalRBuilder 
        => builder.AddJsonProtocolFrom(configuration);

    private static TBuilder AddJsonProtocolFrom<TBuilder, TDep>(this TBuilder builder, Action<JsonHubProtocolOptions, TDep> configuration)
        where TBuilder : ISignalRBuilder
        where TDep : class
    {
        builder.AddJsonProtocol()
            .Services.AddOptions<JsonHubProtocolOptions>()
                .PostConfigure(configuration);
        return builder;
    }

    public static IApplicationBuilder UseApplicationSignalR(this IApplicationBuilder app)
    {
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHub<ApplicationHub>(DefaultSignalREndpoint);
        });

        return app;
    }

    private static MessageHubConfiguration ConfigureSignalRHub(MessageHubConfiguration conf)
        => conf
            .WithTypes(typeof(UiAddress), typeof(ApplicationAddress))
            .WithSerialization(serialization =>
                serialization.WithOptions(options =>
                {
                    if (!options.Converters.Any(c => c is RawJsonConverter))
                        options.Converters.Insert(0, new RawJsonConverter(serialization.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>()));
                })
            );
}

public record SignalRAddress;
