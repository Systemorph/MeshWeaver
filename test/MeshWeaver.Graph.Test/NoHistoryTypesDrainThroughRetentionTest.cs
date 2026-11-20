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
/// High-churn STATUS types declare <see cref="NodeTypeDefinition.KeepsHistory"/> = <c>false</c>, and
/// the data-retention sweep drains what they recorded before they did
/// (<c>Doc/Architecture/DataRetention</c> → "Types that keep no history"). Exercised through a REAL
/// mesh over the file-system store, which retains history (the in-memory store keeps none), so the
/// store's own <see cref="IVersionQuery.GetVersions"/> is the instrument: a drain that did not
/// happen cannot read as one.
///
/// <para>The backlog a type accumulated before its opt-out is planted with the store's own
/// <see cref="IVersionQuery.WriteVersion"/> — the same rows the version writer records for a type
/// that keeps history — because a C#-registered definition cannot change its mind mid-test.</para>
/// </summary>
public class NoHistoryTypesDrainThroughRetentionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A static (C#-registered) status type that keeps no history.</summary>
    private const string StatusType = "RetentionTestStatus";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-nohistory-drain-{Guid.NewGuid():N}");

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => new FileSystemBootstrap(root);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMeshNodes(new MeshNode(StatusType)
        {
            NodeType = MeshNode.NodeTypePath,
            Name = "Retention test status",
            Content = new NodeTypeDefinition { KeepsHistory = false },
            HubConfiguration = config => config,
        });

    private IVersionQuery Versions => Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();

    [Fact(Timeout = 180_000)]
    public async Task A_status_type_writes_no_history_and_an_ordinary_type_still_does()
    {
        var ct = TestContext.Current.CancellationToken;
        Versions.RetainsHistory.Should().BeTrue("precondition: a store that retains history, or nothing is measured");
        var status = Unique("Status");
        var document = Unique("Doc");
        await Create(status, StatusType, ct);
        await Create(document, "Markdown", ct);

        for (var i = 1; i <= 3; i++)
        {
            await Rename(status, $"tick {i}", ct);
            await Rename(document, $"edit {i}", ct);
        }

        (await History(document, ct)).Should().HaveCountGreaterThanOrEqualTo(4,
            "the control: an ordinary type records a version for its create and each of its three edits");
        (await History(status, ct)).Should().BeEmpty(
            "a type declaring keepsHistory:false records no version of any write — the declaration, not a prune, keeps it empty");
    }

    [Fact(Timeout = 180_000)]
    public async Task The_sweep_drains_a_no_history_type_in_bounded_batches_and_keeps_the_newest_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var status = Unique("Backlog");
        var document = Unique("Kept");
        // History alone, no live node: the drain is a store-level rule over recorded versions, and a
        // write of the live node would itself discard its newest versions and blur what is measured.
        // The backlog a status node recorded before its type opted out: versions 101..112.
        await Plant(status, StatusType, 101, 12, ct);
        await Plant(document, "Markdown", 101, 12, ct);
        (await History(status, ct)).Should().HaveCount(12, "precondition: the planted backlog is there");
        (await History(document, ct)).Should().HaveCount(12, "precondition: the ordinary type's history is there");

        var hub = Mesh.NodeOperationIssuingHub();
        var types = await DataRetentionSweep
            .ListTypesWithoutHistory(hub, [new RetentionPartition(TestPartition, true)], logger: null)
            .Should().Within(TestTimeouts.Convergence).Emit("the type listing must answer", ct);
        types.Should().Contain(StatusType, "the static definition declares keepsHistory:false");
        types.Should().NotContain("Markdown", "an ordinary type is never listed");

        // 7 per pass, in statements of 3: the cap and the batch both hold.
        var resolver = new DataRetentionResolver(Quiet with
        {
            NoHistory = new NoHistoryRetention { MaxDeletionsPerRun = 7, BatchSize = 3 },
        }, []);
        var first = await Pass(hub, resolver, types, ct);
        first.UnretainedVersions.Should().Be(7, "one pass removes at most MaxDeletionsPerRun, spent in batches of BatchSize");
        (await History(status, ct)).Should().HaveCount(5);

        var second = await Pass(hub, resolver, types, ct);
        second.UnretainedVersions.Should().Be(4, "the next pass drains the rest — every version but the newest");
        (await History(status, ct)).Select(v => v.Version).Should().Equal([112L],
            "the newest recorded version is never removed");

        var third = await Pass(hub, resolver, types, ct);
        third.UnretainedVersions.Should().Be(0, "a drained node gives nothing more");
        (await History(document, ct)).Should().HaveCount(12,
            "the negative control: an ordinary type's history is untouched by the no-history class");
    }

    [Fact(Timeout = 180_000)]
    public async Task Negative_control_with_the_class_switched_off_nothing_is_drained()
    {
        var ct = TestContext.Current.CancellationToken;
        var status = Unique("Off");
        await Plant(status, StatusType, 101, 6, ct);

        var hub = Mesh.NodeOperationIssuingHub();
        var resolver = new DataRetentionResolver(Quiet with { NoHistory = new NoHistoryRetention { Enabled = false } }, []);
        var outcome = await Pass(hub, resolver, ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, StatusType), ct);

        outcome.UnretainedVersions.Should().Be(0);
        // The pass has completed: no positive signal to wait for, so read once, now.
        (await History(status, ct)).Should().HaveCount(6, "a switched-off class removes nothing");
    }

    // ------------------------------------------------------------------------------ plumbing

    /// <summary>Every other class off, so a pass measures the no-history class alone.</summary>
    private static DataRetentionPolicy Quiet => DataRetentionPolicy.Default with
    {
        Activities = new ActivityRetention { Enabled = false },
        Versions = new VersionRetention { Enabled = false },
        LogEntries = new LogEntryRetention { Enabled = false },
    };

    private Task<PartitionRetentionOutcome> Pass(
        IMessageHub hub, DataRetentionResolver resolver, ImmutableHashSet<string> types, CancellationToken ct)
        => DataRetentionSweep
            .RunPass(hub, resolver, [new RetentionPartition(TestPartition, true)], types, DateTimeOffset.UtcNow, logger: null)
            .Select(outcomes => outcomes.Single())
            .Should().Within(TestTimeouts.Convergence).Emit("the pass must complete", ct);

    private string Unique(string prefix) => $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private Task Create(string path, string nodeType, CancellationToken ct)
        => NodeFactory.CreateNode(MeshNode.FromPath(path) with { Name = path, NodeType = nodeType, State = MeshNodeState.Active })
            .Should().Within(TestTimeouts.Convergence).Emit($"{path} must be created", ct);

    private Task<MeshNode> Rename(string path, string name, CancellationToken ct)
        => Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Name = name })
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit($"{path} must take the edit", ct);

    /// <summary>Writes versions <paramref name="from"/>..(from+count-1) of <paramref name="path"/> into the store.</summary>
    private async Task Plant(string path, string nodeType, long from, int count, CancellationToken ct)
    {
        for (var v = from; v < from + count; v++)
            await Versions.WriteVersion(MeshNode.FromPath(path) with { NodeType = nodeType, Version = v, Name = $"v{v}" },
                    Mesh.JsonSerializerOptions)
                .Should().Within(TestTimeouts.Convergence).Emit($"version {v} of {path} must be written", ct);
    }

    private Task<IList<MeshNodeVersion>> History(string path, CancellationToken ct)
        => Versions.GetVersions(path).ToList()
            .Should().Within(TestTimeouts.Convergence).Emit($"the history of {path} must be read", ct);

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
