#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the ISSUING half of "the router must be neither end of a delivery" — the half
/// <see cref="NodeOperationOriginTest"/> cannot see. That test covers a request a CLIENT hub aims at
/// the node-operation target; this one covers work issued while HOLDING the root mesh hub itself —
/// the DI-injected <c>IMessageHub</c> every mesh-singleton service gets (the plugin-catalog boot
/// seed, the log-incident ingest, the credential resolvers). Before the fix, their one-shot reads
/// went out stamped <c>Sender = mesh/{id}</c> and the <c>GetDataResponse</c> — or, for a missing
/// node, the <c>DeliveryFailure</c> — was addressed straight back at <c>mesh/{id}</c>: production
/// 2026-08-10 logged <c>"GetDataResponse has the mesh hub as target (sender:
/// Hosting/_Access/Public_Access…)"</c> and <c>"DeliveryFailure has the mesh hub as target (sender:
/// Plugins/_DefaultInstallLedger…)"</c> (issue #1113).
///
/// <para>The assertion reads the ERROR the <c>ROUTER_TRAFFIC</c> detector itself logs, so a
/// regression here is exactly one production ERROR line — plus <see cref="RouterTrafficRule"/>, the
/// detector's own predicate, applied to the real response delivery of the write-shaped seam.</para>
/// </summary>
public class NodeOperationIssuingHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly RouterTrafficCapture capture = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Capture the detector's ERROR lines out of the REAL logging pipeline — asserting on
            // them is the only way to pin the detector's verdict without re-implementing it.
            .ConfigureServices(s => s.AddLogging(l =>
                l.Services.AddSingleton<ILoggerProvider>(capture)));

    [Fact(Timeout = 60_000)]
    public async Task WorkIssuedWhileHoldingTheRouter_NeverMakesTheRouterAnEnd()
    {
        // ── The seam itself ─────────────────────────────────────────────────────────────
        // The router hops onto the shared off-router execution hub; any other hub is returned
        // unchanged (a portal/session/import caller keeps its identity byte-for-byte).
        Mesh.NodeOperationIssuingHub().Should().BeSameAs(Mesh.NodeOperationExecutionHub()!,
            "the ROUTER must issue node work on the dedicated off-router hub");
        var client = GetClient();
        client.NodeOperationIssuingHub().Should().BeSameAs(client,
            "a non-router hub is its own issuing hub — the seam is a no-op for it");

        // ── Read-shaped seam: hub.GetMeshNode issued while holding the router ──────────
        var path = $"{TestPartition}/IssuingHub-{Guid.NewGuid():N}";
        var node = MeshNode.FromPath(path) with
        {
            Name = "Issuing Hub Probe",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        var create = await AwaitResponseAsync(
            new CreateNodeRequest(node), o => o.WithTarget(client.NodeOperationTarget()), client);
        create.Message.Error.Should().BeNull("the probe node must exist for the read to resolve");

        // The exact shape of InstanceAutoRegistrationService / RegistryTokenResolver: a one-shot
        // GetMeshNode read on the DI root mesh hub. Must resolve — and off the router.
        var read = await Mesh.GetMeshNode(path).FirstAsync().ToTask();
        read.Should().NotBeNull("the read must still resolve after the off-router retarget");
        read!.Path.Should().Be(path);

        // The DeliveryFailure arm: a MISSING node's read (the default-install ledger on a fresh
        // instance) answers with a routing failure — which must land on the issuing hub, never mesh.
        var missing = await Mesh.GetMeshNode($"{TestPartition}/missing-{Guid.NewGuid():N}")
            .FirstAsync().ToTask();
        missing.Should().BeNull("a genuinely absent node resolves to null");

        // ── Write-shaped seam: the target-less CreateOrUpdate the boot services post ────
        // LogIncidentIngestService / PackageInstaller.Upsert shape: a target-less
        // CreateOrUpdateNodeRequest issued via the seam. Target-less ⇒ it EXECUTES on the posting
        // hub, so issuing off the router is also what keeps the work off the router's action block.
        var recordPath = $"{TestPartition}/IssuingHubRecord-{Guid.NewGuid():N}";
        var record = MeshNode.FromPath(recordPath) with
        {
            Name = "Issuing Hub Record",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        };
        var upsert = await Mesh.NodeOperationIssuingHub()
            .Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(record))
            .FirstAsync().ToTask();
        upsert.Message.Success.Should().BeTrue(
            "the upsert must genuinely succeed — a rejected op would prove nothing about where work runs");
        RouterTrafficRule.RoleOf(upsert.Target?.Type, upsert.Sender?.Type, upsert.Message)
            .Should().BeNull("the router must be neither end of the write's response delivery");

        // ── The detector's own verdict over everything this test drove ──────────────────
        // Zero ROUTER_TRAFFIC ERROR lines: each captured record here is exactly one production
        // ERROR line of issue #1113.
        capture.Records.Should().BeEmpty(
            "work issued while holding the router must never make the router an end of any delivery; "
            + $"got: [{string.Join("; ", capture.Records.Select(r => r.ToString()))}]");
    }

    /// <summary>Captures the <c>ROUTER_TRAFFIC</c> ERROR records the detector logs.</summary>
    private sealed record RouterTrafficRecord(string MessageType, string Role, string Sender, string Target);

    private sealed class RouterTrafficCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<RouterTrafficRecord> records = new();

        internal RouterTrafficRecord[] Records => records.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(records);

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
                        return pair.Value?.ToString() ?? "";
                return "";
            }
        }
    }
}
