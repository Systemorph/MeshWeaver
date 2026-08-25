using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Channels;
using Grpc.Core;
using MeshWeaver.Hosting.Grpc.Protocol;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
public sealed class MeshGrpcService(
    IMessageHub hub,
    GrpcConnectionRegistry registry,
    IOptions<GrpcOptions>? options = null,
    ILogger<MeshGrpcService>? logger = null)
    : Protocol.Mesh.MeshBase
{
    /// <summary>The fully-qualified gRPC service name the endpoint is mapped at.</summary>
    public const string ServiceName = "meshweaver.v1.Mesh";

    /// <summary>
    /// Whether this call arrived on the TRUSTED loopback endpoint (<see cref="GrpcOptions.TrustedPort"/>).
    /// The trust boundary is the pod: only same-pod containers (the shipped node / bun / python gates)
    /// can reach a <c>127.0.0.1</c>-bound port, so the local port of the accepted connection IS the
    /// authentication. No configured trusted port ⇒ never trusted.
    /// </summary>
    private bool IsTrusted(ServerCallContext context)
    {
        if (options?.Value.TrustedPort is not int trustedPort)
            return false;
        try
        {
            // The official accessor: on real Kestrel the context IS an HttpContextServerCallContext
            // (a UserState probe does NOT work there — "__HttpContext" is only its legacy fallback,
            // which the in-memory transport tests use).
            return context.GetHttpContext().Connection.LocalPort == trustedPort;
        }
        catch (InvalidOperationException)
        {
            // Not an AspNetCore-hosted call (e.g. an in-memory context without the fallback entry) —
            // no HttpContext means no trusted-port evidence.
            return false;
        }
    }

    /// <summary>
    /// Boundary bridge: validate the connection's Bearer token (gRPC call metadata) once per
    /// connection; a call on the trusted loopback endpoint authenticates by reachability instead.
    /// When token validation is UNAVAILABLE (retryable infrastructure fault — issue #637) the
    /// registry errors with <see cref="TokenValidationUnavailableException"/>; this helper turns
    /// that into <see cref="StatusCode.Unavailable"/> — the status gRPC clients treat as
    /// retryable — instead of silently connecting a possibly-valid token as Anonymous. A
    /// DEFINITIVE invalid token still connects as Anonymous (unchanged: writes are cleanly
    /// RLS-denied, never fail-closed).
    /// </summary>
    private async Task AuthenticateOrAbortRetryable(string connectionId, ServerCallContext context)
    {
        try
        {
            await registry.Authenticate(connectionId, ExtractBearerToken(context), IsTrusted(context))
                .FirstAsync().ToTask(context.CancellationToken);
        }
        catch (TokenValidationUnavailableException ex)
        {
            throw new RpcException(new Status(
                StatusCode.Unavailable,
                "Token validation is temporarily unavailable — retry shortly. "
                + $"The token was NOT rejected; do not re-authenticate. ({ex.Message})"));
        }
    }

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
        var pump = WritePumpAsync(outbound.Reader, responseStream, context.CancellationToken, logger);
        try
        {
            // Boundary bridge: validate the Bearer token (gRPC call metadata) once per connection.
            // A call on the trusted loopback endpoint authenticates by reachability instead.
            await AuthenticateOrAbortRetryable(connectionId, context);

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

    /// <summary>
    /// gRPC-web entry (browsers / React Native, which can't do bidi or Node http2): the server-streaming
    /// half of the split — register the participant and stream mesh→client frames. Client→mesh deliveries
    /// arrive on separate unary <see cref="Deliver"/> calls keyed by the connection id sent in the ack.
    /// </summary>
    public override async Task Connect(
        ConnectRequest request,
        IServerStreamWriter<ServerFrame> responseStream,
        ServerCallContext context)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        var outbound = Channel.CreateUnbounded<ServerFrame>(new UnboundedChannelOptions { SingleReader = true });
        registry.Begin(connectionId, outbound.Writer);

        var pump = WritePumpAsync(outbound.Reader, responseStream, context.CancellationToken, logger);
        try
        {
            await AuthenticateOrAbortRetryable(connectionId, context);
            var address = JsonSerializer.Deserialize<Address>(request.Address, hub.JsonSerializerOptions)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "Address did not deserialize."));
            registry.Connect(address, connectionId);
            await outbound.Writer.WriteAsync(
                new ServerFrame { Ack = new ConnectAck { Address = address.ToString(), ConnectionId = connectionId } },
                context.CancellationToken);

            // Server-streaming has no inbound on this call; hold it open until the client cancels while the
            // pump drains mesh→client frames. (Transport-boundary await, like Open's foreach.)
            await Task.Delay(System.Threading.Timeout.Infinite, context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Client disconnected — normal teardown.
        }
        finally
        {
            outbound.Writer.TryComplete();
            registry.Disconnect(connectionId);
            await pump;
        }
    }

    /// <summary>
    /// gRPC-web entry: inject one client→mesh delivery for the connection established by <see cref="Connect"/>.
    /// The connection id is a server-issued capability (returned only on that client's Connect stream); the
    /// delivery is re-stamped with the connection's validated identity inside the registry.
    /// </summary>
    public override Task<DeliverAck> Deliver(DeliverRequest request, ServerCallContext context)
    {
        var delivery = JsonSerializer.Deserialize<IMessageDelivery>(request.Delivery, hub.JsonSerializerOptions);
        if (delivery is not null)
            registry.Deliver(request.ConnectionId, delivery);
        return Task.FromResult(new DeliverAck());
    }

    /// <summary>
    /// Drains the connection's outbound channel to the response stream. ONE writer owns that stream
    /// (gRPC forbids concurrent writes), so every outbound frame funnels through here.
    ///
    /// <para>🚨 <b>A client that went away is not a service-method failure</b> (#2138, #2139). The
    /// pump used to write without observing the call at all, so when the client dropped mid-stream
    /// gRPC answered the next write with
    /// <c>InvalidOperationException("Can't write the message because the request is complete.")</c>,
    /// the exception escaped through <c>Connect</c>'s <c>await pump</c> in its <c>finally</c>, and
    /// <c>Grpc.AspNetCore.Server.ServerCallHandler</c> logged <c>fail: Error when executing service
    /// method 'Connect'</c> for a routine browser disconnect.</para>
    ///
    /// <para>The fix is in two halves, and the first is the real one: the pump now <b>observes the
    /// call's cancellation token</b>, so a disconnect ENDS the drain instead of racing it. The second
    /// half exists because the race is inherent and cannot be closed by checking a flag — the request
    /// can complete between the token check and the write, and gRPC's only signal for that state is
    /// the exception itself. So a write that fails <i>because the call is over</i> ends the pump
    /// quietly, with the reason on a Debug line; <b>any other</b> <see cref="InvalidOperationException"/>
    /// (notably "Only one write can be pending at a time", which would mean two writers got hold of
    /// this stream) still propagates and is still a fault.</para>
    /// </summary>
    /// <param name="reader">The connection's outbound channel.</param>
    /// <param name="responseStream">The gRPC response stream.</param>
    /// <param name="callCancelled">The call's cancellation token — cancelled when the client goes away.</param>
    /// <param name="logger">Optional logger for the benign teardown lines.</param>
    private static async Task WritePumpAsync(
        ChannelReader<ServerFrame> reader,
        IServerStreamWriter<ServerFrame> responseStream,
        CancellationToken callCancelled,
        ILogger? logger)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(callCancelled))
                await responseStream.WriteAsync(frame);
        }
        catch (OperationCanceledException) when (callCancelled.IsCancellationRequested)
        {
            // The call ended while the pump was waiting — normal teardown, nothing to write to.
            logger?.LogDebug(
                "gRPC write pump stopped: the call was cancelled (the client disconnected).");
        }
        catch (InvalidOperationException ex) when (IsWriteAfterCallEnded(ex, callCancelled))
        {
            logger?.LogDebug(ex,
                "gRPC write pump raced the end of the call: the request was already complete when the "
                + "next frame was written, so the frame had nowhere to go. The client is gone; the "
                + "connection is torn down on the Disconnect path either way.");
        }
    }

    /// <summary>
    /// True when a failed write means <b>the call is already over</b> rather than that something is
    /// wrong with this server.
    ///
    /// <para>The token carries the decision wherever it can: a client that disconnects aborts the
    /// request, which is what cancels it. The message check covers the one case the token cannot —
    /// the call was completed by the SERVER (an <see cref="RpcException"/> thrown out of
    /// <c>Connect</c> finalises the response while frames are still queued) — and it is deliberately
    /// narrow: gRPC raises this sentence from <c>HttpContextStreamWriter.WriteCoreAsync</c> and
    /// nowhere else, so anything that stops matching it becomes a propagated fault, which is the
    /// safe direction to fail.</para>
    /// </summary>
    private static bool IsWriteAfterCallEnded(InvalidOperationException ex, CancellationToken callCancelled) =>
        callCancelled.IsCancellationRequested
        || ex.Message.Contains("request is complete", StringComparison.OrdinalIgnoreCase);

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
