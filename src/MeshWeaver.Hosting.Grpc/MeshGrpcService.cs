using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Channels;
using Grpc.Core;
using MeshWeaver.Hosting.Grpc.Protocol;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Grpc;

/// <summary>
/// The gRPC endpoint a remote mesh participant connects to — a single bidirectional stream IS the
/// participant connection, the gRPC counterpart of <c>SignalRConnectionHub</c>. The first inbound
/// frame's <c>connect</c> registers the participant's address for inbound routing; each subsequent
/// <c>deliver</c> injects the participant's outbound message into the mesh under its validated
/// identity; mesh deliveries addressed to the participant are written back as <c>receive</c> frames.
///
/// <para><b>async/await here is the transport boundary</b> — exactly as <c>SignalRConnectionHub</c>'s
/// hub methods are <c>async Task</c>. Once a frame enters <see cref="GrpcConnectionRegistry"/>
/// everything is reactive and runs off this boundary. The bidi stream is read (inbound) and written
/// (outbound) concurrently; gRPC forbids two concurrent writes to one response stream, so every
/// outbound frame — the connect ack AND mesh deliveries — funnels through one pump draining the
/// connection's <see cref="Channel{T}"/>.</para>
/// </summary>
public sealed class MeshGrpcService(IMessageHub hub, GrpcConnectionRegistry registry) : Protocol.Mesh.MeshBase
{
    /// <summary>The fully-qualified gRPC service name the endpoint is mapped at.</summary>
    public const string ServiceName = "meshweaver.v1.Mesh";

    /// <inheritdoc />
    public override async Task Open(
        IAsyncStreamReader<ClientFrame> requestStream,
        IServerStreamWriter<ServerFrame> responseStream,
        ServerCallContext context)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        var outbound = Channel.CreateUnbounded<ServerFrame>(new UnboundedChannelOptions { SingleReader = true });
        registry.Begin(connectionId, outbound.Writer);

        // ONE writer owns the response stream (gRPC forbids concurrent writes). It drains every
        // outbound frame — connect ack + mesh deliveries the registry enqueues — to the wire.
        var pump = WritePumpAsync(outbound.Reader, responseStream);
        try
        {
            // Boundary bridge: validate the Bearer token (gRPC call metadata) once per connection.
            await registry.Authenticate(connectionId, ExtractBearerToken(context))
                .FirstAsync().ToTask(context.CancellationToken);

            await foreach (var frame in requestStream.ReadAllAsync(context.CancellationToken))
            {
                switch (frame.KindCase)
                {
                    case ClientFrame.KindOneofCase.Connect:
                        var address = JsonSerializer.Deserialize<Address>(frame.Connect, hub.JsonSerializerOptions)
                            ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "Address did not deserialize."));
                        registry.Connect(address, connectionId);
                        await outbound.Writer.WriteAsync(
                            new ServerFrame { Ack = new ConnectAck { Address = address.ToString() } },
                            context.CancellationToken);
                        break;
                    case ClientFrame.KindOneofCase.Deliver:
                        var delivery = JsonSerializer.Deserialize<IMessageDelivery>(frame.Deliver, hub.JsonSerializerOptions);
                        if (delivery is not null)
                            registry.Deliver(connectionId, delivery);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Client disconnected / call cancelled — normal stream teardown.
        }
        finally
        {
            outbound.Writer.TryComplete();
            registry.Disconnect(connectionId);
            await pump;
        }
    }

    private static async Task WritePumpAsync(ChannelReader<ServerFrame> reader, IServerStreamWriter<ServerFrame> responseStream)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync())
                await responseStream.WriteAsync(frame);
        }
        catch (OperationCanceledException)
        {
            // Response stream torn down (client gone) — stop pumping.
        }
    }

    // gRPC metadata keys are lower-cased. The participant sends the API token as
    // "authorization: Bearer <token>"; accept that single shape.
    private static string? ExtractBearerToken(ServerCallContext context)
    {
        var auth = context.RequestHeaders.GetValue("authorization");
        return auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim()
            : null;
    }
}
