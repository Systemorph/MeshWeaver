using OpenSmc.Application.SignalR;
using OpenSmc.Messaging;
using Orleans.Serialization;
using static OpenSmc.Hosting.HostBuilderExtensions;

namespace OpenSmc.Application.Host;

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
                    serializerBuilder.AddJsonSerializer(isSupported: type => true);
                });
                
                siloBuilder.Services.AddOrleansHub();
            });

        using var app = builder.Build();

        app
            .UseRouting()
            .UseApplicationSignalR();

        await app.RunAsync();
    }
}

public static class OrleansMessageHubExtensions
{
    public static IServiceCollection AddOrleansHub(this IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetOrleansHub());
        return services;
    }

    public static IMessageHub GetOrleansHub(this IServiceProvider serviceProvider) 
        => serviceProvider.CreateMessageHub(new OrleansAddress(), conf => 
            conf
        );
}

record OrleansAddress;

// HACK V10: these TestRequest/TestResponse should not be a part of Prod setup (2024/04/17, Dmitry Kalabin)
public record TestRequest : IRequest<TestResponse>;
public record TestResponse;
