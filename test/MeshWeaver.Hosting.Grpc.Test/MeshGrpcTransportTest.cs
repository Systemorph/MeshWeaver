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
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
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
                return config.WithHandler<EchoRequest>((hub, request) =>
                {
                    handlerHit.TrySetResult(request.Message.Text);
                    hub.Post(new EchoResponse(request.Message.Text), o => o.ResponseFor(request));
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

        output.WriteLine($"canonical wire delivery:\n{received}");
        Assert.NotNull(received);
        Assert.Contains("hello mesh", received!);  // response payload round-tripped to the participant
        Assert.Contains(delivery.Id, received);    // correlated to the request (RequestId == request delivery id)

        inbound.Writer.Complete();
        await open;
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

    // Minimal ServerCallContext — MeshGrpcService.Open only reads RequestHeaders + CancellationToken.
    private sealed class FakeServerCallContext(Metadata headers, CancellationToken ct) : ServerCallContext
    {
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
