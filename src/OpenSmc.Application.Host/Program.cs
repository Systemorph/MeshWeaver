using OpenSmc.Application.Orleans;
using OpenSmc.Application.SignalR;
using OpenSmc.Messaging;
using OpenSmc.Messaging.Serialization;
using Orleans.Serialization;
using static OpenSmc.Hosting.HostBuilderExtensions;

namespace OpenSmc.Application.Host;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.ConfigureApplicationSignalR(c =>
            c.WithForwardToOrleansGrain<ApplicationAddress, IApplicationGrain>(
                a => a.ToString(), // TODO V10: need to make this deterministic (2024/04/22, Dmitry Kalabin)
                (g, d) => g.DeliverMessage(d)
            )
        );

        builder
            .Host.UseOpenSmc(new RouterAddress(), conf => conf.ConfigureRouter())
            .UseOrleans(static siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering();
                siloBuilder.Services.AddSerializer(serializerBuilder =>
                {
                    serializerBuilder.AddJsonSerializer(
                        type => true,
                        type => true,
                        ob =>
                            ob.PostConfigure<IMessageHub>(
                                (o, hub) => o.SerializerOptions = hub.JsonSerializerOptions
                            )
                    );
                });
                siloBuilder
                    .AddMemoryStreams(ApplicationStreamProviders.AppStreamProvider)
                    .AddMemoryGrainStorage("PubSubStore");

            });

        using var app = builder.Build();

        app.UseRouting().UseApplicationSignalR();

        await app.RunAsync();
    }
}

public static class OrleansMessageHubExtensions
{
    public static MessageHubConfiguration ConfigureRouter(this MessageHubConfiguration conf) =>
        conf.WithTypes(typeof(UiAddress), typeof(ApplicationAddress))
            .WithHostedHub(
                new ApplicationAddress(TestApplication.Name, TestApplication.Environment),
                config =>
                    // HACK V10: this is just for testing and should not be a part of Prod setup (2024/04/17, Dmitry Kalabin)
                    config.WithHandler<TestRequest>(
                        (hub, request) =>
                        {
                            hub.Post(new TestResponse(), options => options.ResponseFor(request));
                            return request.Processed();
                        }
                    )
            )
            .WithForwardThroughOrleansStream<UiAddress>(ApplicationStreamNamespaces.Ui, a => a.Id);
}

record RouterAddress;

// HACK V10: these TestRequest/TestResponse should not be a part of Prod setup (2024/04/17, Dmitry Kalabin)
public record TestRequest : IRequest<TestResponse>;

public record TestResponse;
