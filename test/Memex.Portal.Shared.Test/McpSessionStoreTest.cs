using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Mesh-backed McpSessionStore semantics — the replica-safety fix that lets a stateful MCP
/// session established on one silo be re-hydrated on another (the MCP client sends no affinity
/// cookie, so a follow-up request for an established Mcp-Session-Id can land on a replica that
/// never served the initialize → 404 "Session not found").
///
/// <para>
/// <see cref="TwoStoreInstances_StoreOnOne_ReadOnOther"/> and
/// <see cref="Handler_StoreOnOne_MigrateOnOther_RoundTrips"/> pin exactly the multi-silo shape:
/// two independent store / handler instances share one mesh the way two replicas share one PG,
/// and B must serve a session A created — mirroring <c>OAuthCodeStoreTest</c>'s
/// <c>TwoStoreInstances_GenerateOnOne_ExchangeOnOther</c>.
/// </para>
/// </summary>
public class McpSessionStoreTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string InitJson =
        """{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"claude","version":"1.0"}}""";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // Same registration the portal wires in ConfigureMemexMesh.
        => base.ConfigureMesh(builder).AddMcpSessionType();

    /// <summary>Each call builds an independent store on the SAME mesh — two KEDA replicas
    /// against the shared PG-backed mesh.</summary>
    private McpSessionStore NewStore(TimeSpan? sessionLifetime = null) => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh)
    {
        SessionLifetime = sessionLifetime ?? TimeSpan.FromDays(1),
        // Short read window so the unknown-session negative path (waits out the timeout by
        // design) stays fast; a stored session resolves near-instantly via Take(1). Prod = 10 s.
        ReadTimeout = TimeSpan.FromSeconds(2),
    };

    private static string NewSessionId() => "sess-" + Guid.NewGuid().ToString("N");

    private McpSessionMigrationHandler NewHandler(McpSessionStore store)
        => new(store, NullLogger<McpSessionMigrationHandler>.Instance);

    private static HttpContext ContextFor(string oid) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", oid)], "Test")),
    };

    [Fact]
    public async Task StoreThenRead_RoundTrips()
    {
        var store = NewStore();
        var sessionId = NewSessionId();

        (await store.StoreSession(sessionId, "user-1", InitJson).Should().Within(30.Seconds()).Emit())
            .Should().BeTrue();

        var entry = await store.ReadSession(sessionId).Should().Within(30.Seconds()).Emit();
        entry.Should().NotBeNull();
        entry!.Owner.Should().Be("user-1");
        entry.InitializeParamsJson.Should().Be(InitJson);
    }

    [Fact]
    public async Task ReadSession_UnknownId_ReturnsNull()
    {
        var store = NewStore();

        var entry = await store.ReadSession(NewSessionId()).Should().Within(30.Seconds()).Emit();

        entry.Should().BeNull();
    }

    [Fact]
    public async Task ReadSession_Expired_ReturnsNull()
    {
        // Zero lifetime → the stored session is already expired when read (deterministic).
        var store = NewStore(sessionLifetime: TimeSpan.Zero);
        var sessionId = NewSessionId();
        await store.StoreSession(sessionId, "user-1", InitJson).Should().Within(30.Seconds()).Emit();

        var entry = await store.ReadSession(sessionId).Should().Within(30.Seconds()).Emit();

        entry.Should().BeNull();
    }

    [Fact]
    public async Task TwoStoreInstances_StoreOnOne_ReadOnOther()
    {
        // The multi-silo shape: initialize on replica A, follow-up request on replica B.
        // Two independent stores share the mesh the way two silos share PG — B must read
        // a session A stored.
        var replicaA = NewStore();
        var replicaB = NewStore();
        var sessionId = NewSessionId();

        await replicaA.StoreSession(sessionId, "user-1", InitJson).Should().Within(30.Seconds()).Emit();

        var onB = await replicaB.ReadSession(sessionId).Should().Within(30.Seconds()).Emit();
        onB.Should().NotBeNull();
        onB!.Owner.Should().Be("user-1");
        onB.InitializeParamsJson.Should().Be(InitJson);
    }

    [Fact]
    public async Task Handler_StoreOnOne_MigrateOnOther_RoundTrips()
    {
        // End-to-end through the ISessionMigrationHandler the MCP SDK resolves from DI:
        // OnSessionInitialized on replica A, AllowSessionMigration on replica B.
        var handlerA = NewHandler(NewStore());
        var handlerB = NewHandler(NewStore());
        var sessionId = NewSessionId();
        var init = JsonSerializer.Deserialize<InitializeRequestParams>(InitJson, McpJsonUtilities.DefaultOptions)!;

        await handlerA.OnSessionInitializedAsync(ContextFor("user-1"), sessionId, init, CancellationToken.None);

        var migrated = await handlerB.AllowSessionMigrationAsync(ContextFor("user-1"), sessionId, CancellationToken.None);
        migrated.Should().NotBeNull();
        migrated!.ProtocolVersion.Should().Be("2024-11-05");
        migrated.ClientInfo!.Name.Should().Be("claude");
    }

    [Fact]
    public async Task Handler_RejectsMigration_ForDifferentOwner()
    {
        var handlerA = NewHandler(NewStore());
        var handlerB = NewHandler(NewStore());
        var sessionId = NewSessionId();
        var init = JsonSerializer.Deserialize<InitializeRequestParams>(InitJson, McpJsonUtilities.DefaultOptions)!;

        await handlerA.OnSessionInitializedAsync(ContextFor("user-1"), sessionId, init, CancellationToken.None);

        // A different authenticated caller must not be able to re-bind someone else's session.
        var stolen = await handlerB.AllowSessionMigrationAsync(ContextFor("attacker"), sessionId, CancellationToken.None);
        stolen.Should().BeNull();
    }

    [Fact]
    public async Task Handler_UnknownSession_ReturnsNull()
    {
        var handler = NewHandler(NewStore());

        var migrated = await handler.AllowSessionMigrationAsync(ContextFor("user-1"), NewSessionId(), CancellationToken.None);

        migrated.Should().BeNull();
    }
}
