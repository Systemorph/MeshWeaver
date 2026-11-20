using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Retention;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The configurable data retention (<c>Doc/Architecture/DataRetention</c>): the per-class rules as
/// pure functions, the configuration edge, the per-partition rule chain, and the sweep end to end
/// through a real mesh — what it removes, what it must never remove, and a negative control.
/// </summary>
public class DataRetentionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static TimeSpan Bound => TestTimeouts.Convergence;
    private static readonly JsonSerializerOptions PlainOptions = new();
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static MeshNode Activity(string ns, string id, DateTimeOffset at, ActivityStatus status,
        string category = ActivityCategory.DataUpdate, bool inbox = false) =>
        new(id, ns)
        {
            NodeType = GraphNodeTypeNames.Activity,
            Name = id,
            State = MeshNodeState.Active,
            LastModified = at,
            Content = new ActivityLog(category) { Id = id, Status = status, ShowInInbox = inbox },
        };

    // --------------------------------------------------------------------- the activity class

    [Fact]
    public void An_activity_goes_only_when_it_is_finished_old_and_not_owned_elsewhere()
    {
        var policy = new ActivityRetention { MaxAge = TimeSpan.FromDays(30) };
        var old = Now.AddDays(-31);

        policy.IsExpired(Activity("a/_Activity", "done", old, ActivityStatus.Succeeded), Now, PlainOptions)
            .Should().BeTrue("a finished activity past the window is what the class removes");
        policy.IsExpired(Activity("a/_Activity", "failed", old, ActivityStatus.Failed), Now, PlainOptions)
            .Should().BeTrue("a failure nobody is asked to look at ages out like any other run");

        policy.IsExpired(Activity("a/_Activity", "young", Now.AddDays(-29), ActivityStatus.Succeeded), Now, PlainOptions)
            .Should().BeFalse("inside the window");
        policy.IsExpired(Activity("a/_Activity", "running", old, ActivityStatus.Running), Now, PlainOptions)
            .Should().BeFalse("an unfinished activity is never removed, however old");
        policy.IsExpired(Activity("a/_Activity", "compile", old, ActivityStatus.Succeeded, ActivityCategory.Compilation), Now, PlainOptions)
            .Should().BeFalse("compile activities belong to CompileActivityRetention");
        policy.IsExpired(Activity("a/_Activity", "needs-you", old, ActivityStatus.Failed, inbox: true), Now, PlainOptions)
            .Should().BeFalse("a failed inbox activity is someone's open item");
        policy.IsExpired(Activity("a/_Activity", "undated", default, ActivityStatus.Succeeded), Now, PlainOptions)
            .Should().BeFalse("an undated row cannot be aged");
        policy.IsExpired(new MeshNode("x", "a") { NodeType = "Markdown", LastModified = old }, Now, PlainOptions)
            .Should().BeFalse("not an activity");
        (policy with { Enabled = false })
            .IsExpired(Activity("a/_Activity", "done", old, ActivityStatus.Succeeded), Now, PlainOptions)
            .Should().BeFalse("a switched-off class removes nothing");
    }

    [Fact]
    public void A_log_entry_goes_only_when_it_is_of_the_declared_type_and_old()
    {
        var policy = new LogEntryRetention { MaxAge = TimeSpan.FromDays(30) };
        var entry = new MeshNode("e1", "Ops/Logs/q") { NodeType = LogEntryRetention.DefaultNodeType, LastModified = Now.AddDays(-31) };

        policy.IsExpired(entry, Now).Should().BeTrue();
        policy.IsExpired(entry with { LastModified = Now.AddDays(-29) }, Now).Should().BeFalse("inside the window");
        policy.IsExpired(entry with { NodeType = "Hosting/InstanceAction" }, Now).Should().BeFalse("another type");
        policy.IsExpired(entry with { LastModified = default }, Now).Should().BeFalse("undated");
    }

    // ---------------------------------------------------------------------- the version rule

    private static IEnumerable<VersionHistoryRow> NodeHistory(string path, int count, DateTimeOffset newestAt, TimeSpan step)
        => Enumerable.Range(1, count).Select(v => new VersionHistoryRow(path, v, newestAt - step * (count - v)));

    [Fact]
    public void The_version_rule_keeps_the_newest_K_the_window_and_the_state_at_the_cutoff()
    {
        // 30 versions, one a day, the newest today: v30 today … v1 29 days ago. Cutoff 10 days ago.
        var rows = NodeHistory("p/n", 30, Now, TimeSpan.FromDays(1)).ToList();
        var prune = new VersionHistoryPrune(Now.AddDays(-10), KeepLast: 5, MaxRows: 1000);

        var selected = prune.Select(rows).Select(r => r.Version).ToList();

        // Inside the window: v21..v30 (written after the cutoff, v20 is exactly 10 days old and
        // before it only if strictly less; it equals the cutoff, so it is NOT before it and stays).
        // State at the cutoff: the newest strictly before it, v19. Everything older goes.
        selected.Should().Equal(Enumerable.Range(1, 18).Select(v => (long)v),
            "versions inside the window, the newest 5 and the state at the cutoff stay; the rest go oldest first");
    }

    [Fact]
    public void The_version_rule_never_removes_the_current_version_even_with_KeepLast_zero()
    {
        var rows = NodeHistory("p/n", 3, Now.AddDays(-400), TimeSpan.FromDays(1)).ToList();
        var prune = new VersionHistoryPrune(Now, KeepLast: 0, MaxRows: 1000);

        var selected = prune.Select(rows).Select(r => r.Version).ToList();

        selected.Should().NotContain(3, "the newest version is the current one");
        selected.Should().Equal(1L, 2L);
    }

    [Fact]
    public void The_version_rule_is_capped_oldest_first_across_nodes_and_keeps_undated_rows()
    {
        var rows = NodeHistory("p/a", 10, Now.AddDays(-100), TimeSpan.FromDays(1))
            .Concat(NodeHistory("p/b", 10, Now.AddDays(-50), TimeSpan.FromDays(1)))
            .Append(new VersionHistoryRow("p/a", 0, default))
            .ToList();
        var prune = new VersionHistoryPrune(Now.AddDays(-10), KeepLast: 1, MaxRows: 3);

        var selected = prune.Select(rows);

        selected.Should().HaveCount(3, "the cap");
        selected.Select(r => (r.Path, r.Version)).Should().Equal(("p/a", 1L), ("p/a", 2L), ("p/a", 3L));
        selected.Should().NotContain(r => r.LastModified == default);
    }

    [Fact]
    public void Negative_control_a_history_entirely_inside_the_window_loses_nothing()
        => new VersionHistoryPrune(Now.AddDays(-90), KeepLast: 1, MaxRows: 1000)
            .Select(NodeHistory("p/hot", 2564, Now, TimeSpan.FromMinutes(5)))
            .Should().BeEmpty("2564 versions written over nine days are all inside a 90-day window");

    // ------------------------------------------------------------------ the configuration edge

    [Fact]
    public void Configuration_sets_each_class_and_can_never_widen_below_the_floors()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Retention:Activities:MaxAge"] = "30.00:00:00",
            ["Retention:Versions:MaxAge"] = "0.00:00:00",
            ["Retention:Versions:KeepLast"] = "0",
            ["Retention:LogEntries:Enabled"] = "false",
            ["Retention:LogEntries:MaxDeletionsPerRun"] = "not-a-number",
            ["Retention:Interval"] = "00:00:01",
        }).Build();

        var policy = DataRetentionPolicy.FromConfiguration(config);

        policy.Activities.MaxAge.Should().Be(TimeSpan.FromDays(30), "the owner's 30 days");
        policy.Versions.MaxAge.Should().Be(VersionRetention.MinimumMaxAge, "a zero window is a typo, clamped");
        policy.Versions.KeepLast.Should().Be(VersionRetention.MinimumKeepLast, "the current version always stays");
        policy.LogEntries.Enabled.Should().BeFalse();
        policy.LogEntries.MaxDeletionsPerRun.Should().Be(new LogEntryRetention().MaxDeletionsPerRun, "malformed keeps the shipped bound");
        policy.Interval.Should().Be(DataRetentionPolicy.MinimumInterval);
        DataRetentionPolicy.FromConfiguration(null).Should().Be(DataRetentionPolicy.Default);
    }

    // ------------------------------------------------------------------- the per-partition chain

    private sealed class Rule(string name, int order, Func<string, IObservable<DataRetentionOverride?>> resolve) : IDataRetentionRule
    {
        public string Name => name;
        public int Order => order;
        public IObservable<DataRetentionOverride?> Resolve(string partition) => resolve(partition);
    }

    [Fact]
    public void Each_class_is_resolved_independently_rule_first_then_the_instance()
    {
        var instance = DataRetentionPolicy.Default with { Activities = new ActivityRetention { MaxAge = TimeSpan.FromDays(60) } };
        var resolver = new DataRetentionResolver(instance,
        [
            new Rule("late", 20, _ => Observable.Return<DataRetentionOverride?>(new("tier:Late")
                { Activities = new ActivityRetention { MaxAge = TimeSpan.FromDays(999) } })),
            new Rule("tier", 10, p => Observable.Return<DataRetentionOverride?>(p == "free"
                ? new DataRetentionOverride("tier:Free") { Activities = new ActivityRetention { MaxAge = TimeSpan.FromDays(7) } }
                : null)),
        ]);

        var free = resolver.Fold("free", [new DataRetentionOverride("tier:Free") { Activities = new ActivityRetention { MaxAge = TimeSpan.FromDays(7) } }]);
        free.Activities.MaxAge.Should().Be(TimeSpan.FromDays(7));
        free.ActivitiesSource.Should().Be("tier:Free");
        free.VersionsSource.Should().Be(DataRetentionResolver.InstanceSource, "the tier did not speak for versions");
        free.LogEntriesSource.Should().Be(DataRetentionResolver.InstanceSource);

        resolver.Rules.Select(r => r.Name).Should().Equal("tier", "late");
        var none = resolver.Fold("paid", []);
        none.Activities.MaxAge.Should().Be(TimeSpan.FromDays(60), "no rule speaks: the instance policy");
    }

    [Fact]
    public void Versions_are_kept_at_least_as_long_as_activities_so_every_retained_activity_can_be_undone()
    {
        var instance = DataRetentionPolicy.Default with
        {
            Activities = new ActivityRetention { MaxAge = TimeSpan.FromDays(120) },
            Versions = new VersionRetention { MaxAge = TimeSpan.FromDays(30) },
        };

        new DataRetentionResolver(instance, []).Fold("p", []).Versions.MaxAge.Should().Be(TimeSpan.FromDays(120));
    }

    // --------------------------------------------------------------------------- end to end

    [Fact(Timeout = 180000)]
    public async Task The_sweep_removes_exactly_the_expired_rows_through_the_mesh_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-45);
        var ns = $"{TestPartition}/Owner/_Activity";
        var logs = $"{TestPartition}/Ops/Logs";
        await SeedAsync(new MeshNode("Owner", TestPartition) { NodeType = "Markdown", Name = "Owner", State = MeshNodeState.Active }, ct);
        await SeedAsync(new MeshNode("Ops", TestPartition) { NodeType = "Markdown", Name = "Ops", State = MeshNodeState.Active }, ct);
        await SeedAsync(new MeshNode("Logs", $"{TestPartition}/Ops") { NodeType = "Markdown", Name = "Logs", State = MeshNodeState.Active }, ct);

        // Removable: two old finished activities, two old log rows.
        await SeedAsync(Activity(ns, "old-done", old, ActivityStatus.Succeeded), ct);
        await SeedAsync(Activity(ns, "old-cancelled", old, ActivityStatus.Cancelled), ct);
        await SeedAsync(LogRow(logs, "old-1", old), ct);
        await SeedAsync(LogRow(logs, "old-2", old), ct);
        // Kept: inside the window, still running, compile, open inbox failure, young log row.
        await SeedAsync(Activity(ns, "young-done", now.AddDays(-1), ActivityStatus.Succeeded), ct);
        await SeedAsync(Activity(ns, "old-running", old, ActivityStatus.Running), ct);
        await SeedAsync(Activity(ns, "compile-2026090100000000a1", old, ActivityStatus.Succeeded, ActivityCategory.Compilation), ct);
        await SeedAsync(Activity(ns, "old-needs-you", old, ActivityStatus.Failed, inbox: true), ct);
        await SeedAsync(LogRow(logs, "young-1", now.AddDays(-1)), ct);

        (await SettleAsync($"namespace:{ns}", rows => rows.Count == 6, ct)).Should().HaveCount(6);
        (await SettleAsync($"namespace:{logs}", rows => rows.Count == 3, ct)).Should().HaveCount(3);

        var policy = Policy(enabled: true);
        var outcome = await DataRetentionSweep
            .SweepPartition(Mesh.NodeOperationIssuingHub(), policy, versioned: true, now, logger: null)
            .FirstAsync().Timeout(Bound).Await(ct);

        outcome.Activities.Should().Be(2);
        outcome.LogEntries.Should().Be(2);
        outcome.Failures.Should().BeEmpty();

        (await SettleAsync($"namespace:{ns}", rows => rows.Count == 4, ct)).Select(n => n.Id).Order(StringComparer.Ordinal)
            .Should().Equal(new[] { "compile-2026090100000000a1", "old-needs-you", "old-running", "young-done" }.Order(StringComparer.Ordinal),
                "only the old finished activities left");
        (await SettleAsync($"namespace:{logs}", rows => rows.Count == 1, ct)).Select(n => n.Id)
            .Should().Equal("young-1");
        (await SnapshotAsync($"path:{TestPartition}/Ops/Logs", ct)).Should().HaveCount(1, "the parent the rows sit under is not a log entry");

        var again = await DataRetentionSweep
            .SweepPartition(Mesh.NodeOperationIssuingHub(), policy, versioned: true, now, logger: null)
            .FirstAsync().Timeout(Bound).Await(ct);
        (again.Activities + again.LogEntries).Should().Be(0, "a second pass over a pruned partition removes nothing");
    }

    [Fact(Timeout = 180000)]
    public async Task Negative_control_with_every_class_switched_off_nothing_is_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var ns = $"{TestPartition}/Quiet/_Activity";
        await SeedAsync(new MeshNode("Quiet", TestPartition) { NodeType = "Markdown", Name = "Quiet", State = MeshNodeState.Active }, ct);
        await SeedAsync(Activity(ns, "ancient", now.AddDays(-2000), ActivityStatus.Succeeded), ct);
        await SeedAsync(LogRow($"{TestPartition}/Quiet", "ancient-log", now.AddDays(-2000)), ct);
        (await SettleAsync($"namespace:{ns}", rows => rows.Count == 1, ct)).Should().HaveCount(1);

        var outcome = await DataRetentionSweep
            .SweepPartition(Mesh.NodeOperationIssuingHub(), Policy(enabled: false), versioned: true, now, logger: null)
            .FirstAsync().Timeout(Bound).Await(ct);

        (outcome.Activities + outcome.Versions + outcome.LogEntries).Should().Be(0);
        // No positive signal to wait for: the sweep has completed, so read once, now.
        (await SnapshotAsync($"namespace:{ns}", ct)).Should().HaveCount(1, "a switched-off class removes nothing");
        (await SnapshotAsync($"path:{TestPartition}/Quiet/ancient-log", ct)).Should().HaveCount(1);
    }

    [Fact(Timeout = 180000)]
    public async Task A_rule_that_cannot_answer_skips_its_partition_instead_of_falling_back_to_the_instance()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var ns = $"{TestPartition}/Paid/_Activity";
        await SeedAsync(new MeshNode("Paid", TestPartition) { NodeType = "Markdown", Name = "Paid", State = MeshNodeState.Active }, ct);
        await SeedAsync(Activity(ns, "old-done", now.AddDays(-45), ActivityStatus.Succeeded), ct);
        (await SettleAsync($"namespace:{ns}", rows => rows.Count == 1, ct)).Should().HaveCount(1);

        var resolver = new DataRetentionResolver(
            DataRetentionPolicy.Default with { Activities = new ActivityRetention { MaxAge = TimeSpan.FromDays(30) } },
            [new Rule("broken-tier", 0, _ => Observable.Throw<DataRetentionOverride?>(new InvalidOperationException("tier store down")))]);

        var outcomes = await DataRetentionSweep
            .RunPass(Mesh.NodeOperationIssuingHub(), resolver, [new RetentionPartition(TestPartition, true)], now, logger: null)
            .FirstAsync().Timeout(Bound).Await(ct);

        outcomes.Should().ContainSingle().Which.SkipReason.Should().NotBeNull();
        (await SnapshotAsync($"namespace:{ns}", ct)).Should().HaveCount(1,
            "the rule might keep this partition's data longer, so an unanswered rule removes nothing");
    }

    [Fact(Timeout = 180000)]
    public async Task The_mesh_registers_the_policy_and_the_sweep_lists_its_partitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Mesh.ServiceProvider.GetRequiredService<DataRetentionResolver>();
        resolver.Instance.Should().Be(DataRetentionPolicy.Default, "no Retention:* key is configured in the test host");
        Mesh.ServiceProvider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<DataRetentionHostedService>().Should().ContainSingle("AddGraph registers the sweep once");

        var partitions = await DataRetentionSweep.ListPartitions(Mesh.NodeOperationIssuingHub())
            .FirstAsync().Timeout(Bound).Await(ct);
        partitions.Should().NotBeEmpty("the sweep visits what Admin/Partition lists");
        partitions.Should().OnlyContain(p => p.Name.Length > 0 && !p.Name.Contains('/'));
    }

    // ------------------------------------------------------------------------------ plumbing

    private static ResolvedDataRetention Policy(bool enabled) => new(
        TestPartition,
        new ActivityRetention { Enabled = enabled, MaxAge = TimeSpan.FromDays(30) }, "test",
        new VersionRetention { Enabled = enabled, MaxAge = TimeSpan.FromDays(30) }, "test",
        // Markdown stands in for Hosting/LogEntry, which is a Plugins type this mesh does not know.
        new LogEntryRetention { Enabled = enabled, MaxAge = TimeSpan.FromDays(30), NodeType = "Markdown" }, "test");

    private static MeshNode LogRow(string ns, string id, DateTimeOffset at) => new(id, ns)
    {
        NodeType = "Markdown", Name = id, State = MeshNodeState.Active, LastModified = at,
    };

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
