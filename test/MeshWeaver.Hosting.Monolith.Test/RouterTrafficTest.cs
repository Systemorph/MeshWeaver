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
/// The ROOT MESH HUB is the mesh's ROUTER — it must never be an <b>end</b> of a delivery, only a hop
/// on the way to one. <c>SignalRConnectionRegistry</c> broke that: it issued its per-connection
/// <c>ValidateTokenRequest</c> on the DI-injected <c>IMessageHub</c>, which IS the root mesh hub, so
/// the request reached the <c>ApiToken/{hashPrefix}</c> hub stamped <c>Sender = mesh/{id}</c> and its
/// response was addressed straight back at the router. <c>GrpcConnectionRegistry</c> had already been
/// fixed (its <c>TransportHub</c>); SignalR had not. In production this was part of the ~2,385
/// <c>ROUTER_TRAFFIC</c> ERROR lines per 24 h across the three portals — the single largest source of
/// red log lines.
///
/// <para>🚨 The assertion is deliberately on the <b>sender</b> role only. "The mesh hub as SENDER" is
/// unambiguous — the router actually ORIGINATED that message, which it may never do. "The mesh hub as
/// TARGET" is reported by <c>MessageHub.DeliverMessage</c> for the RECEIVING hub, so it also fires for
/// a pure routing HOP: <c>HierarchicalRouting.RouteAlongHostingHierarchy</c> (line 218) hands every
/// delivery a hosted hub cannot route locally to <c>parentHub.DeliverMessage</c>, and for every hub in
/// the process that parent is the mesh hub. Those hops are the DESIGN — they are what gives
/// <c>RoutingServiceBase.RouteInMesh</c> its single-threaded arrival-order guarantee for per-address
/// activation — so asserting on them would pin an invariant the architecture does not hold.</para>
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
    /// 🚨 The other half of the same defect, and the one that produced the VOLUME.
    /// <c>MeshExtensions.NodeOperationTarget</c> fell back to <c>hub.GetMeshHub().Address</c>, so a
    /// create from any hub that declared no execution target of its own — every per-node hub — both
    /// landed on and EXECUTED on the router, and every message that work then sent went out stamped
    /// <c>Sender = mesh/{id}</c>. Because the sender role is reported by the RECEIVING hub, the line
    /// count scaled with the number of node hubs rather than the number of message types: production
    /// 2026-08 logged <c>"RawJson has the mesh hub as sender (sender: mesh/…, target:
    /// …/Source/FNodeTypeAtomicSolution)"</c> once per node hub. Node CRUD now runs on
    /// <c>portal/nodeops-{meshId}</c>.
    ///
    /// <para><see cref="NodeOperationOriginTest"/> pins the same change deterministically (which hub
    /// answered); this one measures it in the terms production reports it.</para>
    /// </summary>
    [Fact]
    public async Task NodeCreate_IsNeverIssuedByTheRouter()
    {
        var path = $"{TestPartition}/RouterTraffic-{Guid.NewGuid():N}";

        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                Name = "Router traffic probe",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
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
