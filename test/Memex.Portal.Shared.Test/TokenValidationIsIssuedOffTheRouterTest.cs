using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The second call site of
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/1140">#1140</see></b>, and the one
/// this repo owns: token validation on the HTTP request path.
///
/// <para><c>UserContextMiddleware</c> resolves its hub as
/// <c>RequestServices.GetService&lt;PortalApplication&gt;()?.Hub ?? RequestServices.GetRequiredService&lt;IMessageHub&gt;()</c>.
/// The first is the portal hub and is fine. The FALLBACK resolves the ROOT MESH HUB in the root
/// container — which is what a portal running without the Blazor shell
/// (<c>Features:Gui:Blazor=false</c>) gets, and what any request outside a circuit scope gets. A
/// <c>ValidateTokenRequest</c> posted there reaches the ApiToken node's hub stamped
/// <c>Sender = mesh/{id}</c> and its response is addressed straight back at the router.
/// <c>ValidateTokenRequest/Response</c> is named among the observed types in #1140's own evidence
/// from <c>memex</c>.</para>
///
/// <para><b>Why <c>ReadIssuingHub</c> and not <c>NodeOperationIssuingHub</c>.</b> This is a bounded
/// READ with an HTTP request waiting on it. <c>portal/reads-{meshId}</c> registers no handlers, so
/// its block only ever dispatches replies to reads issued on it; the node-CRUD execution hub would
/// put this reply behind every write in flight — #2901's ~10.3 s-then-503 shape. Both seams are the
/// identity function for any hub that is not the router, so the portal-hub path is unchanged.</para>
///
/// <para><b>The positive control is not optional</b> — see
/// <see cref="TheCapture_RecordsAViolation_WhenTheRouterGenuinelyPostsWork"/>. Without it, "no
/// ROUTER_TRAFFIC" and "the capture was never wired into this mesh" are the same reading.</para>
/// </summary>
public class TokenValidationIsIssuedOffTheRouterTest : MonolithMeshTestBase
{
    private const string TokenUserId = "TokenRouterUser";

    /// <summary>The control's probe: a message with no meaning beyond being WORK the router posts.</summary>
    private record RouterOriginProbe;

    private readonly RouterTrafficCapture _capture = new();

    /// <summary>Producer → test: the client hub completes this when the probe reaches its handler.</summary>
    private readonly AsyncSubject<Unit> _probeArrived = new();

