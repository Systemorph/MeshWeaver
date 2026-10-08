using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>MeshWeaver#6026</b>: <see cref="ApiTokenService.RevokeToken"/> wrote the revocation TWICE —
/// once through <c>GetMeshNodeStream(path).Update</c>, then again as a raw whole-node
/// <c>SaveMeshNodeRequest</c> posted from the hub the service was handed. In production that hub is
/// the ROOT MESH HUB, so the router became the SENDER of a node write (<c>ROUTER_TRAFFIC ORIGIN</c>),
/// and the second write's outcome — including a refusal — had nowhere to go. The revoke also folded
/// every fault into <c>false</c>, so a REFUSED revocation was indistinguishable from "nothing to do"
/// and a rotation that ignores the value reported success while the old credential stayed live.
///
/// <para>The service is constructed on <c>Mesh</c> DELIBERATELY: the router as the caller IS the
/// production shape (the singleton is built from the root container).</para>
///
/// <para><b>SHOULD-FAIL-IF</b> the second write comes back: <see cref="RevokingFromTheRootMeshHub_NeverMakesTheRouterASender"/>
/// records a <c>sender</c> report. If the swallowing <c>.Catch</c> comes back:
/// <see cref="ARevokeByACallerWithoutUpdate_IsRefused_AndTheTokenStillAuthenticates"/> sees
/// <c>OnNext(false)</c> instead of a fault. <see cref="TheCapture_RecordsAViolation_WhenTheRouterGenuinelyPostsWork"/>
/// is the positive control: without it "no report" and "the capture was never wired" read the same.</para>
/// </summary>
public class TokenRevocationIsIssuedOffTheRouterTest : MonolithMeshTestBase
{
    private const string OwnerId = "revoke-router-owner";

    /// <summary>The control's probe: a message with no meaning beyond being WORK the router posts.</summary>
    private record RevokeRouterProbe;

    private readonly RouterTrafficCapture _capture = new();

    /// <summary>Producer → test: the client hub completes this when the probe reaches its handler.</summary>
    private readonly AsyncSubject<Unit> _probeArrived = new();

