#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the cost of the schema PROBE paths —
/// <see cref="MeshOperations.GetContentSchema"/> and
/// <see cref="MeshOperations.ValidateContentAgainstSchema"/>.
///
/// <para>Both apply a NodeType's hub configuration to a hub they dispose in the same breath,
/// purely to read one <see cref="MeshWeaver.Domain.ITypeRegistry"/> entry. That hub used to get
/// the full per-node CONTROL PLANE as well — own-node subscription, persistence sampler, compile /
/// release-request / sources watchers, compile-state mirror. On a hub that lives for microseconds
/// those have nothing to do except open a <c>sync/</c> sub-hub apiece and then fault as the hub is
/// torn down out from under them (<c>HubDisposingException: … is shutting down — cannot create
/// '/MeshNode'</c>), which each watcher reports as a fault and retries.</para>
///
/// <para>Measured before the fix: ~20 hubs per probe, of which ~19 were <c>sync/</c> sub-hubs, and
/// (on the AKS portals) ~22 error/warning log lines per probe — every one of them a symptom of the
/// teardown race and none of them actionable. These tests fail if that machinery comes back.</para>
/// </summary>
public class ProbeHubCostTest : MonolithMeshTestBase
{
    private const string TestNodeType = "CostProbeProduct";
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    /// <summary>
    /// Generous relative to what a probe needs (the probe hub itself), but far below the ~20 the
    /// control plane used to cost. A bound rather than an exact count so an unrelated framework
    /// change that legitimately adds one stream does not fail the suite — while the regression
    /// this pins (the whole control plane returning) is an order of magnitude away.
    /// </summary>
    private const int MaxHubsPerProbe = 5;

    private readonly RecordingLoggerProvider recorder = new();

