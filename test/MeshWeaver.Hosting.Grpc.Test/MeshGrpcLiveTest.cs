using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Grpc;
using MeshWeaver.Hosting.Grpc.Protocol;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Grpc.Test;

/// <summary>
/// LIVE end-to-end proof over a real Kestrel gRPC endpoint (HTTP/2 cleartext, h2c) — beyond the
/// in-memory harness. Stands up a monolith mesh in a <see cref="WebApplication"/>, maps
/// <c>MapMeshWeaverGrpc()</c>, then connects a real <see cref="GrpcChannel"/> and drives the
/// <c>Open</c> bidi stream as a foreign participant would: connect → ack → deliver a real request →
/// receive the response routed back through the participant's hosted hub.
/// </summary>
public class MeshGrpcLiveTest(ITestOutputHelper output)
{
    public record EchoRequest(string Text) : IRequest<EchoResponse>;
    public record EchoResponse(string Text);

    [Fact]
    public async Task Round_trips_over_a_real_kestrel_grpc_endpoint()
    {
        // Allow gRPC over HTTP/2 cleartext (no TLS) for the loopback test endpoint.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
            // h2c on a dynamic port — dynamic binding requires an explicit loopback IP, not "localhost".
            k.Listen(System.Net.IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));

        builder.UseMeshWeaver(
            AddressExtensions.CreateMeshAddress(),
            mesh => mesh
                .UseMonolithMesh()
                .AddInMemoryPersistence()
                .AddRowLevelSecurity()
                .AddGraph()
                .AddGrpcHub()
                .ConfigureHub(config =>
                {
                    config.TypeRegistry.WithType(typeof(EchoRequest), nameof(EchoRequest));
                    config.TypeRegistry.WithType(typeof(EchoResponse), nameof(EchoResponse));
                    return config.WithHandler<EchoRequest>((hub, request) =>
                    {
                        hub.Post(new EchoResponse(request.Message.Text), o => o.ResponseFor(request));
                        return request.Processed();
                    });
                }));

        var app = builder.Build();
        app.MapMeshWeaverGrpc();
        await app.StartAsync();

        try
        {
            var url = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            output.WriteLine($"gRPC endpoint: {url}");

            // Same process, so reuse the mesh hub's serializer options + address to build a wire-faithful
            // delivery — the framework does the JSON; the gRPC channel carries it over the real wire.
            var meshHub = app.Services.GetRequiredService<IMessageHub>();
            var options = meshHub.JsonSerializerOptions;

            using var channel = GrpcChannel.ForAddress(url);
            var client = new Protocol.Mesh.MeshClient(channel);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var call = client.Open(cancellationToken: cts.Token);

            // 1) connect → ack
            var participant = new Address(GrpcHostingExtensions.NodeAddressType, Guid.NewGuid().ToString("N"));
            await call.RequestStream.WriteAsync(new ClientFrame { Connect = JsonSerializer.Serialize(participant.ToString()) });
            Assert.True(await call.ResponseStream.MoveNext(cts.Token));
            Assert.Equal(ServerFrame.KindOneofCase.Ack, call.ResponseStream.Current.KindCase);

            // 2) deliver a real request to the mesh hub's echo handler
            var delivery = new MessageDelivery<EchoRequest>(
                participant, meshHub.Address, new EchoRequest("hello kestrel"), options);
            await call.RequestStream.WriteAsync(new ClientFrame
            {
                Deliver = JsonSerializer.Serialize<IMessageDelivery>(delivery, options)
            });

            // 3) receive the echo response, routed back to the participant over the real gRPC stream
            string? received = null;
            while (received is null && await call.ResponseStream.MoveNext(cts.Token))
            {
                var frame = call.ResponseStream.Current;
                if (frame.KindCase == ServerFrame.KindOneofCase.Receive && frame.Receive.Contains("hello kestrel"))
                    received = frame.Receive;
            }

            output.WriteLine($"wire delivery:\n{received}");
            Assert.NotNull(received);
            Assert.Contains("hello kestrel", received!);  // response payload round-tripped over the wire
            Assert.Contains(delivery.Id, received);        // correlated (RequestId == request delivery id)

            await call.RequestStream.CompleteAsync();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
