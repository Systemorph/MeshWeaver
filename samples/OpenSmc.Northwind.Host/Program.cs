using OpenSmc.Application;
using OpenSmc.Application.Orleans;
using OpenSmc.Data;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using Orleans.Serialization;
using static OpenSmc.Application.SignalR.SignalRExtensions;
using static OpenSmc.Hosting.HostBuilderExtensions;

namespace OpenSmc.Northwind.Host;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.ConfigureApplicationSignalR();

        builder
            .Host.UseOpenSmc()
            .UseOrleans(static siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                siloBuilder.Services.AddSerializer(serializerBuilder =>
                {
                    serializerBuilder.AddJsonSerializer(
                        _ => true,
                        _ => true,
                        ob =>
                            ob.PostConfigure<IMessageHub>(
                                (o, hub) => o.SerializerOptions = hub.JsonSerializerOptions
                            )
                    );
                });
                siloBuilder
                    .AddMemoryStreams(ApplicationStreamProviders.AppStreamProvider)
                    .AddMemoryGrainStorage("PubSubStore");

                siloBuilder.Services.AddRouterHub();
            });

        await using var app = builder.Build();

        app.UseRouting().UseApplicationSignalR();

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

    private static IMessageHub GetRouterHub(this IServiceProvider serviceProvider) =>
        serviceProvider.CreateMessageHub(
            new RouterAddress(),
            conf =>
                conf.WithTypes(typeof(UiAddress), typeof(ApplicationAddress))
                    .WithRoutes(forward =>
                        forward
                            .RouteAddressToHostedHub<ApplicationAddress>(ConfigureApplication)
                            .RouteAddressToHostedHub<ReferenceDataAddress>(c =>
                                c.AddNorthwindReferenceData()
                            )
                    )
                    .WithForwardThroughOrleansStream<UiAddress>(
                        ApplicationStreamNamespaces.Ui,
                        a => a.Id
                    )
        );

    private static MessageHubConfiguration ConfigureApplication(
        MessageHubConfiguration configuration
    ) =>
        configuration.AddData(data =>
            data.FromHub(new ReferenceDataAddress(), ds => ds.AddNorthwindReferenceData())
        );
}

internal record RouterAddress;
