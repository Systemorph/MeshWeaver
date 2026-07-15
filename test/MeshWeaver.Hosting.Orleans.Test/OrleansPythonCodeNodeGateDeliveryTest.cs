using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using MeshWeaver.Hosting.Grpc;
using MeshWeaver.Hosting.Grpc.Protocol;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>The distributed (Orleans) sibling of <c>PythonCodeNodeGateDeliveryTest</c>: the silo mesh
/// adds the gRPC transport (<see cref="GrpcHostingExtensions.AddGrpcHub(MeshBuilder)"/>) so <c>py</c> is
/// stream-routed there.</summary>
public sealed class GrpcSiloConfigurator : TestSiloConfigurator
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => builder.AddGrpcHub();
}

/// <summary>
/// Orleans-cohosted proof that a <c>python</c> Code-node run delivers its <see cref="SubmitCodeRequest"/>
/// to a connected <c>py/python-kernel</c> gate — the exact memex portal topology (Orleans silo + gRPC
/// transport + gate, one process). The gate connects to the SILO's <see cref="MeshGrpcService"/> (so the
/// silo's <c>GrpcConnectionRegistry</c> sees it — the presence check must NOT fail fast), the Code node's
/// <c>HandleExecuteScript</c> runs on the silo grain, and the submit routes to <c>py/python-kernel</c>
/// through the RoutingGrain → Orleans memory stream → the gate's <c>RegisterStream</c> subscription →
/// proxy hub → gate. Covers the Orleans routing hop the Monolith test can't.
/// </summary>
public class OrleansPythonCodeNodeGateDeliveryTest(ITestOutputHelper output)
    : OrleansTestBase<GrpcSiloConfigurator>(output)
{
    private const string Partition = "TestUser";

    [Fact(Timeout = 120000)]
    public async Task PythonCodeNode_run_delivers_the_SubmitCodeRequest_to_the_gate_connected_to_the_silo()
    {
        var ct = TestContext.Current.CancellationToken;
        var siloServices = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services;
        var siloMesh = siloServices.GetRequiredService<IMessageHub>();
        var registry = siloServices.GetRequiredService<GrpcConnectionRegistry>();
        var service = new MeshGrpcService(siloMesh, registry);

        // 1) A gate connects (to the silo) at EXACTLY py/python-kernel.
        var gate = new Address(GrpcHostingExtensions.PythonAddressType, "python-kernel");
        var inbound = Channel.CreateUnbounded<ClientFrame>();
        var writer = new CapturingStreamWriter<ServerFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var open = service.Open(new ChannelStreamReader<ClientFrame>(inbound.Reader), writer,
            new FakeServerCallContext(new Metadata(), cts.Token));
        await inbound.Writer.WriteAsync(new ClientFrame
        {
            Connect = JsonSerializer.Serialize(gate, siloMesh.JsonSerializerOptions)
        }, ct);
        var ack = await NextFrame(writer, "connect ack", TimeSpan.FromSeconds(15));
        Assert.Equal(ServerFrame.KindOneofCase.Ack, ack.KindCase);

        // 2) A real, executable python Code node.
        var id = $"py-cell-{Guid.NewGuid():N}";
        var meshService = siloServices.GetRequiredService<IMeshService>();
        const string code = "print('gate-delivery-marker')\n1 + 1";
        await meshService.CreateNode(new MeshNode(id, Partition)
        {
            Name = "python cell", NodeType = "Code",
            Content = new CodeConfiguration { Language = "python", IsExecutable = true, Code = code }
        }).FirstAsync().ToTask(ct);

        // 3) Run it on the silo grain. The gate is CONNECTED (presence), so HandleExecuteScript does
        //    not fail fast — it posts the SubmitCodeRequest to py/python-kernel.
        var exec = await siloMesh.Observe<ExecuteScriptResponse>(
                new ExecuteScriptRequest(), o => o.WithTarget(new Address($"{Partition}/{id}")))
            .Take(1).ToTask(ct);
        Assert.True(exec.Message.Success, exec.Message.Error ?? "the run should start");

        // 4) The gate RECEIVES the submit over the wire — through the Orleans routing hop.
        string? submit = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
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
        Assert.NotNull(submit);
        Assert.Contains("gate-delivery-marker", submit!);
        Assert.Contains("python", submit!);

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
