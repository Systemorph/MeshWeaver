using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using MeshWeaver.Hosting.Grpc;
using MeshWeaver.Hosting.Grpc.Protocol;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Grpc.Test;

/// <summary>
/// The trusted-endpoint semantics over a REAL Kestrel server and real gRPC channels — the two seams
/// the in-memory transport test cannot cover: <c>Grpc:TrustedPort</c> bound from configuration (the
/// chart injects it as the <c>Grpc__TrustedPort</c> env var) and the accepted connection's actual
/// <c>Connection.LocalPort</c> as the trust discriminator. Two h2c endpoints stand in for the
/// deployment's public (8081) and trusted-loopback (8082) ports.
/// </summary>
public class MeshGrpcTrustedKestrelTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    public record WhoAmIRequest : IRequest<WhoAmIResponse>;
    public record WhoAmIResponse(string ObjectId);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddGrpcHub()
            .ConfigureHub(config =>
            {
                config.TypeRegistry.WithType(typeof(WhoAmIRequest), nameof(WhoAmIRequest));
                config.TypeRegistry.WithType(typeof(WhoAmIResponse), nameof(WhoAmIResponse));
                return config.WithHandler<WhoAmIRequest>((hub, request) =>
                {
                    hub.Post(new WhoAmIResponse(request.AccessContext?.ObjectId ?? "<none>"),
                        o => o.ResponseFor(request));
                    return request.Processed();
                });
            });

    [Fact]
    public async Task Trusted_port_is_detected_on_real_kestrel_with_config_bound_options()
    {
        var publicPort = FreePort();
        var trustedPort = FreePort();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // The exact shape the helm chart injects: Grpc__TrustedPort → Grpc:TrustedPort.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Grpc:TrustedPort"] = trustedPort.ToString(),
        });
        builder.Services.AddGrpc();
        builder.Services.AddOptions<GrpcOptions>().BindConfiguration(GrpcOptions.SectionName);
        // The gRPC service resolves the MESH's hub + registry (the app is just the transport shell).
        builder.Services.AddSingleton(Mesh);
        builder.Services.AddSingleton(Mesh.ServiceProvider.GetRequiredService<GrpcConnectionRegistry>());
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // h2c needs a dedicated Http2 endpoint — the same layout the chart configures.
            kestrel.ListenLocalhost(publicPort, l => l.Protocols = HttpProtocols.Http2);
            kestrel.ListenLocalhost(trustedPort, l => l.Protocols = HttpProtocols.Http2);
        });

        await using var app = builder.Build();
        app.MapMeshWeaverGrpc();
        await app.StartAsync();
        try
        {
            // 1) trusted + carried context → passes through (the gate acts as the requesting user).
            Assert.Equal("alice", await WhoAmIOver($"http://localhost:{trustedPort}",
                new AccessContext { ObjectId = "alice", Name = "Alice" }));

            // 2) trusted + no context → the well-known System principal.
            Assert.Equal(WellKnownUsers.System, await WhoAmIOver($"http://localhost:{trustedPort}", null));

            // 3) public port + forged context → re-stamped to the connection's (Anonymous) identity.
            Assert.Equal(WellKnownUsers.Anonymous, await WhoAmIOver($"http://localhost:{publicPort}",
                new AccessContext { ObjectId = "alice", Name = "Alice" }));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private async Task<string> WhoAmIOver(string url, AccessContext? carried)
    {
        var hub = Mesh;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var channel = GrpcChannel.ForAddress(url);
        var client = new Protocol.Mesh.MeshClient(channel);
        using var call = client.Open(cancellationToken: cts.Token);

        var participant = new Address(GrpcHostingExtensions.PythonAddressType, Guid.NewGuid().ToString("N"));
        await call.RequestStream.WriteAsync(new ClientFrame
        {
            Connect = JsonSerializer.Serialize(participant, hub.JsonSerializerOptions)
        }, cts.Token);
        Assert.True(await call.ResponseStream.MoveNext(cts.Token));
        Assert.Equal(ServerFrame.KindOneofCase.Ack, call.ResponseStream.Current.KindCase);

        IMessageDelivery delivery = new MessageDelivery<WhoAmIRequest>(
            participant, hub.Address, new WhoAmIRequest(), hub.JsonSerializerOptions);
        if (carried is not null)
            delivery = delivery.SetAccessContext(carried);
        await call.RequestStream.WriteAsync(new ClientFrame
        {
            Deliver = JsonSerializer.Serialize(delivery, hub.JsonSerializerOptions)
        }, cts.Token);

        while (await call.ResponseStream.MoveNext(cts.Token))
        {
            var frame = call.ResponseStream.Current;
            if (frame.KindCase != ServerFrame.KindOneofCase.Receive
                || !frame.Receive.Contains(nameof(WhoAmIResponse)))
                continue;
            await call.RequestStream.CompleteAsync();
            using var doc = JsonDocument.Parse(frame.Receive);
            return doc.RootElement.GetProperty("message").GetProperty("objectId").GetString()!;
        }
        throw new TimeoutException("no WhoAmIResponse frame received");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
