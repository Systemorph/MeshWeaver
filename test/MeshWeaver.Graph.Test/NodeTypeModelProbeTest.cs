#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

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

    public record ProbeSelfRead(string Id, string Verdict);

    /// <summary>The instance configuration a NodeType would apply to its instances.</summary>
    private static MessageHubConfiguration InstanceConfig(MessageHubConfiguration config)
        => config.AddMeshDataSource(source => source.WithContentType<ProbeOrder>());

    /// <summary>
    /// The same instance configuration plus what real NodeType content routinely does: derive a
    /// mesh path from the hub's OWN address and read it. On a real per-node hub the address IS the
    /// node's path, so this is correct code; on the probe it collapses onto the synthetic
    /// <c>$model-probe/{guid}</c>. See the test below for why that used to cost 10 s and a process.
    /// </summary>
    private static MessageHubConfiguration SelfReadingInstanceConfig(MessageHubConfiguration config)
        => config
            .AddMeshDataSource(source => source.WithContentType<ProbeOrder>())
            .AddData(data => data
                .WithVirtualDataSource("SelfRead", vds =>
                    vds.WithVirtualType<ProbeSelfRead>(workspace =>
                        workspace.Hub
                            .GetMeshNode(workspace.Hub.Address.ToString(), TimeSpan.FromSeconds(10))
                            .Select(node => (IEnumerable<ProbeSelfRead>)
                                [new ProbeSelfRead("1", node?.Path ?? "absent")]))));

    /// <summary>
    /// The verdict of the probe's own-address read through the stream cache, reported by the
    /// provider itself. A POSITIVE signal both ways: <c>"empty"</c> when the read completed with
    /// no node (the contract), <c>"fault: …"</c> carrying the exception when it did not. Without
    /// it the test could only sample the log and hope — and the correct behaviour produces no log
    /// line at all, so an assertion on the log's absence passes whenever it runs before the fault,
    /// which is most of the time (measured: it passed on the unfixed build).
    ///
    /// <para>Instance field on the test, never static — it dies with the test class.</para>
    /// </summary>
    private readonly AsyncSubject<string> selfReadVerdict = new();

    /// <summary>
    /// The SECOND own-node read seam, and the one in-mesh NodeType content actually reaches for:
    /// the process-wide <see cref="IMeshNodeStreamCache"/>. This is a faithful copy of the Store
    /// catalog's <c>store-packages</c> provider (<c>StoreManifestSource.Sources</c>) — read the
    /// hub's own address through the cache, project the node's content, and hand the virtual
    /// collection an initialization value so the hub is up instantly. Correct code on a real
    /// per-node hub, where the hub's address IS its mesh path; on the probe it collapses onto
    /// <c>$model-probe/{guid}</c>.
    /// </summary>
    private MessageHubConfiguration CacheSelfReadingInstanceConfig(MessageHubConfiguration config)
        => config
            .AddMeshDataSource(source => source.WithContentType<ProbeOrder>())
            .AddData(data => data
                .WithVirtualDataSource("cache-self-read", vds =>
                    vds.WithVirtualType<ProbeSelfRead>(workspace =>
                    {
                        var hub = workspace.Hub;
                        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
                        return cache.GetStream(hub.Address.ToString(), hub.JsonSerializerOptions)
                            .Do(_ => { },
                                ex => Report($"fault: {ex.GetType().Name}: {ex.Message}"),
                                () => Report("empty"))
                            .Select(node => (IEnumerable<ProbeSelfRead>)
                                [new ProbeSelfRead("1", node.Path)])
                            .StartWith((IEnumerable<ProbeSelfRead>)
                                [new ProbeSelfRead("1", "initial")]);
                    })));

    private void Report(string verdict)
    {
        selfReadVerdict.OnNext(verdict);
        selfReadVerdict.OnCompleted();
    }

    /// <summary>
    /// 🚨 <b>A probe's own-address read through the STREAM CACHE must be answered, not faulted</b>
    /// — Systemorph/MeshWeaver#2894.
    ///
    /// <para><see cref="MeshNodeStreamExtensions.GetMeshNodeOutcome"/> has answered a probe's read
    /// of its own address <c>Absent</c> since #2468, but the cache seam had no guard at all — and
    /// the cache is what content uses, because it is the only own-node read that answers before
    /// the hub's init gates open. Unguarded, that read either evaluated the caller's permissions
    /// on the synthetic address ("User 'rsalzmann' lacks Read permission on '$model-probe/…'", the
    /// reported line) or routed and died on "No node found at '$model-probe/…'" (the sister
    /// incident on the same pod, same second). Either fault reaches <c>VirtualDataSource</c>'s
    /// error arm, which logs <c>"the provider for collection 'X' faulted … frozen at its last
    /// emission"</c> and leaves the probe serving a data model with that collection missing.</para>
    ///
    /// <para>In-process — where routing DOES find the probe's hosted hub — the same read produces
    /// the third shape instead: the subscribe is delivered to the probe itself and parks behind
    /// the <c>DataContextInit</c> gate that the probe's own initialization opens, so the read
    /// never ends at all and the collection is simply never filled. That is what this test
    /// measured on the unfixed build: the provider reported NO verdict within 20 s.</para>
    ///
    /// <para>The probe itself completes either way — it completed on the unfixed build too, just
    /// with a broken model and an error per affected user. So the assertion is the provider's OWN
    /// verdict on its read, not the snapshot and not the absence of a log line.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ProbeInstanceModel_OwnAddressReadThroughTheStreamCache_DoesNotFaultTheCollection()
    {
        var model = await NodeTypeDataModelAreas
            .ProbeInstanceModel(Mesh, CacheSelfReadingInstanceConfig)
            .Should().Within(40.Seconds()).Emit();

        model.Should().NotBeNull(
            "content that reads its own address through the cache must not stop the snapshot");
        model!.ContentTypeName.Should().Be(nameof(ProbeOrder));

        var verdict = await selfReadVerdict.Should().Within(20.Seconds())
            .Emit("the provider always reports how its own-address read ended");

        Output.WriteLine($"own-address read through the stream cache ended: {verdict}");

        verdict.Should().Be("empty",
            "a probe has no mesh node, so reading its own address through the stream cache is "
            + "answered with an empty stream — never a permission denial and never a routing "
            + "NotFound, both of which freeze the virtual collection that issued the read (#2894)");
    }

    /// <summary>
    /// The same guard, measured DIRECTLY on the seam rather than through a probe hub, and under a
    /// real user identity.
    ///
    /// <para>🚨 What the failure is depends on how far the read gets, and both ends are #2894. On a
    /// portal with row-level security installed the cache's per-user gate runs first: a probe
    /// address is not a partition-rooted path, so the evaluator answers <c>Permission.None</c> and
    /// <c>GateOnRead</c> throws <c>UnauthorizedAccessException</c> naming the triggering user —
    /// the reported line. This fixture registers no <c>EffectivePermissionsDelegate</c>, so the
    /// gate is skipped and the read reaches the upstream instead, where routing answers
    /// <c>DeliveryFailureException: No node found at '$model-probe/…'</c> — the SISTER incident,
    /// logged on the same production pod in the same second. Measured on the unfixed build, this
    /// case fails with exactly that exception in 212 ms.</para>
    ///
    /// <para>An empty stream is the stream-shaped twin of <c>NodeReadStatus.Absent</c>: no
    /// emission, because there is no node; immediate completion, because there never will be one.
    /// It is reached before either the permission probe or the upstream subscribe, so it removes
    /// both shapes at once.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task StreamCache_ReadOfAProbeOwnAddress_CompletesEmptyForAnUnprivilegedUser()
    {
        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var probePath = $"{TransientProbeAddresses.ModelProbePrefix}{Guid.NewGuid():N}";

        IObservable<MeshNode> stream;
        // The gate captures the caller's identity synchronously, on the calling thread — so the
        // scope has to be open across the GetStream CALL, not across the subscribe.
        using (access.SwitchAccessContext(new AccessContext
               {
                   ObjectId = "probe-viewer",
                   Name = "Probe Viewer",
               }))
        {
            stream = cache.GetStream(probePath, Mesh.JsonSerializerOptions);
        }

        var emitted = await stream.ToArray().Timeout(20.Seconds()).Await();

        emitted.Should().BeEmpty(
            "there is no node at a probe's synthetic address and there never will be — the read "
            + "is answered without a permission probe and without an upstream subscribe (#2894)");
    }

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
            .Await();

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

    /// <summary>
    /// A probe hub has NO MESH NODE — so a read of its own address must be answered immediately,
    /// not parked behind the probe's own initialization gates (Systemorph/MeshWeaver#2468).
    ///
    /// <para>The read is issued by the probe's own data-context initialization and posted to the
    /// probe itself, where it defers behind <c>DataContextInit</c> / <c>MeshNodeInit</c> — the very
    /// gates that initialization opens. A cycle: the read could only ever end by spending its whole
    /// budget. In CI that budget elapsed on a <c>CancellationTokenSource</c> timer thread, which is
    /// how a <b>content</b> loader's mesh read ended up aborting the Doc content gate's process.</para>
    ///
    /// <para>The bar is elapsed time, deliberately: the assertion has to fail if the read parks,
    /// and 8 s is under the read's own 10 s budget while leaving a loaded runner room for the
    /// probe's ordinary cost (~1 s locally).</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ProbeInstanceModel_OwnAddressRead_IsAnsweredWithoutBurningTheBudget()
    {
        var started = Stopwatch.StartNew();
        var model = await NodeTypeDataModelAreas
            .ProbeInstanceModel(Mesh, SelfReadingInstanceConfig)
            .Should().Within(40.Seconds()).Emit();
        started.Stop();

        Output.WriteLine($"probe with a self-reading provider completed in {started.Elapsed}");

        model.Should().NotBeNull(
            "content that reads its own address must not stop the probe from snapshotting");
        model!.ContentTypeName.Should().Be(nameof(ProbeOrder));

        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8),
            "a read of the probe's own address is answered Absent immediately — parking it behind "
            + "the probe's own init gates is a cycle that can only end by burning the full budget");
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
