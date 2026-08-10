using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Hosting.SignalR;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the two ways the ROOT MESH HUB — the mesh's ROUTER — ends up as an <b>end</b> of a delivery
/// rather than a hop on the way to one. Both showed up in production as <c>ROUTER_TRAFFIC</c> ERROR
/// lines (~2,385/24h across the three portals, the single largest source of red log lines):
///
/// <list type="number">
///   <item><b>Node CRUD.</b> <c>MeshExtensions.NodeOperationTarget</c> used to fall back to
///     <c>hub.GetMeshHub().Address</c> whenever the issuing hub had no
///     <c>WithNodeOperationExecution</c> ancestor — which is EVERY per-node hub (a thread hub
///     creating its <c>_Notification</c> satellite, a NodeType <c>Source</c> hub writing a compile
///     activity). The create then executed on the router's single-threaded action block and its
///     response came back stamped <c>Sender = mesh/{id}</c>:
///     <c>"RawJson has the mesh hub as sender (sender: mesh/…, target: …/Source/FNodeTypeAtomicSolution)"</c>.</item>
///   <item><b>SignalR token validation.</b> <c>SignalRConnectionRegistry</c> issued its
///     <c>ValidateTokenRequest</c> on the DI-injected <c>IMessageHub</c> — which is the root mesh hub
///     — so the request reached the <c>ApiToken/{hashPrefix}</c> hub stamped <c>Sender = mesh/{id}</c>
///     and its response was addressed straight back at the router. <c>GrpcConnectionRegistry</c> had
///     already been fixed (its <c>TransportHub</c>); SignalR had not.</item>
/// </list>
///
/// <para>🚨 The assertion is deliberately on the <b>sender</b> role only. "The mesh hub as SENDER" is
/// unambiguous — the router actually originated that message, which it may never do. "The mesh hub as
/// TARGET" is reported by <c>MessageHub.DeliverMessage</c> for the RECEIVING hub, so it also fires for
/// a pure routing HOP: <c>HierarchicalRouting.RouteAlongHostingHierarchy</c> hands every delivery a
/// hosted hub cannot route locally to <c>parentHub.DeliverMessage</c>, and for every hub in the
/// process that parent is the mesh hub. Those hops are the design (they are what gives
/// <c>RoutingServiceBase.RouteInMesh</c> its single-threaded arrival-order guarantee for per-address
/// activation) and are NOT what this test pins.</para>
/// </summary>
public class RouterTrafficTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly ConcurrentQueue<string> _routerTraffic = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(s =>
            {
                // SignalRConnectionRegistry needs IHubContext<SignalRConnectionHub>; AddSignalR
                // registers it (and nothing here needs a running web host — Authenticate never
                // touches the hub context, only PushToClient does).
                s.AddSignalR();
                return s.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(_routerTraffic));
            });

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A per-node hub issuing node CRUD must NOT target the router — and the response must therefore
    /// never reach it stamped with the mesh hub as sender.
    /// </summary>
    [Fact]
    public async Task NodeCrud_FromAPerNodeHub_NeverRunsOnTheRouter()
    {
        var nodeHub = Mesh.GetHostedHub(new Address(TestPartition), c => c, HostedHubCreation.Always)!;

        var target = nodeHub.NodeOperationTarget();
        Output.WriteLine($"NodeOperationTarget for {nodeHub.Address} = {target}");
        Assert.False(
            string.Equals(target.Type, AddressExtensions.MeshType, StringComparison.Ordinal),
            $"Node CRUD from the per-node hub '{nodeHub.Address}' targets the ROUTER ({target}). "
            + "The mesh hub routes; it must not execute work. NodeOperationTarget must fall back to "
            + "the mesh's dedicated off-router execution hub.");

        await nodeHub.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode($"child-{Guid.NewGuid():N}", TestPartition) { NodeType = "Markdown" })
            .Timeout(30.Seconds()).FirstAsync().ToTask(Ct);

        AssertRouterNeverSent();
    }

    /// <summary>
    /// SignalR's per-connection token validation must be issued on the transport's own portal hub,
    /// never on the root mesh hub.
    /// </summary>
    [Fact]
    public async Task SignalRTokenValidation_IsNeverIssuedByTheRouter()
    {
        var rawToken = await SeedApiToken();

        using var registry = new SignalRConnectionRegistry(
            Mesh,
            RoutingService,
            Mesh.ServiceProvider.GetRequiredService<IHubContext<SignalRConnectionHub>>());

        await registry.Authenticate("test-connection", rawToken)
            .Timeout(30.Seconds()).FirstAsync().ToTask(Ct);

        AssertRouterNeverSent();
    }

    /// <summary>
    /// Creates an ApiToken node for the DevLogin admin and returns the raw <c>mw_…</c> token.
    /// </summary>
    private async Task<string> SeedApiToken()
    {
        var rawToken = ValidateTokenRequest.TokenPrefix
            + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var hash = ValidateTokenRequest.HashToken(rawToken);

        await NodeFactory.CreateNode(new MeshNode(hash[..12], "ApiToken")
        {
            Name = "Router traffic test token",
            NodeType = "ApiToken",
            // MainNode = the owning user so the validation round-trip's self-access read is granted.
            MainNode = TestUsers.Admin.ObjectId,
            Content = new ApiToken
            {
                TokenHash = hash,
                UserId = TestUsers.Admin.ObjectId,
                UserName = TestUsers.Admin.Name,
                UserEmail = TestUsers.Admin.Email,
                Label = "Test",
                CreatedAt = DateTimeOffset.UtcNow,
                Roles = ["Editor"],
            }
        }).Timeout(30.Seconds()).FirstAsync().ToTask(Ct);

        return rawToken;
    }

    private void AssertRouterNeverSent()
    {
        var offending = _routerTraffic
            .Where(l => l.Contains("as sender", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        foreach (var line in _routerTraffic.Distinct())
            Output.WriteLine(line);
        Assert.True(offending.Count == 0,
            "The mesh hub is the ROUTER and must never ORIGINATE a delivery — work belongs on a hub "
            + "of its own. Offending ROUTER_TRAFFIC lines:\n" + string.Join("\n", offending));
    }

    /// <summary>Captures the <c>ROUTER_TRAFFIC</c> ERROR lines <c>MessageHub</c> emits.</summary>
    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error) return;
            var msg = formatter(state, exception);
            if (msg.Contains("ROUTER_TRAFFIC", StringComparison.Ordinal))
                sink.Enqueue(msg);
        }
    }
}
