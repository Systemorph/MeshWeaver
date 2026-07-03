using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using MeshWeaver.Hosting.Grpc;
using MeshWeaver.Hosting.Grpc.Protocol;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Hosting.Grpc.Test;

/// <summary>
/// Network-free end-to-end proof of the gRPC mesh transport: drives <see cref="MeshGrpcService.Open"/>
/// over in-memory duplex streams against a REAL mesh — no Kestrel, no h2c, deterministic. It exercises
/// the full path a foreign-language participant takes: <c>connect</c> frame → participant registered
/// (route + hosted proxy hub) → <c>ack</c> frame; then a <c>deliver</c> frame carrying a real
/// <see cref="MessageDelivery{T}"/> → re-stamped, injected, routed, dispatched to the mesh hub's
/// handler → response addressed back to the participant → routed to its hosted hub → forwarded as a
/// <c>receive</c> frame.
///
/// <para>The wire delivery is produced/consumed by the framework's own <c>JsonSerializerOptions</c>,
/// reusing the entire mesh serialization stack unchanged — so this also emits the canonical envelope
/// sample the Python/Node SDKs pin their <c>envelope.py</c> against (inspect <c>received.Receive</c>).</para>
/// </summary>
public class MeshGrpcTransportTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    public record EchoRequest(string Text) : IRequest<EchoResponse>;
    public record EchoResponse(string Text);

    /// <summary>Answers with the identity the delivery arrived under — pins the trust semantics.</summary>
    public record WhoAmIRequest : IRequest<WhoAmIResponse>;
    public record WhoAmIResponse(string ObjectId);

    // Fires when the echo handler actually runs — lets the test tell "request never reached the
    // handler" apart from "handler ran but the response didn't route back".
    private readonly TaskCompletionSource<string> handlerHit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Lightweight mesh (ConfigureMeshBase, no Graph sample load) + the gRPC transport + an echo handler.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddGrpcHub()
            .ConfigureHub(config =>
            {
                config.TypeRegistry.WithType(typeof(EchoRequest), nameof(EchoRequest));
                config.TypeRegistry.WithType(typeof(EchoResponse), nameof(EchoResponse));
                config.TypeRegistry.WithType(typeof(WhoAmIRequest), nameof(WhoAmIRequest));
                config.TypeRegistry.WithType(typeof(WhoAmIResponse), nameof(WhoAmIResponse));
                return config.WithHandler<EchoRequest>((hub, request) =>
                    {
                        handlerHit.TrySetResult(request.Message.Text);
                        hub.Post(new EchoResponse(request.Message.Text), o => o.ResponseFor(request));
                        return request.Processed();
                    })
                    .WithHandler<WhoAmIRequest>((hub, request) =>
                    {
                        hub.Post(new WhoAmIResponse(request.AccessContext?.ObjectId ?? "<none>"),
                            o => o.ResponseFor(request));
                        return request.Processed();
                    });
            });

    [Fact]
    public async Task Participant_request_round_trips_through_the_grpc_transport()
    {
        var hub = Mesh;
        var registry = hub.ServiceProvider.GetRequiredService<GrpcConnectionRegistry>();
        var service = new MeshGrpcService(hub, registry);

        var inbound = Channel.CreateUnbounded<ClientFrame>();
        var writer = new CapturingStreamWriter<ServerFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var context = new FakeServerCallContext(new Metadata(), cts.Token);

        // Run the transport. It stays alive until the inbound stream completes / cancels.
        var open = service.Open(new ChannelStreamReader<ClientFrame>(inbound.Reader), writer, context);

        // 1) connect: register this participant's address for inbound routing.
        var participant = new Address(GrpcHostingExtensions.PythonAddressType, Guid.NewGuid().ToString("N"));
        await inbound.Writer.WriteAsync(new ClientFrame
        {
            Connect = JsonSerializer.Serialize(participant, hub.JsonSerializerOptions)
        });
        // Outbound transport works: the connect frame produced an ack frame back over the bidi stream.
        var ack = await NextFrame(writer, "ack", TimeSpan.FromSeconds(10));
        Assert.Equal(ServerFrame.KindOneofCase.Ack, ack.KindCase);
        Assert.Equal(participant.ToString(), ack.Ack.Address);

        // 2) deliver: a real request from the participant, framed over gRPC, into the mesh.
        var delivery = new MessageDelivery<EchoRequest>(
            participant, hub.Address, new EchoRequest("hello mesh"), hub.JsonSerializerOptions);
        await inbound.Writer.WriteAsync(new ClientFrame
        {
            Deliver = JsonSerializer.Serialize<IMessageDelivery>(delivery, hub.JsonSerializerOptions)
        });

        // Inbound transport works: the request was re-stamped, injected, routed, and dispatched to
        // the mesh hub's handler with its payload intact.
        var handled = await handlerHit.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("hello mesh", handled);

        // 3) receive: the echo response, routed by the mesh back to the participant (reached via its
        // hosted proxy hub) and framed onto the gRPC stream. Closes the full round trip.
        string? received = null;
        for (var i = 0; i < 20 && received is null; i++)
        {
            ServerFrame f;
            try { f = await NextFrame(writer, "response", TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { break; }
            if (f.KindCase == ServerFrame.KindOneofCase.Receive && f.Receive.Contains("hello mesh"))
                received = f.Receive;
        }

        Output.WriteLine($"canonical wire delivery:\n{received}");
        Assert.NotNull(received);
        Assert.Contains("hello mesh", received!);  // response payload round-tripped to the participant
        Assert.Contains(delivery.Id, received);    // correlated to the request (RequestId == request delivery id)

        inbound.Writer.Complete();
        await open;
    }

    [Fact]
    public async Task Trusted_endpoint_identity_semantics()
    {
        // The trust boundary is the POD: only same-pod containers (the shipped node / bun / python
        // gates) can reach the loopback-bound trusted port, so arriving on it IS the authentication —
        // no token, nothing to rotate. Three semantics pinned here:
        //   1. trusted + carried AccessContext → passes through (a gate executing a user's request
        //      writes back under that user's identity, like the in-process C# kernel),
        //   2. trusted + no context → the well-known System principal,
        //   3. UNTRUSTED + forged context → re-stamped to the connection's own (Anonymous) identity.
        var hub = Mesh;
        var registry = hub.ServiceProvider.GetRequiredService<GrpcConnectionRegistry>();
        var service = new MeshGrpcService(hub, registry,
            Options.Create(new GrpcOptions { TrustedPort = 8082 }));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var trusted = new DefaultHttpContext();
        trusted.Connection.LocalPort = 8082;
        Assert.Equal("alice", await WhoAmI(service, cts.Token, trusted,
            new AccessContext { ObjectId = "alice", Name = "Alice" }));
        Assert.Equal(WellKnownUsers.System, await WhoAmI(service, cts.Token, trusted, null));
        // An EMPTY carried context (no ObjectId) is not an identity — falls back to System,
        // never an empty principal.
        Assert.Equal(WellKnownUsers.System, await WhoAmI(service, cts.Token, trusted,
            new AccessContext { ObjectId = "", Name = "" }));

        var untrusted = new DefaultHttpContext();
        untrusted.Connection.LocalPort = 8081;
        Assert.Equal(WellKnownUsers.Anonymous, await WhoAmI(service, cts.Token, untrusted,
            new AccessContext { ObjectId = "alice", Name = "Alice" }));
    }

    private async Task<string> WhoAmI(
        MeshGrpcService service, CancellationToken ct, HttpContext http, AccessContext? carried)
    {
        var hub = Mesh;
        var participant = new Address(GrpcHostingExtensions.PythonAddressType, Guid.NewGuid().ToString("N"));
        var inbound = Channel.CreateUnbounded<ClientFrame>();
        var writer = new CapturingStreamWriter<ServerFrame>();
        var open = service.Open(new ChannelStreamReader<ClientFrame>(inbound.Reader), writer,
            new FakeServerCallContext(new Metadata(), ct, http));
        await inbound.Writer.WriteAsync(new ClientFrame
        {
            Connect = JsonSerializer.Serialize(participant, hub.JsonSerializerOptions)
        }, ct);
        var ack = await NextFrame(writer, "connect ack", TimeSpan.FromSeconds(10));
        Assert.Equal(ServerFrame.KindOneofCase.Ack, ack.KindCase);

        IMessageDelivery delivery = new MessageDelivery<WhoAmIRequest>(
            participant, hub.Address, new WhoAmIRequest(), hub.JsonSerializerOptions);
        if (carried is not null)
            delivery = delivery.SetAccessContext(carried);
        await inbound.Writer.WriteAsync(new ClientFrame
        {
            Deliver = JsonSerializer.Serialize(delivery, hub.JsonSerializerOptions)
        }, ct);

        for (var i = 0; i < 20; i++)
        {
            var f = await NextFrame(writer, "whoami response", TimeSpan.FromSeconds(10));
            if (f.KindCase != ServerFrame.KindOneofCase.Receive || !f.Receive.Contains(nameof(WhoAmIResponse)))
                continue;
            inbound.Writer.Complete();
            await open;
            using var doc = JsonDocument.Parse(f.Receive);
            return doc.RootElement.GetProperty("message").GetProperty("objectId").GetString()!;
        }
        throw new TimeoutException("no WhoAmIResponse frame");
    }

    [Fact]
    public async Task Unregistered_message_type_forwards_between_participants_as_raw_json()
    {
        // The participant contract: a custom protocol type (e.g. the Python pandas node's
        // PandasCommand) is OPAQUE to the mesh — it routes by target address and round-trips as
        // RawJson, needing NO server-side registration. The defect this pins: the participant's
        // hosted proxy hub is the delivery TARGET, so UnpackIfNecessary ran there and failed the
        // delivery ("type not registered in this hub's TypeRegistry") instead of forwarding it.
        var hub = Mesh;
        var registry = hub.ServiceProvider.GetRequiredService<GrpcConnectionRegistry>();
        var service = new MeshGrpcService(hub, registry);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Two foreign participants, A and B, each on its own bidi stream.
        var (participantA, inboundA, writerA, openA) = await OpenParticipant(service, cts.Token);
        var (participantB, inboundB, writerB, openB) = await OpenParticipant(service, cts.Token);

        // A -> B: a message whose $type is registered NOWHERE on the server.
        var payload = """{"$type":"TotallyCustomCommand","command":"load","path":"PythonDemo/SalesData"}""";
        var delivery = new MessageDelivery<RawJson>(participantA, participantB, new RawJson(payload), hub.JsonSerializerOptions);
        await inboundA.Writer.WriteAsync(new ClientFrame
        {
            Deliver = JsonSerializer.Serialize<IMessageDelivery>(delivery, hub.JsonSerializerOptions)
        });

        // B receives it verbatim as a receive frame — the custom protocol survived the proxy hub.
        string? received = null;
        for (var i = 0; i < 20 && received is null; i++)
        {
            var f = await NextFrame(writerB, "forwarded custom command", TimeSpan.FromSeconds(10));
            if (f.KindCase == ServerFrame.KindOneofCase.Receive && f.Receive.Contains("TotallyCustomCommand"))
                received = f.Receive;
        }
        Assert.NotNull(received);
        Assert.Contains("PythonDemo/SalesData", received!);

        // And A got no DeliveryFailure for it.
        while (writerA.Output.TryRead(out var frame))
            Assert.DoesNotContain(nameof(DeliveryFailure), frame.Receive ?? "");

        inboundA.Writer.Complete();
        inboundB.Writer.Complete();
        await Task.WhenAll(openA, openB);
    }

    private async Task<(Address Participant, Channel<ClientFrame> Inbound, CapturingStreamWriter<ServerFrame> Writer, Task Open)>
        OpenParticipant(MeshGrpcService service, CancellationToken ct)
    {
        var hub = Mesh;
        var participant = new Address(GrpcHostingExtensions.PythonAddressType, Guid.NewGuid().ToString("N"));
        var inbound = Channel.CreateUnbounded<ClientFrame>();
        var writer = new CapturingStreamWriter<ServerFrame>();
        var open = service.Open(new ChannelStreamReader<ClientFrame>(inbound.Reader), writer,
            new FakeServerCallContext(new Metadata(), ct));
        await inbound.Writer.WriteAsync(new ClientFrame
        {
            Connect = JsonSerializer.Serialize(participant, hub.JsonSerializerOptions)
        }, ct);
        // Wait for the ack so the participant's route + proxy hub are registered before use.
        var ack = await NextFrame(writer, "connect ack", TimeSpan.FromSeconds(10));
        Assert.Equal(ServerFrame.KindOneofCase.Ack, ack.KindCase);
        return (participant, inbound, writer, open);
    }

    [Fact]
    public async Task WebSplit_request_round_trips_via_connect_and_deliver()
    {
        // The gRPC-web split (browsers / React Native — no bidi, no http2): a server-streaming Connect
        // (mesh→client) + a unary Deliver (client→mesh), driven over in-memory streams.
        var hub = Mesh;
        var registry = hub.ServiceProvider.GetRequiredService<GrpcConnectionRegistry>();
        var service = new MeshGrpcService(hub, registry);

        var stream = new CapturingStreamWriter<ServerFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var participant = new Address(GrpcHostingExtensions.NodeAddressType, Guid.NewGuid().ToString("N"));

        // Connect: server-streaming. Its ack carries the connection id the client passes to Deliver.
        var connect = service.Connect(
            new ConnectRequest { Address = JsonSerializer.Serialize(participant, hub.JsonSerializerOptions) },
            stream,
            new FakeServerCallContext(new Metadata(), cts.Token));
        var ack = await NextFrame(stream, "ack", TimeSpan.FromSeconds(10));
        Assert.Equal(ServerFrame.KindOneofCase.Ack, ack.KindCase);
        Assert.False(string.IsNullOrEmpty(ack.Ack.ConnectionId));

        // Deliver: unary. Injects the participant's request, tied to the connection id.
        var delivery = new MessageDelivery<EchoRequest>(
            participant, hub.Address, new EchoRequest("hello web split"), hub.JsonSerializerOptions);
        await service.Deliver(
            new DeliverRequest
            {
                ConnectionId = ack.Ack.ConnectionId,
                Delivery = JsonSerializer.Serialize<IMessageDelivery>(delivery, hub.JsonSerializerOptions),
            },
            new FakeServerCallContext(new Metadata(), cts.Token));

        // The echo response routes back over the Connect server-stream.
        string? received = null;
        for (var i = 0; i < 20 && received is null; i++)
        {
            ServerFrame f;
            try { f = await NextFrame(stream, "response", TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { break; }
            if (f.KindCase == ServerFrame.KindOneofCase.Receive && f.Receive.Contains("hello web split"))
                received = f.Receive;
        }
        Assert.NotNull(received);
        Assert.Contains("hello web split", received!); // payload round-tripped over the split transport
        Assert.Contains(delivery.Id, received);        // correlated (RequestId == request id)

        cts.Cancel();
        await connect;
    }

    // Read the next outbound frame, failing fast with a clear locus instead of hanging to the watchdog.
    private static async Task<ServerFrame> NextFrame(CapturingStreamWriter<ServerFrame> writer, string step, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await writer.Output.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"no '{step}' frame within {timeout}");
        }
    }

    private sealed class ChannelStreamReader<T>(ChannelReader<T> reader) : IAsyncStreamReader<T>
    {
        public T Current { get; private set; } = default!;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            try
            {
                Current = await reader.ReadAsync(cancellationToken);
                return true;
            }
            catch (ChannelClosedException) { return false; }
            catch (OperationCanceledException) { return false; }
        }
    }

    private sealed class CapturingStreamWriter<T> : IServerStreamWriter<T>
    {
        private readonly Channel<T> channel = Channel.CreateUnbounded<T>();
        public ChannelReader<T> Output => channel.Reader;
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message)
        {
            channel.Writer.TryWrite(message);
            return Task.CompletedTask;
        }
    }

    // Minimal ServerCallContext — MeshGrpcService.Open reads RequestHeaders + CancellationToken +
    // UserState (the AspNetCore HttpContext slot, used for the trusted-endpoint local-port check).
    private sealed class FakeServerCallContext(Metadata headers, CancellationToken ct, HttpContext? httpContext = null)
        : ServerCallContext
    {
        private readonly Dictionary<object, object> userState = httpContext is null
            ? new Dictionary<object, object>()
            : new Dictionary<object, object> { ["__HttpContext"] = httpContext };

        protected override IDictionary<object, object> UserStateCore => userState;

        protected override string MethodCore => MeshGrpcService.ServiceName + "/Open";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:0";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => headers;
        protected override CancellationToken CancellationTokenCore => ct;
        protected override Metadata ResponseTrailersCore { get; } = new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } =
            new(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
