using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Retention;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// High-churn STATUS types declare a short version-retention window
/// (<see cref="NodeTypeDefinition.HistoryMaxAge"/>): their writes still record history, and the
/// data-retention sweep prunes every version older than the window regardless of the ordinary
/// keep-newest floor, never the newest (<c>Doc/Architecture/DataRetention</c> → "Short-lived
/// history"). Exercised through a REAL mesh over the file-system store, which retains history (the
/// in-memory store keeps none); the store's own <see cref="IVersionQuery.GetVersions"/> is the
/// instrument, so a prune that did not happen cannot read as one.
///
/// <para>Aged versions are planted with the store's own <see cref="IVersionQuery.WriteVersion"/> and
/// dated through their file's write time — the clock the file-system store reports.</para>
/// </summary>
public class ShortLivedHistoryThroughRetentionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A static (C#-registered) status type keeping history for seven days.</summary>
    private const string StatusType = "RetentionTestStatus";

    /// <summary>A static type that keeps no history at all.</summary>
    private const string EphemeralType = "RetentionTestEphemeral";

    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-shortlived-{Guid.NewGuid():N}");

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => new FileSystemBootstrap(root);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMeshNodes(
            new MeshNode(StatusType)
            {
                // No NodeType stamp, the shape ThreadSupervisor and DependencyEdgeSet register: the typed content is the declaration.
                Name = "Retention test status",
                Content = new NodeTypeDefinition { HistoryMaxAge = Week },
                HubConfiguration = config => config,
            },
            new MeshNode(EphemeralType)
            {
                NodeType = MeshNode.NodeTypePath,
                Name = "Retention test ephemeral",
                Content = new NodeTypeDefinition { KeepsHistory = false },
                HubConfiguration = config => config,
            });

    private IVersionQuery Versions => Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();

    [Fact(Timeout = 180_000)]
    public async Task A_short_lived_type_still_records_history_on_every_write()
    {
        var ct = TestContext.Current.CancellationToken;
        Versions.RetainsHistory.Should().BeTrue("precondition: a store that retains history, or nothing is measured");
        var status = Unique("Status");
        await NodeFactory.CreateNode(MeshNode.FromPath(status) with { Name = status, NodeType = StatusType, State = MeshNodeState.Active })
            .Should().Within(TestTimeouts.Convergence).Emit($"{status} must be created", ct);
        for (var i = 1; i <= 3; i++)
            await Mesh.GetWorkspace().GetMeshNodeStream(status).Update(n => n with { Name = $"tick {i}" }).Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit($"{status} must take tick {i}", ct);

        (await History(status, ct)).Should().HaveCountGreaterThanOrEqualTo(4,
            "a short window prunes OLD history; recent states stay restorable, so every write is still recorded");
    }

    [Fact(Timeout = 180_000)]
    public async Task The_sweep_prunes_versions_past_the_window_in_bounded_batches_and_keeps_the_newest()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var hub = Mesh.NodeOperationIssuingHub();
        // 12 versions, v1..v10 aged 30..21 days, v11 and v12 inside the window (2 days, 1 day).
        var status = Unique("Backlog");
        await Plant(status, StatusType, Enumerable.Range(1, 10).Select(v => (v, now.AddDays(-31 + v)))
            .Append((11, now.AddDays(-2))).Append((12, now.AddDays(-1))), ct);
        // The same history on an ordinary type — the negative control.
        var document = Unique("Kept");
        await Plant(document, "Markdown", Enumerable.Range(1, 12).Select(v => (v, now.AddDays(-31 + v))), ct);
        // Every version old, the newest included: the newest must still stay.
        var stale = Unique("Stale");
        await Plant(stale, StatusType, Enumerable.Range(1, 3).Select(v => (v, now.AddDays(-40 + v))), ct);

        var windows = await DataRetentionSweep
            .ListTypeHistoryWindows(hub, [new RetentionPartition(TestPartition, true)], logger: null)
            .Should().Within(TestTimeouts.Convergence).Emit("the type listing must answer", ct);
        windows.GetValueOrDefault(StatusType).Should().Be(Week, "the static definition declares a seven-day window");
        windows.Should().ContainKey(EphemeralType);
        windows[EphemeralType].Should().Be(DataRetentionSweep.KeepsNoHistory, "keepsHistory:false declares no window at all");
        windows.Should().NotContainKey("Markdown", "an ordinary type declares no window");

        // 7 per pass, in statements of 3: the cap and the batch both hold.
        var resolver = new DataRetentionResolver(Quiet with
        {
            ShortLivedHistory = new ShortLivedHistoryRetention { MaxDeletionsPerRun = 7, BatchSize = 3 },
        }, []);
        (await Pass(hub, resolver, windows, now, ct)).ShortLivedVersions.Should().Be(7,
            "one pass removes at most MaxDeletionsPerRun, spent in batches of BatchSize");
        (await Pass(hub, resolver, windows, now, ct)).ShortLivedVersions.Should().Be(5,
            "the next pass removes the rest past the window: 10 of the backlog node, 2 of the stale one");
        (await Pass(hub, resolver, windows, now, ct)).ShortLivedVersions.Should().Be(0, "nothing past the window is left");

        (await Remaining(status, ct)).Should().Equal([11L, 12L], "the versions inside the window stay — no keep-newest floor kept v1..v10");
        (await Remaining(stale, ct)).Should().Equal([3L], "the newest recorded version stays, however old");
        (await Remaining(document, ct)).Should().HaveCount(12,
            "the negative control: an ordinary type's history is untouched by the short-lived class");
    }

    [Fact(Timeout = 180_000)]
    public async Task The_instance_window_overrides_the_declared_one_and_a_no_history_type_keeps_only_its_newest()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var hub = Mesh.NodeOperationIssuingHub();
        var status = Unique("Override");
        await Plant(status, StatusType, Enumerable.Range(1, 5).Select(v => (v, now.AddDays(-6 + v))), ct); // 5..1 days old
        var ephemeral = Unique("Ephemeral");
        await Plant(ephemeral, EphemeralType, Enumerable.Range(1, 4).Select(v => (v, now.AddMinutes(-v))), ct);
        var windows = ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, new[]
        {
            KeyValuePair.Create(StatusType, Week),
            KeyValuePair.Create(EphemeralType, DataRetentionSweep.KeepsNoHistory),
        });

        // Retention:ShortLivedHistory:MaxAge = 2 days replaces the declared week.
        var resolver = new DataRetentionResolver(Quiet with
        {
            ShortLivedHistory = new ShortLivedHistoryRetention { MaxAge = TimeSpan.FromDays(2) },
        }, []);
        await Pass(hub, resolver, windows, now, ct);

        (await Remaining(status, ct)).Should().Equal([4L, 5L], "only the versions inside the instance's 2-day window stay");
        (await Remaining(ephemeral, ct)).Should().Equal([4L],
            "a type that keeps no history keeps only its newest version — minutes old, inside any window");
    }

    [Fact(Timeout = 180_000)]
    public async Task Negative_control_with_the_class_switched_off_nothing_is_pruned()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var status = Unique("Off");
        await Plant(status, StatusType, Enumerable.Range(1, 6).Select(v => (v, now.AddDays(-60 + v))), ct);

        var resolver = new DataRetentionResolver(
            Quiet with { ShortLivedHistory = new ShortLivedHistoryRetention { Enabled = false } }, []);
        var outcome = await Pass(Mesh.NodeOperationIssuingHub(), resolver,
            ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase, new[] { KeyValuePair.Create(StatusType, Week) }), now, ct);

        outcome.ShortLivedVersions.Should().Be(0);
        // The pass has completed: no positive signal to wait for, so read once, now.
        (await Remaining(status, ct)).Should().HaveCount(6, "a switched-off class removes nothing");
    }

    // ------------------------------------------------------------------------------ plumbing

    /// <summary>Every other class off, so a pass measures the short-lived class alone.</summary>
    private static DataRetentionPolicy Quiet => DataRetentionPolicy.Default with
    {
        Activities = new ActivityRetention { Enabled = false },
        Versions = new VersionRetention { Enabled = false },
        LogEntries = new LogEntryRetention { Enabled = false },
    };

    private string Unique(string prefix) => $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private Task<PartitionRetentionOutcome> Pass(
        IMessageHub hub, DataRetentionResolver resolver, ImmutableDictionary<string, TimeSpan> windows,
        DateTimeOffset now, CancellationToken ct)
        => DataRetentionSweep
            .RunPass(hub, resolver, [new RetentionPartition(TestPartition, true)], windows, now, logger: null)
            .Select(outcomes => outcomes.Single())
            .Should().Within(TestTimeouts.Convergence).Emit("the pass must complete", ct);

    /// <summary>Writes the given versions of <paramref name="path"/> into the store, each dated by its file's write time.</summary>
    private async Task Plant(string path, string nodeType, IEnumerable<(int Version, DateTimeOffset At)> versions, CancellationToken ct)
    {
        var id = path[(path.LastIndexOf('/') + 1)..];
        foreach (var (version, at) in versions)
        {
            await Versions.WriteVersion(MeshNode.FromPath(path) with { NodeType = nodeType, Version = version, Name = $"v{version}" },
                    Mesh.JsonSerializerOptions)
                .Should().Within(TestTimeouts.Convergence).Emit($"version {version} of {path} must be written", ct);
            var file = Directory.EnumerateFiles(root, $"{id}_{version}.json", SearchOption.AllDirectories).Single();
            File.SetLastWriteTimeUtc(file, at.UtcDateTime);
        }
    }

    private Task<IList<MeshNodeVersion>> History(string path, CancellationToken ct)
        => Versions.GetVersions(path).ToList()
            .Should().Within(TestTimeouts.Convergence).Emit($"the history of {path} must be read", ct);

    private async Task<long[]> Remaining(string path, CancellationToken ct)
        => (await History(path, ct)).Select(v => v.Version).Order().ToArray();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    /// <summary>The monolith mesh over a file-system store rooted at a per-test directory.</summary>
    private sealed class FileSystemBootstrap(string baseDirectory) : IMeshBootstrap
    {
        /// <inheritdoc />
        public string Name => "monolith-filesystem";

        /// <inheritdoc />
        public MeshBuilder Bootstrap(MeshBuilder builder)
        {
            Directory.CreateDirectory(baseDirectory);
            return builder.UseMonolithMesh().AddFileSystemPersistence(baseDirectory);
        }
    }
}