    public TokenValidationIsIssuedOffTheRouterTest(ITestOutputHelper output) : base(output)
    {
        // Registered AFTER the base constructor's ClearProviders(), so it survives alongside the
        // xUnit sink. The detector's whole contract is the ERROR it emits; reading that record is
        // the only way to assert on it without re-implementing the decision here.
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(_capture));
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureHub(c => c
                .WithTypes(typeof(RouterOriginProbe))
                .WithType(typeof(ValidateTokenRequest), nameof(ValidateTokenRequest))
                .WithType(typeof(ValidateTokenResponse), nameof(ValidateTokenResponse)));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(RouterOriginProbe))
            .WithHandler<RouterOriginProbe>((_, delivery) =>
            {
                _probeArrived.OnNext(Unit.Default);
                _probeArrived.OnCompleted();
                return delivery.Processed();
            });

    /// <summary>
    /// 🚨 THE MEASUREMENT. The middleware's own entry point, handed the ROOT MESH HUB — exactly what
    /// its <c>GetRequiredService&lt;IMessageHub&gt;()</c> fallback produces — must leave the router
    /// off BOTH ends of the validation exchange.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TokenValidationIssuedFromTheRootMeshHub_NeverPutsTheRouterOnEitherEnd()
    {
        var rawToken = $"mw_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var hash = ValidateTokenRequest.HashToken(rawToken);
        var hashPrefix = hash[..12];

        // A real token node, created through the seam that is already off-router so nothing before
        // the subject under test can contribute a record.
        await NodeFactory.CreateNode(new MeshNode(hashPrefix, $"User/{TokenUserId}/_Api")
            {
                Name = "Router Origin Token",
                NodeType = ApiTokenNodeType.NodeType,
                MainNode = $"User/{TokenUserId}",
                Content = new ApiToken
                {
                    UserId = TokenUserId,
                    UserName = TokenUserId,
                    UserEmail = "token-router@meshweaver.io",
                    TokenHash = hash,
                    Label = "Router Origin Token",
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            })
            .Should().Emit("the token must exist, or the validation never reaches a receiving hub "
                + "and the assertion below would measure nothing");

        // The routing index the middleware's target address names — `ApiToken/{hashPrefix}`. In
        // production ApiTokenService writes it beside the token; nothing writes it automatically, so
        // the fixture does. Without it the validation faults with a DeliveryFailure and the
        // assertions below would pass having observed no exchange at all (they did, on the first
        // run of this test — which is why the Success assertion above is not decoration).
        await NodeFactory.CreateNode(new MeshNode(hashPrefix, ApiTokenNodeType.NodeType)
            {
                Name = "Router Origin Token Index",
                NodeType = ApiTokenNodeType.NodeType,
                Content = new ApiTokenIndex
                {
                    TokenHash = hash,
                    TokenPath = $"User/{TokenUserId}/_Api/{hashPrefix}",
                },
            })
            .Should().Emit("the validation routes to the INDEX at ApiToken/{hashPrefix} first");

        // 🚨 On `Mesh` DELIBERATELY: the ROUTER as the caller IS the subject, and it is the shape
        // the middleware's own fallback produces.
        var response = await UserContextMiddleware.ValidateTokenViaHub(rawToken, Mesh)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the validation must actually complete — an exchange that never happened emits no "
                + "traffic at all");

        response.Should().NotBeNull();
        response!.Success.Should().BeTrue(
            $"the token is real — a failed validation takes a different, shorter path and would "
            + $"leave the assertions below measuring nothing (error: {response.Error ?? "(none)"}, "
            + $"unavailable: {response.IsUnavailable})");
        response.UserId.Should().Be(TokenUserId);

        DumpReports();
        Reports().Where(r => r.Role.Contains("sender", StringComparison.Ordinal)).Should().BeEmpty(
            "the ValidateTokenRequest must reach the ApiToken node's hub from the off-router read "
            + "seam (MeshExtensions.ReadIssuingHub), never stamped `sender: mesh/{id}` — which is "
            + "one of the message types #1140's evidence from memex names");
        Reports().Where(r => r.Role.Contains("target", StringComparison.Ordinal)).Should().BeEmpty(
            "and the ValidateTokenResponse must not be addressed back at the router, which follows "
            + "from the request's sender");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. The measurement reads "no records" as "the seam held", which is only
    /// valid if this fixture's capture CAN record one. So make the router an end on purpose.
    ///
    /// <para>A plain <c>Post</c> at a client hub: the same "sender" role, with no token or node CRUD
    /// involved, so a failure here cannot be confused with a failure of the validation path. The
    /// arrival subject is the barrier rather than a sleep — the detector runs synchronously at the
    /// top of <c>DeliverMessage</c>, strictly before the delivery reaches the handler that completes
    /// it.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheCapture_RecordsAViolation_WhenTheRouterGenuinelyPostsWork()
    {
        var client = GetClient();

        Mesh.Post(new RouterOriginProbe(), o => o.WithTarget(client.Address));

        await _probeArrived.Should().Within(TestTimeouts.Convergence)
            .Emit("the probe must actually reach the client hub, or nothing was delivered and this "
                + "control proves nothing");

        DumpReports();
        // Asserted on ROLE and ENDS, never on the message type: the client hub is reached over a
        // serializing route, so the detector reads `RawJson` — precisely the type #1140's production
        // lines carry.
        var report = Reports().Should().ContainSingle(
            "the capture must be able to record a genuine violation, or the measurement's green is "
            + "an instrument that cannot fail rather than a seam that holds").Subject;
        report.Role.Should().Be("sender");
        report.Sender.Should().Be(Mesh.Address.ToString());
        report.Target.Should().Be(client.Address.ToString());
    }

    private RouterTrafficRecord[] Reports() => _capture.Records;

    private void DumpReports()
    {
        foreach (var record in _capture.Records)
            Output.WriteLine($"ROUTER_TRAFFIC captured: {record}");
    }

    private sealed record RouterTrafficRecord(string MessageType, string Role, string Sender, string Target);

    /// <summary>
    /// Reads the detector's own ERROR out of the logging pipeline — structured state, not the
    /// formatted string, so the assertions pin the VALUES the detector chose rather than the prose
    /// around them.
    /// </summary>
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
                if (!formatter(state, exception).StartsWith("ROUTER_TRAFFIC:", StringComparison.Ordinal))
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
