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
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
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
        using var publicPort = LoopbackPort.Reserve();
        using var trustedPort = LoopbackPort.Reserve();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // The exact shape the helm chart injects: Grpc__TrustedPort → Grpc:TrustedPort. The number
        // has to be known BEFORE the host starts — which is why the endpoints cannot simply bind :0
        // and read the port back afterwards, and why the ports are RESERVED instead (see LoopbackPort).
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Grpc:TrustedPort"] = trustedPort.Port.ToString(),
        });
        builder.Services.AddGrpc();
        builder.Services.AddOptions<GrpcOptions>().BindConfiguration(GrpcOptions.SectionName);
        // The gRPC service resolves the MESH's hub + registry (the app is just the transport shell).
        builder.Services.AddSingleton(Mesh);
        builder.Services.AddSingleton(Mesh.ServiceProvider.GetRequiredService<GrpcConnectionRegistry>());
        LoopbackPort.Handover(builder.Services, publicPort, trustedPort);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // h2c needs a dedicated Http2 endpoint — the same layout the chart configures.
            // Listen(IPAddress.Loopback, …), not ListenLocalhost: the latter binds the IPv4 AND the
            // IPv6 loopback, i.e. TWO sockets for one port number, and only one of them can be the
            // reserved one. The clients below therefore dial 127.0.0.1 explicitly — which is also
            // the address GrpcOptions.TrustedPort documents (http://127.0.0.1:{TrustedPort}).
            kestrel.Listen(IPAddress.Loopback, publicPort.Port, l => l.Protocols = HttpProtocols.Http2);
            kestrel.Listen(IPAddress.Loopback, trustedPort.Port, l => l.Protocols = HttpProtocols.Http2);
        });

        await using var app = builder.Build();
        app.MapMeshWeaverGrpc();
        await app.StartAsync();
        try
        {
            // 1) trusted + carried context → passes through (the gate acts as the requesting user).
            Assert.Equal("alice", await WhoAmIOver($"http://127.0.0.1:{trustedPort.Port}",
                new AccessContext { ObjectId = "alice", Name = "Alice" }));

            // 2) trusted + no context → the well-known System principal.
            Assert.Equal(WellKnownUsers.System, await WhoAmIOver($"http://127.0.0.1:{trustedPort.Port}", null));

            // 3) public port + forged context → re-stamped to the connection's (Anonymous) identity.
            Assert.Equal(WellKnownUsers.Anonymous, await WhoAmIOver($"http://127.0.0.1:{publicPort.Port}",
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

}

/// <summary>
/// Pins the ownership property <see cref="LoopbackPort"/> exists for (issue #2379), by running the
/// interleaving that reds CI <b>deterministically</b> rather than waiting for a loaded runner to
/// produce it: a rival grabs the port in the window between "which port is free?" and Kestrel's bind.
/// </summary>
public class LoopbackPortReservationTest
{
    /// <summary>
    /// Both halves are load-bearing, and each fails against the discover-then-release shape this
    /// replaced:
    /// <list type="number">
    /// <item>the rival is REFUSED — with a released port it wins, and Kestrel then cannot start
    /// (<c>IOException: … address already in use</c>, the exact CI failure);</item>
    /// <item>Kestrel binds and serves that port WHILE the reservation is still held — which is only
    /// possible because it adopts the reserved socket. Drop the handover and this half fails, because
    /// the reservation itself would be what blocks Kestrel.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Kestrel_binds_a_reserved_port_no_rival_can_take()
    {
        using var reserved = LoopbackPort.Reserve();

        Assert.Equal(SocketError.AddressAlreadyInUse, RivalBind(reserved.Port));

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        LoopbackPort.Handover(builder.Services, reserved);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, reserved.Port, l => l.Protocols = HttpProtocols.Http1));

        await using var app = builder.Build();
        app.MapGet("/", () => "served");
        await app.StartAsync();
        try
        {
            using var http = new HttpClient();
            Assert.Equal("served", await http.GetStringAsync($"http://127.0.0.1:{reserved.Port}/"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    /// What a competing process does to the port. Returns the refusal, or <c>null</c> when the rival
    /// took it — which is what "the port was unowned for a moment" looks like from outside.
    /// </summary>
    private static SocketError? RivalBind(int port)
    {
        using var rival = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            rival.Bind(new IPEndPoint(IPAddress.Loopback, port));
            rival.Listen(1);
            return null;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode;
        }
    }
}

/// <summary>
/// A loopback port this test <b>owns</b> from the moment it is chosen until Kestrel stops serving on
/// it — the cure for the time-of-check/time-of-use race that the previous <c>FreePort()</c> helper
/// was (issue #2379).
///
/// <para><b>The race.</b> <c>FreePort()</c> bound a socket to <c>:0</c>, read the port the kernel
/// assigned, and then <b>released it</b>. Between that release and Kestrel's own bind the port is
/// owned by nobody, so anything else on the machine — another test, another CI shard's process, an
/// ephemeral outbound connection — can take it, and Kestrel then fails to start with
/// <c>IOException: Failed to bind to address http://127.0.0.1:NNNNN: address already in use</c>. The
/// window is narrow on an idle box and wide on a loaded CI runner, which is why it reads as a flake
/// while being a plain ownership bug.</para>
///
/// <para><b>The cure is structural, not a retry.</b> The reservation socket is bound AND listening
/// and is never closed: the kernel refuses every other bind for as long as this object lives, so the
/// unowned window does not exist. Kestrel does not open a second socket for the port either — it is
/// handed <i>this</i> one through <see cref="SocketTransportOptions.CreateBoundListenSocket"/>, so
/// discovery and use are the same socket and there is nothing in between to lose. A retry loop, a
/// wider port range or a sleep would each only make the window smaller
/// (AGENTS.md → "No band-aids": never raise a bound to make a race less likely).</para>
///
/// <para>Binding <c>:0</c> and reading the port back from <c>IServerAddressesFeature</c> — the usual
/// cure — is not available here: this test's whole subject is <c>Grpc:TrustedPort</c> bound from
/// configuration <em>before</em> the host starts, so the number has to exist up front.</para>
/// </summary>
internal sealed class LoopbackPort : IDisposable
{
    private readonly Socket socket;

    private LoopbackPort(Socket socket)
    {
        this.socket = socket;
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>The reserved port. Owned by this object — no other process can bind it.</summary>
    public int Port { get; }

    /// <summary>Takes a free loopback port and keeps listening on it.</summary>
    public static LoopbackPort Reserve()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        // Listen, not just Bind: a bound-but-not-listening socket already blocks rival binds, but
        // Kestrel takes this exact socket over and expects a listener. Kestrel calls Listen again
        // with its own backlog, which on every platform just updates the accept queue depth.
        socket.Listen(512);
        return new LoopbackPort(socket);
    }

    /// <summary>
    /// Makes Kestrel adopt the reserved sockets instead of opening its own. The socket transport asks
    /// this factory for the listen socket of every endpoint it binds; for a reserved port we hand
    /// back the socket that already owns it, so the port is never re-acquired and can never be lost
    /// in between.
    /// </summary>
    public static void Handover(IServiceCollection services, params LoopbackPort[] reserved) =>
        services.Configure<SocketTransportOptions>(options =>
        {
            var byPort = reserved.ToDictionary(r => r.Port, r => r.socket);
            var openNewSocket = options.CreateBoundListenSocket;
            options.CreateBoundListenSocket = endpoint =>
                endpoint is IPEndPoint ip && byPort.TryGetValue(ip.Port, out var mine)
                    ? mine
                    : openNewSocket(endpoint);
        });

    /// <summary>
    /// Releases the port. Kestrel disposes the listen socket when it unbinds, and this disposes the
    /// same <see cref="Socket"/> instance — one handle, so the second dispose is a no-op rather than
    /// a double close.
    /// </summary>
    public void Dispose() => socket.Dispose();
}