    public ProbeHubCostTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    public record CostProbeProduct
    {
        public string Name { get; init; } = "";
        public decimal Price { get; init; }
        public int Quantity { get; init; }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddAI()
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(recorder))
            .AddMeshNodes(new MeshNode(TestNodeType)
            {
                Name = "Cost Probe Product",
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<CostProbeProduct>())
                    .AddDefaultLayoutAreas()
            });
    }

    #region Instrumentation

    /// <summary>
    /// Counts every hub created anywhere in the hosted-hub subtree rooted at the watched hub,
    /// including the <c>sync/</c> sub-hubs a hub's data context spins up — which is where the bulk
    /// of a probe's cost sat. Recursive, so a hub created three levels down is still counted, and
    /// deduplicated by address so a hub observed through two collections counts once.
    /// </summary>
    private sealed class HubCreationCounter : IDisposable
    {
        private readonly CompositeDisposable subscriptions = new();
        private readonly ConcurrentDictionary<HostedHubsCollection, byte> watched = new();
        private readonly ConcurrentDictionary<string, string> created = new(StringComparer.Ordinal);

        public HubCreationCounter(IMessageHub root) => Watch(root);

        private void Watch(IMessageHub hub)
        {
            var collection = hub.ServiceProvider.GetService<HostedHubsCollection>();
            // A hub whose own collection was already watched (or that shares its parent's) must
            // not be subscribed twice — that is what double-counted every downstream creation.
            if (collection is null || !watched.TryAdd(collection, 0))
                return;

            // Hubs already present are the BASELINE: subscribe so their future children are
            // counted, but do not count them as created by the operation under test.
            foreach (var existing in collection.Hubs.ToArray())
                Watch(existing);

            var host = collection.Host.ToString();
            subscriptions.Add(collection.HubAdded.Subscribe(child =>
            {
                created.TryAdd(child.Address.ToString(), host);
                Watch(child);
            }));
        }

        /// <summary>Every hub created while watching, as address → host that created it.</summary>
        public IReadOnlyDictionary<string, string> Created => created;

        /// <summary>
        /// The hubs created UNDER <paramref name="hostPrefix"/> — i.e. the ones the probe hub
        /// itself is responsible for, as opposed to streams other hubs opened concurrently.
        /// </summary>
        public IReadOnlyList<string> CreatedUnder(string hostPrefix)
            => created.Where(kv => kv.Value.StartsWith(hostPrefix, StringComparison.Ordinal))
                .Select(kv => kv.Key).ToArray();

        public void Dispose() => subscriptions.Dispose();
    }

    /// <summary>
    /// Captures log records so a test can assert that a probe produced no teardown FAULT — the
    /// visible symptom this change removes. Instance state on an instance provider; nothing static.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message, Exception? Error)> records = new();

        public IReadOnlyList<(LogLevel Level, string Category, string Message, Exception? Error)> Records
            => records.ToArray();

        public void Clear() => records.Clear();

        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, records);

        public void Dispose() { }

        private sealed class Recorder(
            string category,
            ConcurrentQueue<(LogLevel, string, string, Exception?)> sink) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => Disposable.Empty;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                sink.Enqueue((logLevel, category, formatter(state, exception), exception));
            }
        }
    }

    private static bool IsTeardownFault(
        (LogLevel Level, string Category, string Message, Exception? Error) record)
    {
        if (Mentions(record.Message)) return true;
        for (var ex = record.Error; ex is not null; ex = ex.InnerException)
        {
            if (ex is HubDisposingException) return true;
            if (Mentions(ex.Message)) return true;
        }
        return false;

        static bool Mentions(string? text)
            => text is not null
               && (text.Contains("is shutting down", StringComparison.Ordinal)
                   || text.Contains("during disposal - collection is disposing", StringComparison.Ordinal));
    }

    private void ReportHubs(string label, HubCreationCounter counter)
    {
        Output.WriteLine($"[{label}] hubs created (all hosts): {counter.Created.Count}");
        foreach (var byHost in counter.Created.GroupBy(kv => Bucket(kv.Value))
                     .OrderByDescending(g => g.Count()))
        {
            Output.WriteLine($"    host {byHost.Key}: {byHost.Count()}");
            foreach (var child in byHost.GroupBy(kv => Bucket(kv.Key)))
                Output.WriteLine($"        {child.Count(),3} x {child.Key}");
        }
    }

    private static string Bucket(string address)
    {
        var slash = address.IndexOf('/');
        return slash < 0 ? address : address[..slash] + "/…";
    }

    private void ReportFaults(string label)
    {
        var faults = recorder.Records.Where(IsTeardownFault).ToArray();
        Output.WriteLine($"[{label}] teardown fault log records: {faults.Length}");
        foreach (var fault in faults.Take(5))
            Output.WriteLine($"    {fault.Level} {fault.Category}: {fault.Message}");
    }

    #endregion

    [Fact(Timeout = 60000)]
    public async Task ValidateContentAgainstSchema_DoesNotBuildTheNodeControlPlane()
    {
        var ops = new MeshOperations(Mesh);
        var node = new MeshNode("cost-probe", "ACME")
        {
            Name = "Cost Probe",
            NodeType = TestNodeType,
            Content = new CostProbeProduct { Name = "Widget", Price = 9.99m, Quantity = 5 },
        };

        recorder.Clear();
        using var counter = new HubCreationCounter(Mesh);

        var result = await ops.ValidateContentAgainstSchema(node)
            .Should().Within(20.Seconds()).Emit();

        ReportHubs("validate", counter);
        ReportFaults("validate");

        result.Should().BeNull("valid content must still validate — behaviour is unchanged");
        counter.CreatedUnder("_schema_validation").Count.Should().BeLessThanOrEqualTo(MaxHubsPerProbe,
            "a probe that only reads a type registry must not spin up the per-node control plane "
            + "(own-node subscription, persistence sampler, compile/release/sources watchers, "
            + "compile-state mirror) and a sync/ sub-hub for each of them");
        recorder.Records.Where(IsTeardownFault).Should().BeEmpty(
            "disposing the probe must not fault machinery that had no reason to be installed on it");
    }

    [Fact(Timeout = 60000)]
    public async Task GetContentSchema_DoesNotBuildTheNodeControlPlane()
    {
        var ops = new MeshOperations(Mesh);

        recorder.Clear();
        using var counter = new HubCreationCounter(Mesh);

        var schema = await ops.GetContentSchema(TestNodeType)
            .Should().Within(20.Seconds()).Emit();

        ReportHubs("schema", counter);
        ReportFaults("schema");

        schema.Should().NotBeNullOrEmpty("the schema must still be produced");
        schema!.Should().Contain("name").And.Contain("price").And.Contain("quantity");
        counter.CreatedUnder("_schema_lookup").Count.Should().BeLessThanOrEqualTo(MaxHubsPerProbe,
            "reading one type registry entry must not cost a per-node control plane");
        recorder.Records.Where(IsTeardownFault).Should().BeEmpty(
            "disposing the probe must not fault machinery that had no reason to be installed on it");
    }

    /// <summary>
    /// The invalid-content path returns the validation error AND the recovery schema. Both want
    /// the same type definition, so they must share ONE probe — not build, and immediately tear
    /// down, two full hubs for a single failed agent write.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ValidateContentWithSchema_OnInvalidContent_BuildsOneProbeNotTwo()
    {
        var ops = new MeshOperations(Mesh);
        var invalid = new MeshNode("cost-probe-invalid", "ACME")
        {
            Name = "Cost Probe Invalid",
            NodeType = TestNodeType,
            // Price is a decimal; a non-numeric string cannot deserialize into it.
            Content = new { name = "Widget", price = "not-a-number", quantity = 1 },
        };

        recorder.Clear();
        using var counter = new HubCreationCounter(Mesh);

        var message = await ops.ValidateContentWithSchema(invalid)
            .Should().Within(20.Seconds()).Emit();

        ReportHubs("validate+schema", counter);
        ReportFaults("validate+schema");

        // Behaviour preserved: the agent still gets the error AND the schema to recover with.
        message.Should().NotBeNull("invalid content must be rejected");
        message!.Should().Contain("Error");
        message.Should().Contain("Expected content schema",
            "the agent needs the schema to retry");
        message.Should().Contain("price");

        counter.Created.Keys.Where(a => a.StartsWith("_schema_", StringComparison.Ordinal))
            .Should().HaveCount(1,
                "validation and the recovery schema resolve the SAME type — one probe serves both");
        counter.CreatedUnder("_schema_validation").Count.Should().BeLessThanOrEqualTo(MaxHubsPerProbe);
        recorder.Records.Where(IsTeardownFault).Should().BeEmpty();
    }
}
