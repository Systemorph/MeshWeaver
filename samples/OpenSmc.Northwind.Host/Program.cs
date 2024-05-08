using OpenSmc.Application;
using OpenSmc.Application.Orleans;
using OpenSmc.Messaging;
using Orleans.Serialization;
using static OpenSmc.Application.SignalR.SignalRExtensions;
using static OpenSmc.Hosting.HostBuilderExtensions;

namespace OpenSmc.Northwind.Host;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.ConfigureApplicationSignalR();

        builder.Host
            .ConfigureServiceProvider()
            .UseOrleans(static siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                siloBuilder.Services.AddSerializer(serializerBuilder =>
                {
                    serializerBuilder.AddJsonSerializer(_ => true, _ => true, ob => ob.PostConfigure<IMessageHub>((o, hub) => o.SerializerOptions = hub.JsonSerializerOptions));
                });
                siloBuilder
                    .AddMemoryStreams(ApplicationStreamProviders.AppStreamProvider)
                    .AddMemoryGrainStorage("PubSubStore");

                siloBuilder.Services.AddRouterHub();
            });

        await using var app = builder.Build();

        app
            .UseRouting()
            .UseApplicationSignalR();

        await app.RunAsync();
    }
}

internal static class RouterHubExtensions
{
    internal static IServiceCollection AddRouterHub(this IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRouterHub());
        return services;
    }

    private static IMessageHub GetRouterHub(this IServiceProvider serviceProvider)
        => serviceProvider.CreateMessageHub(new RouterAddress(), conf =>
            conf
                .WithRoutes(forward => forward
                    .RouteAddressToHostedHub<ApplicationAddress>(ConfigureApplication)
                )
        );

    private static MessageHubConfiguration ConfigureApplication(MessageHubConfiguration configuration)
        => configuration;
}

internal record RouterAddress;
