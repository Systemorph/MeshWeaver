using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Grpc.Protocol;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Grpc.Test;

/// <summary>
/// The missing end-to-end coverage that let "python Code nodes don't run" ship: a REAL gRPC gate
/// participant connected at <c>py/python-kernel</c> (the address <see cref="Graph.Configuration.CodeNodeType.ResolveKernelAddress"/>
/// targets for <c>python</c>), a real python Code node executed via <see cref="ExecuteScriptRequest"/>,
/// and the assertion that the gate actually RECEIVES the <see cref="SubmitCodeRequest"/> over the wire.
///
/// <para>This is CO-HOSTED (one process: mesh + gRPC transport + gate proxy hub), the same topology as
/// the portal pod, so it exercises the full dispatch path — <c>HandleExecuteScript</c> → presence check
/// (the gate is connected, so it must NOT fail-fast) → post to <c>py/python-kernel</c> → the participant's
/// hosted proxy hub forwards it to the connected gate. Prior tests covered the language→address mapping
/// and an in-process fake worker, but never a real connected gate receiving the submit.</para>
/// </summary>
public class PythonCodeNodeGateDeliveryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "rbuergi";

    // Graph (CodeNodeType + the ExecuteScriptRequest handler) + the gRPC transport (py/node stream
    // routing, GrpcConnectionRegistry, IParticipantPresence). ConfigureMeshBase already adds Graph.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddGrpcHub();

    [Fact(Timeout = 120000)]
    public async Task PythonCodeNode_run_delivers_the_SubmitCodeRequest_to_the_connected_gate()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Mesh.ServiceProvider.GetRequiredService<GrpcConnectionRegistry>();
        var service = new MeshGrpcService(Mesh, registry);

        // 1) A gate connects at EXACTLY py/python-kernel (what ResolveKernelAddress("python") targets).
        var gate = new Address(GrpcHostingExtensions.PythonAddressType, "python-kernel");
        var inbound = Channel.CreateUnbounded<ClientFrame>();
        var writer = new CapturingStreamWriter<ServerFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var open = service.Open(new ChannelStreamReader<ClientFrame>(inbound.Reader), writer,
            new FakeServerCallContext(new Metadata(), cts.Token));
        await inbound.Writer.WriteAsync(new ClientFrame
        {
            Connect = JsonSerializer.Serialize(gate, Mesh.JsonSerializerOptions)
        }, ct);
        var ack = await NextFrame(writer, "connect ack", TimeSpan.FromSeconds(10));
        Assert.Equal(ServerFrame.KindOneofCase.Ack, ack.KindCase);
        Assert.Equal(gate.ToString(), ack.Ack.Address);

        // 2) A real, executable python Code node.
        var id = $"py-cell-{Guid.NewGuid():N}";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        const string code = "print('gate-delivery-marker')\n1 + 1";
        await meshService.CreateNode(new MeshNode(id, Partition)
        {
            Name = "python cell", NodeType = "Code",
            Content = new CodeConfiguration { Language = "python", IsExecutable = true, Code = code }
        }).FirstAsync().ToTask(ct);

        // 3) Run it. HandleExecuteScript sees the gate as CONNECTED (presence), so it does NOT fail
        //    fast — it posts the SubmitCodeRequest to py/python-kernel, which the gate must receive.
        var exec = await Mesh.Observe<ExecuteScriptResponse>(
                new ExecuteScriptRequest(), o => o.WithTarget(new Address($"{Partition}/{id}")))
            .Take(1).ToTask(ct);
        Assert.True(exec.Message.Success, exec.Message.Error ?? "the run should start");

        // 4) The gate RECEIVES the submit over the wire — the assertion nothing exercised before.
        string? submit = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (submit is null && DateTime.UtcNow < deadline)
        {
            ServerFrame f;
            try { f = await NextFrame(writer, "SubmitCodeRequest", deadline - DateTime.UtcNow); }
            catch (TimeoutException) { break; }
            if (f.KindCase == ServerFrame.KindOneofCase.Receive
                && f.Receive.Contains(nameof(SubmitCodeRequest)))
                submit = f.Receive;
        }

        Output.WriteLine($"gate received:\n{submit}");
        Assert.NotNull(submit);                        // the connected gate got the submit
        Assert.Contains("gate-delivery-marker", submit!); // …carrying the node's code (marker survives JSON escaping)
        Assert.Contains("python", submit!);            // …tagged with the language
        Assert.Contains(nameof(SubmitCodeRequest), submit!);

        inbound.Writer.Complete();
        await open;
    }

    // ── gRPC in-memory transport helpers (mirror MeshGrpcTransportTest) ──────────────────────

    private static async Task<ServerFrame> NextFrame(
        CapturingStreamWriter<ServerFrame> writer, string step, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
        try { return await writer.Output.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { throw new TimeoutException($"no '{step}' frame within {timeout}"); }
    }

    private sealed class ChannelStreamReader<T>(ChannelReader<T> reader) : IAsyncStreamReader<T>
    {
        public T Current { get; private set; } = default!;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            try { Current = await reader.ReadAsync(cancellationToken); return true; }
            catch (ChannelClosedException) { return false; }
            catch (OperationCanceledException) { return false; }
        }
    }

    private sealed class CapturingStreamWriter<T> : IServerStreamWriter<T>
    {
        private readonly Channel<T> channel = Channel.CreateUnbounded<T>();
        public ChannelReader<T> Output => channel.Reader;
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message) { channel.Writer.TryWrite(message); return Task.CompletedTask; }
    }

    private sealed class FakeServerCallContext(Metadata headers, CancellationToken ct)
        : ServerCallContext
    {
        protected override IDictionary<object, object> UserStateCore { get; } = new Dictionary<object, object>();
        protected override string MethodCore => MeshGrpcService.ServiceName + "/Open";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:0";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => headers;
        protected override CancellationToken CancellationTokenCore => ct;
        protected override Metadata ResponseTrailersCore { get; } = new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } = new(null, new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