    public TokenRevocationIsIssuedOffTheRouterTest(ITestOutputHelper output) : base(output)
    {
        // After the base constructor's ClearProviders(), so it survives beside the xUnit sink.
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(_capture));
    }

    /// <summary>
    /// 🚨 <c>ConfigureMeshBase</c>, NOT <c>base.ConfigureMesh</c>: the latter grants <c>Public</c> the
    /// Admin role at root, under which every caller holds <c>All</c> everywhere and no refusal can be
    /// observed. Here only the harness admin holds a grant.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).ConfigureHub(c => c.WithTypes(typeof(RevokeRouterProbe)));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(RevokeRouterProbe))
            .WithHandler<RevokeRouterProbe>((_, delivery) =>
            {
                _probeArrived.OnNext(Unit.Default);
                _probeArrived.OnCompleted();
                return delivery.Processed();
            });

    private ApiTokenService GetService() =>
        new(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>(),
            Mesh.ServiceProvider.GetRequiredService<ILogger<ApiTokenService>>());

    /// <summary>
    /// 🚨 THE MEASUREMENT, and the durability proof that makes deleting the second write safe: the
    /// revocation is read back from the STORAGE ADAPTER — no stream mirror, no cache — so a revoke
    /// that only reached the in-memory node would fail here.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RevokingFromTheRootMeshHub_NeverMakesTheRouterASender()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = GetService();
        var created = await service.CreateToken(OwnerId, "Revoke Owner", "revoke-owner@example.com", "Router")
            .Should().Emit("the token must exist, or the revoke measures nothing", ct);
        var reportsBefore = Reports().Length;

        var revoked = await service.RevokeToken(created.Node.Path)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the revoke must complete — an exchange that never happened emits no traffic", ct);
        revoked.Should().BeTrue();

        var persisted = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(created.Node.Path, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n?.ContentAs<ApiToken>(Mesh.JsonSerializerOptions)?.IsRevoked == true,
                "the revocation must be DURABLE through the one update alone — that is the "
                + "property the deleted SaveMeshNodeRequest claimed to supply", ct);
        persisted.Should().NotBeNull();

        var validation = await service.Validate(created.RawToken)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        validation.Status.Should().Be(TokenValidationStatus.Invalid, $"a revoked token must stop authenticating ({validation.Reason})");

        DumpReports();
        Reports().Skip(reportsBefore).Where(r => r.Role.Contains("sender", StringComparison.Ordinal))
            .Should().BeEmpty(
                "the revocation must reach the token's owning hub through the mesh's own write "
                + "path, never as a post stamped `sender: mesh/{id}` (MeshWeaver#6026)");
    }

    /// <summary>
    /// 🚨 THE UNAUTHORISED PATH. A caller with no grant on the token's partition revokes it: the mesh
    /// must refuse, the refusal must reach the caller as a FAULT, and the token must still be valid
    /// afterwards. The identity is checked first so the test cannot pass by asserting against the
    /// wrong user.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARevokeByACallerWithoutUpdate_IsRefused_AndTheTokenStillAuthenticates()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = GetService();
        var created = await service.CreateToken(OwnerId, "Revoke Owner", "revoke-owner@example.com", "Refused")
            .Should().Emit(cancellationToken: ct);

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var stranger = new AccessContext { ObjectId = "revoke-stranger", Name = "Revoke Stranger" };
        var strangerPerms = await Mesh.GetEffectivePermissions(created.Node.Path, stranger.ObjectId)
            .Should().Emit(cancellationToken: ct);
        strangerPerms.HasFlag(Permission.Update).Should().BeFalse(
            "the control must be a caller who genuinely lacks Update on the token, or a refusal "
            + "cannot be expected");

        Notification<bool> outcome;
        try
        {
            access.SetContext(stranger);
            access.SetHostIdentity(stranger);
            outcome = await service.RevokeToken(created.Node.Path).Materialize()
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the revoke must answer — silence would be its own defect", ct);
        }
        finally
        {
            access.SetContext(TestUsers.Admin);
            access.SetHostIdentity(TestUsers.Admin);
        }

        Output.WriteLine($"stranger revoke outcome: {outcome.Kind} {outcome.Exception?.Message}");
        outcome.Kind.Should().Be(NotificationKind.OnError,
            "a REFUSED revocation is a statement about a token that still authenticates — folding it "
            + "into `false` is how a rotation reported success with the old credential live");
        (outcome.Exception?.Message ?? "(no exception)").Should().Contain("Access denied",
            "the fault must be the mesh's permission refusal, not some unrelated failure");

        var persisted = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(created.Node.Path, Mesh.JsonSerializerOptions)
            .Should().Emit(cancellationToken: ct);
        (persisted?.ContentAs<ApiToken>(Mesh.JsonSerializerOptions)?.IsRevoked).Should().Be((bool?)false,
            "the refused caller must not have revoked the token");

        var validation = await service.Validate(created.RawToken)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        validation.Status.Should().Be(TokenValidationStatus.Valid, $"a refused revoke leaves the owner's token working ({validation.Reason})");
    }

    /// <summary>
    /// The other half of the refusal test, so the pair cannot both be satisfied by "always fault":
    /// a path that holds no node is not a refusal — nothing there authenticates — and answers
    /// <c>false</c>, as <see cref="ApiTokenService.DeleteToken"/> does for the same case.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RevokingAPathThatHoldsNoToken_AnswersFalse_NotAFault()
    {
        var outcome = await GetService().RevokeToken($"{OwnerId}/ApiToken/never-minted").Materialize()
            .Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);

        Output.WriteLine($"absent revoke outcome: {outcome.Kind} {outcome.Exception?.Message}");
        outcome.Kind.Should().Be(NotificationKind.OnNext);
        outcome.Value.Should().BeFalse("nothing was there, so nothing was revoked");
    }

    /// <summary>
    /// A node at the path whose content is NOT an <see cref="ApiToken"/> must fault the revoke and be
    /// left as it was — never read leniently into a default token and overwritten, and never
    /// reported as revoked.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RevokingANodeThatIsNotAToken_Faults_AndLeavesItUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{OwnerId}/ApiToken/not-a-token";
        await NodeFactory.CreateNode(new MeshNode("not-a-token", $"{OwnerId}/ApiToken")
            {
                Name = "Not A Token",
                NodeType = ApiTokenNodeType.NodeType,
                Content = new ApiTokenIndex { TokenHash = "unrelated", TokenPath = "elsewhere" },
            })
            .Should().Emit("the decoy must exist, or the revoke would take the absent-path branch", ct);

        var outcome = await GetService().RevokeToken(path).Materialize()
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        Output.WriteLine($"non-token revoke outcome: {outcome.Kind} {outcome.Exception?.Message}");
        outcome.Kind.Should().Be(NotificationKind.OnError,
            "content that is not a token cannot be revoked, and saying it was would be a lie");

        var persisted = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Should().Emit(cancellationToken: ct);
        (persisted?.ContentAs<ApiTokenIndex>(Mesh.JsonSerializerOptions)?.TokenHash).Should().Be("unrelated",
            "the node must not have been overwritten with a default-constructed token");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL: make the router a sender on purpose, so "no report" above means the
    /// seam held rather than the capture never being wired into this mesh.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheCapture_RecordsAViolation_WhenTheRouterGenuinelyPostsWork()
    {
        var client = GetClient();

        Mesh.Post(new RevokeRouterProbe(), o => o.WithTarget(client.Address));

        await _probeArrived.Should().Within(TestTimeouts.Convergence)
            .Emit("the probe must reach the client hub, or this control proves nothing",
                cancellationToken: TestContext.Current.CancellationToken);

        DumpReports();
        Reports().Should().Contain(r => r.Role == "sender" && r.Sender == Mesh.Address.ToString(),
            "the capture must be able to record a genuine router-sent post");
    }

    private RouterTrafficRecord[] Reports() => _capture.Records;

    private void DumpReports()
    {
        foreach (var record in _capture.Records)
            Output.WriteLine($"ROUTER_TRAFFIC captured: {record}");
    }

    private sealed record RouterTrafficRecord(string MessageType, string Role, string Sender, string Target);

    /// <summary>Reads the detector's own ERROR (both the ORIGIN and the delivery form) out of the
    /// logging pipeline as structured state.</summary>
    private sealed class RouterTrafficCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<RouterTrafficRecord> _records = new();

        internal RouterTrafficRecord[] Records => _records.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_records);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<RouterTrafficRecord> sink) : ILogger
        {
            private sealed class NullScope : IDisposable
            {
                internal static readonly NullScope Instance = new();
                public void Dispose() { }
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error || state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                    return;
                if (!formatter(state, exception).StartsWith("ROUTER_TRAFFIC", StringComparison.Ordinal))
                    return;

                sink.Enqueue(new RouterTrafficRecord(
                    Value(values, "MessageType"),
                    Value(values, "Role"),
                    Value(values, "Sender"),
                    Value(values, "Target")));
            }

            private static string Value(IReadOnlyList<KeyValuePair<string, object?>> values, string key)
            {
                foreach (var pair in values)
                    if (pair.Key == key)
                        return pair.Value?.ToString() ?? "(null)";
                return "(absent)";
            }
        }
    }
}
