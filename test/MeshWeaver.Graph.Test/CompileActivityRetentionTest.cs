using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Compile-activity retention: every Roslyn compile of a NodeType mints a new
/// <c>{nodeType}/_Activity/compile-*</c> record, and before this nothing removed one. On
/// memex.systemorph.com there were 325 under <c>Crm/Interaction</c>, and deleting the retired
/// <c>Crm/Client</c> took more than 60 s. The owner now prunes its history to the newest N plus
/// the newest failure each time it writes a new record.
///
/// <para>This class pins what a prune REMOVES, what it KEEPS (the newest N, the last failure, the
/// current record, and anything that is not a compile activity), that one run is BOUNDED, and the
/// negative control: with the policy off, nothing is removed.</para>
/// </summary>
public class CompileActivityRetentionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static TimeSpan Bound => TestTimeouts.Convergence;

    private static readonly JsonSerializerOptions PlainOptions = new();

    /// <summary>A compile-run id in the shape the pipeline mints: <c>compile-</c>, a 17-digit
    /// timestamp, then a GUID-like tail. The index is folded into the timestamp so ids sort with it.</summary>
    private static string Id(int index) => $"compile-2026100900{index:D7}a1b2c3";

    private static MeshNode Compile(string ns, int index, DateTimeOffset at, ActivityStatus status) =>
        new MeshNode(Id(index), ns)
        {
            NodeType = GraphNodeTypeNames.Activity,
            Name = $"Compile {index}",
            State = MeshNodeState.Active,
            LastModified = at,
            Content = new ActivityLog(ActivityCategory.Compilation) { Id = Id(index), Status = status },
        };

    /// <summary>25 compiles a minute apart; #3 and #5 failed, everything else succeeded.</summary>
    private static List<MeshNode> History(string ns, DateTimeOffset now) =>
        Enumerable.Range(0, 25)
            .Select(i => Compile(ns, i, now.AddMinutes(i - 25),
                i is 3 or 5 ? ActivityStatus.Failed : ActivityStatus.Succeeded))
            .ToList();

    // ---------------------------------------------------------------- the policy, as a pure rule

    [Fact]
    public void It_keeps_the_newest_N_the_last_failure_and_the_current_record_and_selects_the_rest_oldest_first()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = History("T/_Activity", now);
        var policy = CompileActivityRetention.Default with { KeepLast = 10, MaxDeletionsPerRun = 50 };

        var selected = policy.SelectForPruning(rows, currentActivityPath: $"T/_Activity/{Id(24)}", PlainOptions);

        // Newest ten are 15..24; the newest failure is #5; #3 is an OLDER failure and goes.
        var expected = Enumerable.Range(0, 15).Where(i => i != 5).Select(i => $"T/_Activity/{Id(i)}");
        selected.Should().Equal(expected,
            "the newest 10 and the newest failure stay; everything older is selected, oldest first");
    }

    [Fact]
    public void One_prune_is_capped_and_takes_the_oldest_first()
    {
        var rows = History("T/_Activity", DateTimeOffset.UtcNow);
        var policy = CompileActivityRetention.Default with { KeepLast = 10, MaxDeletionsPerRun = 4 };

        policy.SelectForPruning(rows, null, PlainOptions).Should().Equal(
            $"T/_Activity/{Id(0)}", $"T/_Activity/{Id(1)}", $"T/_Activity/{Id(2)}", $"T/_Activity/{Id(3)}");
    }

    [Fact]
    public void It_never_selects_a_row_that_is_not_a_dated_compile_activity()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-30);
        var rows = new List<MeshNode>
        {
            // Another writer's activity in the same container: neither the id nor the category is a compile.
            new MeshNode("write-config", "T/_Activity")
            {
                NodeType = GraphNodeTypeNames.Activity, LastModified = old,
                Content = new ActivityLog("DataUpdate") { Id = "write-config" },
            },
            // A compile-run id whose category says otherwise.
            new MeshNode(Id(800), "T/_Activity")
            {
                NodeType = GraphNodeTypeNames.Activity, LastModified = old,
                Content = new ActivityLog("Import") { Id = Id(800) },
            },
            // The compiler's fixed state row: a Compilation activity whose id merely starts with
            // `compile` (NodeTypeCompileState.StateId). It is state, not run history.
            new MeshNode("compile-state", "T/_Activity")
            {
                NodeType = GraphNodeTypeNames.Activity, LastModified = old,
                Content = new ActivityLog(ActivityCategory.Compilation) { Id = "compile-state" },
            },
            // Undated: this policy cannot age it, so it keeps it.
            Compile("T/_Activity", 900, default, ActivityStatus.Succeeded),
        };
        rows.AddRange(History("T/_Activity", now));

        var selected = CompileActivityRetention.Default.SelectForPruning(rows, null, PlainOptions);

        selected.Should().NotContain(new[]
        {
            "T/_Activity/write-config", $"T/_Activity/{Id(800)}", "T/_Activity/compile-state", $"T/_Activity/{Id(900)}",
        });
        selected.Should().HaveCount(14, "the 25 dated compiles minus the newest 10 and the newest failure");
    }

    [Fact]
    public void Disabled_selects_nothing()
        => (CompileActivityRetention.Default with { Enabled = false })
            .SelectForPruning(History("T/_Activity", DateTimeOffset.UtcNow), null, PlainOptions)
            .Should().BeEmpty();

    [Fact]
    public void Configuration_cannot_widen_what_is_deleted()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [CompileActivityRetention.KeepLastConfigKey] = "0",
            [CompileActivityRetention.MaxDeletionsPerRunConfigKey] = "not-a-number",
        }).Build();

        var policy = CompileActivityRetention.FromConfiguration(config);

        policy.KeepLast.Should().Be(CompileActivityRetention.MinimumKeepLast, "a zero is a typo, clamped");
        policy.MaxDeletionsPerRun.Should().Be(CompileActivityRetention.Default.MaxDeletionsPerRun,
            "a malformed value leaves the shipped bound in place");
        policy.Enabled.Should().BeTrue();
    }

    // --------------------------------------------------------------------------- end to end

    [Fact(Timeout = 180000)]
    public async Task Prune_removes_the_selected_history_through_the_mesh_and_keeps_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = $"{TestPartition}/RetainedType";
        var ns = $"{owner}/_Activity";
        await SeedAsync(new MeshNode("RetainedType", TestPartition)
        {
            NodeType = "Markdown", Name = "RetainedType", State = MeshNodeState.Active,
        }, ct);
        foreach (var row in History(ns, DateTimeOffset.UtcNow))
            await SeedAsync(row, ct);
        await SeedAsync(new MeshNode("write-config", ns)
        {
            NodeType = GraphNodeTypeNames.Activity, Name = "Write", State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow.AddDays(-30),
            Content = new ActivityLog("DataUpdate") { Id = "write-config" },
        }, ct);

        var seeded = await SettleAsync(Children(ns), rows => rows.Count == 26, ct);
        seeded.Should().HaveCount(26, "the prune reads through the query index, so the seed must be visible there");

        var removed = await CompileActivityRetention
            .Prune(Mesh, owner, $"{ns}/{Id(24)}", CompileActivityRetention.Default, logger: null)
            .FirstAsync().Timeout(Bound).Await(ct);

        removed.Should().Be(14);
        var kept = await SettleAsync(Children(ns), rows => rows.Count == 12, ct);
        kept.Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal).Should().Equal(
            Enumerable.Range(15, 10).Select(Id)
                .Append(Id(5))
                .Append("write-config")
                .OrderBy(id => id, StringComparer.Ordinal),
            "the newest 10, the newest failure and the other writer's activity are what remains");

        // Idempotent: a second prune over the pruned history removes nothing.
        (await CompileActivityRetention
            .Prune(Mesh, owner, $"{ns}/{Id(24)}", CompileActivityRetention.Default, logger: null)
            .FirstAsync().Timeout(Bound).Await(ct)).Should().Be(0);
    }

    [Fact(Timeout = 180000)]
    public async Task Other_writers_newer_activities_cannot_fill_the_window_and_starve_the_prune()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = $"{TestPartition}/BusyType";
        var ns = $"{owner}/_Activity";
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(new MeshNode("BusyType", TestPartition)
        {
            NodeType = "Markdown", Name = "BusyType", State = MeshNodeState.Active,
        }, ct);
        // Five OLD compiles, then six NEWER activities from another writer.
        for (var i = 0; i < 5; i++)
            await SeedAsync(Compile(ns, i, now.AddHours(-10 + i), ActivityStatus.Succeeded), ct);
        for (var i = 0; i < 6; i++)
            await SeedAsync(new MeshNode($"write-{i}", ns)
            {
                NodeType = GraphNodeTypeNames.Activity, Name = $"Write {i}", State = MeshNodeState.Active,
                LastModified = now.AddMinutes(-i),
                Content = new ActivityLog("DataUpdate") { Id = $"write-{i}" },
            }, ct);
        (await SettleAsync(Children(ns), rows => rows.Count == 11, ct)).Should().HaveCount(11);

        // A window of 2 + 2 + 1 = 5 rows: without the category filter in the query, the five newest
        // rows are all the other writer's and no compile is ever seen.
        var policy = CompileActivityRetention.Default with { KeepLast = 2, MaxDeletionsPerRun = 2 };
        var removed = await CompileActivityRetention
            .Prune(Mesh, owner, null, policy, logger: null)
            .FirstAsync().Timeout(Bound).Await(ct);

        removed.Should().Be(2, "the window holds only compile activities, so the oldest two beyond the newest two go");
    }

    [Fact(Timeout = 180000)]
    public async Task Negative_control_with_retention_off_the_history_is_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = $"{TestPartition}/UnprunedType";
        var ns = $"{owner}/_Activity";
        await SeedAsync(new MeshNode("UnprunedType", TestPartition)
        {
            NodeType = "Markdown", Name = "UnprunedType", State = MeshNodeState.Active,
        }, ct);
        foreach (var row in History(ns, DateTimeOffset.UtcNow))
            await SeedAsync(row, ct);
        (await SettleAsync(Children(ns), rows => rows.Count == 25, ct)).Should().HaveCount(25);

        var removed = await CompileActivityRetention
            .Prune(Mesh, owner, null, CompileActivityRetention.Default with { Enabled = false }, logger: null)
            .FirstAsync().Timeout(Bound).Await(ct);

        removed.Should().Be(0);
        // No positive signal to wait for: the prune has completed, so read once, now.
        (await SnapshotAsync(Children(ns), ct)).Should().HaveCount(25, "a disabled policy removes nothing");
    }

    // ------------------------------------------------------------------------------ plumbing

    private static string Children(string ns) => $"namespace:{ns} limit:all";

    private IObservable<IReadOnlyCollection<MeshNode>> Snapshot(string query)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => (IReadOnlyCollection<MeshNode>)c.Items.ToArray())
            .Take(1);
    }

    private Task<IReadOnlyCollection<MeshNode>> SnapshotAsync(string query, CancellationToken cancellationToken)
        => Snapshot(query).FirstAsync().Timeout(Bound).Await(cancellationToken);

    /// <summary>Re-reads on an interval until the condition holds; on timeout returns the final snapshot.</summary>
    private async Task<IReadOnlyCollection<MeshNode>> SettleAsync(
        string query, Func<IReadOnlyCollection<MeshNode>, bool> predicate, CancellationToken cancellationToken)
    {
        try
        {
            return await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .SelectMany(_ => Snapshot(query))
                .Where(predicate)
                .FirstAsync().Timeout(Bound).Await(cancellationToken);
        }
        catch (TimeoutException)
        {
            return await SnapshotAsync(query, cancellationToken);
        }
    }

    private Task SeedAsync(MeshNode node, CancellationToken cancellationToken)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => mesh.CreateNode(node))
            .FirstAsync().Timeout(Bound).Await(cancellationToken);
    }
}
