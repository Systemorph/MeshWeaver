#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins <see cref="NodeTypeDataModelAreas.ProbeInstanceModel"/> — the <c>$model-probe</c> hub
/// behind the NodeType Overview's "Data model" section and the <c>$Model</c> area.
///
/// <para>The probe applies a NodeType's INSTANCE configuration to a hub, snapshots the resulting
/// type registry / content type / JSON schema, and disposes the hub immediately. It is now created
/// <see cref="MeshDataSourceExtensions.AsTransientNodeProbe"/>, so it no longer installs the
/// per-node control plane (own-node subscription, persistence sampler, compile / release-request /
/// sources watchers, compile-state mirror) that had nothing to do on a microsecond-lived hub except
/// open a <c>sync/</c> sub-hub apiece and then fault on teardown.</para>
///
/// <para>These tests exist because that path had NO end-to-end coverage: the snapshot the UI renders
/// must be unchanged, and the teardown must be quiet.</para>
/// </summary>
public class NodeTypeModelProbeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    public record ProbeOrder
    {
        public string Reference { get; init; } = "";
        public int Quantity { get; init; }
        public ProbeCustomer? Customer { get; init; }
    }

    public record ProbeCustomer
    {
        public string Name { get; init; } = "";
    }

    private readonly RecordingLoggerProvider recorder = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(recorder));

    /// <summary>The instance configuration a NodeType would apply to its instances.</summary>
    private static MessageHubConfiguration InstanceConfig(MessageHubConfiguration config)
        => config.AddMeshDataSource(source => source.WithContentType<ProbeOrder>());

    [Fact(Timeout = 60000)]
    public async Task ProbeInstanceModel_SnapshotsContentTypeAndSchema()
    {
        var model = await NodeTypeDataModelAreas
            .ProbeInstanceModel(Mesh, InstanceConfig)
            .Should().Within(30.Seconds()).Emit();

        model.Should().NotBeNull("the probe must resolve the instance data model");

        model!.ContentTypeName.Should().Be(nameof(ProbeOrder),
            "the content type registered by WithContentType is what the UI names");
        model.SchemaJson.Should().NotBeNullOrEmpty();
        model.SchemaJson!.Should().Contain("reference").And.Contain("quantity",
            "the schema must describe the content type's properties");

        model.SeedTypes.Should().Contain(td => td.Type == typeof(ProbeOrder),
            "the content type seeds the class diagram");
        // AllTypes is the probe's TYPE REGISTRY snapshot; a type reachable only as a property
        // (ProbeCustomer) is pulled in later by the diagram builder, not registered here.
        model.AllTypes.Should().Contain(td => td.Type == typeof(ProbeOrder),
            "the registered content type must be in the snapshot the diagram is built from");
    }

    /// <summary>
    /// The snapshotted type definitions must stay usable AFTER the probe hub is disposed — the
    /// whole point of snapshotting rather than holding the hub open. (The assembly load context is
    /// owned by the compilation cache, not the probe.)
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ProbeInstanceModel_SnapshotSurvivesProbeDisposal()
    {
        var model = await NodeTypeDataModelAreas
            .ProbeInstanceModel(Mesh, InstanceConfig)
            .Should().Within(30.Seconds()).Emit();

        model.Should().NotBeNull();

        // Reading the snapshot long after the probe hub is gone must not throw.
        var names = model!.AllTypes.Select(td => td.Type.Name).ToArray();
        names.Should().Contain(nameof(ProbeOrder));

        var diagram = MeshWeaver.Layout.Domain.DataModelLayoutArea.BuildMermaidDiagram(
            model.SeedTypes, model.AllTypes, "/Test/DataModel");
        diagram.Should().Contain("classDiagram").And.Contain(nameof(ProbeOrder));
    }

    [Fact(Timeout = 60000)]
    public async Task ProbeInstanceModel_DoesNotFaultOnTeardown()
    {
        recorder.Clear();
        using var counter = new HubCreationCounter(Mesh);

        var model = await NodeTypeDataModelAreas
            .ProbeInstanceModel(Mesh, InstanceConfig)
            .Should().Within(30.Seconds()).Emit();

        model.Should().NotBeNull();

        // Give the disposal cascade a chance to produce a fault if one is going to happen.
        // Waiting on the probe hub's absence is the actual condition, not a fixed sleep.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Where(_ => !counter.Created.Keys.Any(a =>
                a.StartsWith("$model-probe", StringComparison.Ordinal) && ProbeStillLive(a)))
            .FirstAsync()
            .Timeout(10.Seconds())
            .ToTask();

        var probeHubs = counter.Created.Keys
            .Where(a => a.StartsWith("$model-probe", StringComparison.Ordinal)).ToArray();
        var underProbe = counter.CreatedUnder("$model-probe");
        Output.WriteLine($"probe hubs: {probeHubs.Length}, sub-hubs under probe: {underProbe.Count}");

        var faults = recorder.Records.Where(IsTeardownFault).ToArray();
        foreach (var fault in faults.Take(5))
            Output.WriteLine($"    {fault.Level} {fault.Category}: {fault.Message}");

        faults.Should().BeEmpty(
            "a probe hub carries no per-node control plane, so disposing it faults nothing");
        underProbe.Count.Should().BeLessThanOrEqualTo(5,
            "the probe needs its data context, not a sync/ sub-hub per NodeType watcher");
    }

    private bool ProbeStillLive(string address)
        => Mesh.GetHostedHub(new Address(address), HostedHubCreation.Never) is { IsDisposing: false };

    #region Instrumentation

    private sealed class HubCreationCounter : IDisposable
    {
        private readonly CompositeDisposable subscriptions = new();
        private readonly ConcurrentDictionary<HostedHubsCollection, byte> watched = new();
        private readonly ConcurrentDictionary<string, string> created = new(StringComparer.Ordinal);

        public HubCreationCounter(IMessageHub root) => Watch(root);

        private void Watch(IMessageHub hub)
        {
            var collection = hub.ServiceProvider.GetService<HostedHubsCollection>();
            if (collection is null || !watched.TryAdd(collection, 0))
                return;

            foreach (var existing in collection.Hubs.ToArray())
                Watch(existing);

            var host = collection.Host.ToString();
            subscriptions.Add(collection.HubAdded.Subscribe(child =>
            {
                created.TryAdd(child.Address.ToString(), host);
                Watch(child);
            }));
        }

        public IReadOnlyDictionary<string, string> Created => created;

        public IReadOnlyList<string> CreatedUnder(string hostPrefix)
            => created.Where(kv => kv.Value.StartsWith(hostPrefix, StringComparison.Ordinal))
                .Select(kv => kv.Key).ToArray();

        public void Dispose() => subscriptions.Dispose();
    }

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

    #endregion
}
