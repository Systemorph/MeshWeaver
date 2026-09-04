using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Logon;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Notification retention (Systemorph/MeshWeaver#3250): the policy that decides what has expired,
/// the query shape that keeps a sweep bounded, and the logon action that applies both.
///
/// <para>The four properties these cases exist to pin, in the order a reviewer should read them:
/// what the pass REMOVES, what it KEEPS, that one run is BOUNDED (and a backlog still drains), and
/// that running it again changes nothing. The boundedness case is the one most likely to regress
/// silently — a cap dropped from the query, or the sweep quietly widened to span partitions, would
/// leave every other case here green.</para>
/// </summary>
public class NotificationRetentionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Every wait in this class. <see cref="TestTimeouts.Convergence"/> rather than a hand-written
    /// half-minute: a literal is a guess about how fast a machine is, CI is ~1.7x slower than the
    /// laptop it would have been guessed on, and that particular literal is ALSO the framework's own
    /// late-response bound — so a test that gives up there reports "nothing emitted" instead of the
    /// verdict the mesh was about to hand it. <c>TestTimeoutLiteralRatchetGuard</c> enforces this.
    /// </summary>
    private static TimeSpan Bound => TestTimeouts.Convergence;

    private static MeshNode Notification(string path, DateTimeOffset lastModified) =>
        MeshNode.FromPath(path) with
        {
            NodeType = NotificationNodeType.NodeType,
            Name = "Something happened",
            State = MeshNodeState.Active,
            // 🚨 Preserved by the create pipeline (`LastModified == default ? now : …`), which is
            // what lets a test seed a genuinely OLD row instead of racing a clock.
            LastModified = lastModified,
            Content = new Notification
            {
                Title = "Something happened",
                Message = "…",
                CreatedAt = lastModified,
                NotificationType = NotificationType.General,
            },
        };

    // ---------------------------------------------------------------- the policy, as a pure rule

    [Fact]
    public void A_notification_past_the_window_is_expired_and_one_inside_it_is_kept()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = NotificationRetention.Default;

        policy.IsExpired(Notification("u/_Notification/old", now.AddDays(-200)), now)
            .Should().BeTrue("200 days is well past the 90-day default window");
        policy.IsExpired(Notification("u/_Notification/new", now.AddDays(-1)), now)
            .Should().BeFalse("yesterday's notification is still worth showing");
        policy.IsExpired(Notification("u/_Notification/edge", now - policy.MaxAge), now)
            .Should().BeTrue("the boundary itself is expired — the window is inclusive");
    }

    [Fact]
    public void Everything_uncertain_resolves_to_KEEPING_the_row()
    {
        var now = DateTimeOffset.UtcNow;
        var ancient = now.AddDays(-2000);

        NotificationRetention.Default.IsExpired(null, now)
            .Should().BeFalse("a missing node is not a deletion target");
        NotificationRetention.Default.IsExpired(
                Notification("u/Docs/spec", ancient) with { NodeType = "Markdown" }, now)
            .Should().BeFalse("this rule deletes; it re-checks the type rather than trusting its caller");
        NotificationRetention.Default.IsExpired(
                Notification("u/_Notification/undated", ancient) with { LastModified = default }, now)
            .Should().BeFalse(
                "an undated row cannot be aged — and default(DateTimeOffset) is BEFORE every cutoff, "
                + "so treating it as a date would delete exactly the rows storage failed to stamp");
        (NotificationRetention.Default with { Enabled = false })
            .IsExpired(Notification("u/_Notification/old", ancient), now)
            .Should().BeFalse("a disarmed policy expires nothing");
    }

    [Fact]
    public void The_shipped_defaults_are_armed_ninety_days_and_capped()
    {
        // Pinned deliberately: these three are the deployment contract the chart renders, and a
        // silent change to any of them changes what a portal deletes on its next roll.
        NotificationRetention.Default.Enabled.Should().BeTrue(
            "a retention pass that ships disarmed reproduces the defect it was written for");
        NotificationRetention.Default.MaxAge.Should().Be(TimeSpan.FromDays(90));
        NotificationRetention.Default.MaxDeletionsPerRun.Should().Be(200);
    }

    [Fact]
    public void Configuration_overrides_are_read_and_a_bad_one_never_widens_the_window()
    {
        NotificationRetention.FromConfiguration(Config(new()
        {
            [NotificationRetention.EnabledConfigKey] = "false",
            [NotificationRetention.MaxAgeConfigKey] = "30.00:00:00",
            [NotificationRetention.MaxDeletionsPerRunConfigKey] = "50",
        })).Should().Be(new NotificationRetention
        {
            Enabled = false, MaxAge = TimeSpan.FromDays(30), MaxDeletionsPerRun = 50,
        });

        NotificationRetention.FromConfiguration(Config(new()
        {
            [NotificationRetention.MaxAgeConfigKey] = "not-a-timespan",
            [NotificationRetention.MaxDeletionsPerRunConfigKey] = "0",
            [NotificationRetention.EnabledConfigKey] = "yes-please",
        })).Should().Be(NotificationRetention.Default,
            "an unparseable knob leaves the shipped default in place rather than guessing");

        // 🚨 The floor. `MaxAge: "0.00:00:00"` is a typo a chart consumer can make, and reading it
        // literally would empty every bell on the platform in one pass.
        NotificationRetention.FromConfiguration(Config(new()
            { [NotificationRetention.MaxAgeConfigKey] = "1.00:00:00" }))
            .MaxAge.Should().Be(NotificationRetention.MinimumMaxAge,
                "a configured window below the floor is clamped up to it");
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    // ------------------------------------------------------------- the query shape = the bound

    [Fact]
    public void The_sweep_reads_ONE_partition_with_a_cap_and_never_fans_out()
    {
        var parsed = new QueryParser().Parse(NotificationService.RetentionQuery("rbuergi", 200));

        // The bound, asserted on the thing that actually carries it.
        parsed.Limit.Should().Be(200, "the row cap IS the per-run deletion cap");
        parsed.Path.Should().Be("rbuergi",
            "a concrete path anchor is what pins the read to one partition schema");
        parsed.Scope.Should().Be(QueryScope.Descendants,
            "the sweep must reach legacy rows filed under entities, not only {addressee}/_Notification");
        parsed.CrossPartition.Should().BeFalse(
            "a retention pass must never become one statement over every schema on the server");
        parsed.ExtractNodeType().Should().Be(NotificationNodeType.NodeType,
            "the nodeType filter is what keeps the read on the notifications satellite table");
        parsed.OrderBy.Should().Be(new OrderByClause("LastModified", Descending: false),
            "oldest first — so the capped window holds the rows most likely to be expired, and a "
            + "backlog drains instead of the cap re-reading the same young page forever");
    }

    [Fact]
    public void The_no_fan_out_assertion_can_actually_fail()
    {
        // The negative control for the case above. Without it, `CrossPartition.Should().BeFalse()`
        // would also pass against a parser that never sets the flag at all.
        new QueryParser().Parse($"nodeType:Notification {ParsedQuery.CrossPartitionQualifier}")
            .CrossPartition.Should().BeTrue();
    }

    [Fact]
    public void The_sort_and_the_predicate_agree_on_ONE_timestamp()
    {
        // Ordering by one quantity and judging by another is how a "bounded" sweep stops draining:
        // the capped window would hold rows the policy never selects. Both are LastModified, and
        // this is the assertion that says so.
        new QueryParser().Parse(NotificationService.RetentionQuery("p", 1))
            .OrderBy!.Property.Should().Be(nameof(MeshNode.LastModified));
    }

    // ----------------------------------------------------------------------- the shipped wiring

    [Fact]
    public void The_pass_is_registered_so_a_portal_actually_runs_it()
    {
        // A merged action nobody resolves is an unreachable guard. AddNotificationType registers
        // both halves; this asserts the platform can find them.
        Mesh.ServiceProvider.GetServices<ILogonAction>()
            .Should().ContainSingle(a => a is NotificationRetentionLogonAction);
        Mesh.ServiceProvider.GetRequiredService<NotificationRetention>()
            .Should().Be(NotificationRetention.Default,
                "a test host configures no retention keys, so it runs the shipped policy");
    }

    [Fact]
    public void It_runs_on_EVERY_logon_because_notifications_keep_expiring()
    {
        // A run-once action would prune whatever was old the day it first ran, record itself as
        // done, and never look again — while the thing it prunes keeps arriving.
        new NotificationRetentionLogonAction(NotificationRetention.Default)
            .Mode.Should().Be(LogonActionMode.EveryLogon);
    }

    // --------------------------------------------------------------------------- end to end

    [Fact(Timeout = 180000)]
    public async Task It_removes_expired_notifications_addressed_and_legacy_and_keeps_the_recent_ones()
    {
        const string user = "retentionreader";
        var stale = DateTimeOffset.UtcNow.AddDays(-200);

        await SeedUserAsync(user);
        // The entity a pre-addressing notification was filed under. Real shape: before #3156 a
        // notification was a satellite of what it was ABOUT, not of who it was FOR.
        await SeedAsync(MeshNode.FromPath($"{user}/Report") with
        {
            NodeType = "Markdown", Name = "Report", State = MeshNodeState.Active,
        });

        await SeedAsync(Notification($"{user}/{NotificationService.SatelliteSegment}/addressed-old", stale));
        await SeedAsync(Notification($"{user}/Report/{NotificationService.SatelliteSegment}/legacy-old", stale));
        await SeedAsync(Notification(
            $"{user}/{NotificationService.SatelliteSegment}/recent", DateTimeOffset.UtcNow));

        // Precondition, asserted rather than assumed — the sweep reads through the query index, and
        // if the seeded rows are invisible there the real assertion below would pass having deleted
        // nothing, for a reason that has nothing to do with retention.
        var seeded = await SettleAsync(AllNotifications(user), rows => rows.Count == 3);
        Paths(seeded).Should().Be(
            $"{user}/Report/{NotificationService.SatelliteSegment}/legacy-old|"
            + $"{user}/{NotificationService.SatelliteSegment}/addressed-old|"
            + $"{user}/{NotificationService.SatelliteSegment}/recent",
            "all three seeded rows are visible to the query the sweep reads through");
        seeded.Single(n => n.Path.EndsWith("addressed-old", StringComparison.Ordinal))
            .LastModified.Should().BeCloseTo(stale, TimeSpan.FromMinutes(1),
                "the create pipeline preserves an explicitly-set LastModified — the whole test "
                + "depends on a seeded row genuinely being old");

        await RunRetentionAsync(user, NotificationRetention.Default);

        var afterFirst = await SettleAsync(AllNotifications(user), rows => rows.Count == 1);
        Paths(afterFirst).Should().Be(
            $"{user}/{NotificationService.SatelliteSegment}/recent",
            "both expired rows go — the addressed one AND the legacy one filed under the entity — "
            + "and the recent one stays");

        // Idempotence: the cutoff is a pure function of the clock, so a second run selects the same
        // (now absent) set, finds nothing, and leaves the kept row alone.
        await RunRetentionAsync(user, NotificationRetention.Default);
        var afterSecond = await SettleAsync(AllNotifications(user), rows => rows.Count == 1);
        Paths(afterSecond).Should().Be(
            $"{user}/{NotificationService.SatelliteSegment}/recent",
            "a repeat run is a no-op, not a second bite");
    }

    [Fact(Timeout = 180000)]
    public async Task One_run_never_exceeds_the_cap_and_the_backlog_still_drains()
    {
        const string user = "retentionbacklog";
        var stale = DateTimeOffset.UtcNow.AddDays(-200);
        var policy = NotificationRetention.Default with { MaxDeletionsPerRun = 2 };

        await SeedUserAsync(user);
        for (var i = 0; i < 5; i++)
            await SeedAsync(Notification(
                $"{user}/{NotificationService.SatelliteSegment}/backlog-{i}", stale.AddMinutes(i)));

        (await SettleAsync(AllNotifications(user), rows => rows.Count == 5)).Should().HaveCount(5);

        // 🚨 THE BOUNDEDNESS ASSERTION. Five rows are expired and every one of them is reachable in
        // a single statement; the cap is what stops one run taking them all. MEASURED: with BOTH
        // caps removed — `limit:` off the query AND the in-memory Take — one run empties the
        // partition and this reads 0 instead of 3. Either cap alone still holds the line, which is
        // why they are both here and why the query's `limit:` has its own assertion above: this
        // case cannot see a cap that has silently become redundant.
        await RunRetentionAsync(user, policy);
        (await SettleAsync(AllNotifications(user), rows => rows.Count == 3))
            .Should().HaveCount(3, "one run removes at most MaxDeletionsPerRun rows");

        // …and the backlog is not merely capped, it DRAINS: the window is ordered oldest-first, so
        // each run takes the next two rather than re-reading the same page.
        await RunRetentionAsync(user, policy);
        (await SettleAsync(AllNotifications(user), rows => rows.Count == 1))
            .Should().HaveCount(1, "successive runs make monotone progress");

        await RunRetentionAsync(user, policy);
        (await SettleAsync(AllNotifications(user), rows => rows.Count == 0))
            .Should().BeEmpty("the tail is gone after ceil(5/2) runs");
    }

    [Fact(Timeout = 180000)]
    public async Task A_disarmed_policy_deletes_nothing()
    {
        const string user = "retentiondisarmed";
        await SeedUserAsync(user);
        await SeedAsync(Notification(
            $"{user}/{NotificationService.SatelliteSegment}/ancient", DateTimeOffset.UtcNow.AddDays(-2000)));
        (await SettleAsync(AllNotifications(user), rows => rows.Count == 1)).Should().HaveCount(1);

        await RunRetentionAsync(user, NotificationRetention.Default with { Enabled = false });

        // A negative case with no positive signal to wait for: the run has completed, so the only
        // thing left to establish is that it wrote nothing. Read once, now.
        (await SnapshotAsync(AllNotifications(user)))
            .Should().HaveCount(1, "Enabled:false makes the pass a complete no-op");
    }

    // ------------------------------------------------------------------------------ plumbing

    private static string AllNotifications(string user)
        => $"path:{user} scope:descendants nodeType:{NotificationNodeType.NodeType} limit:all";

    /// <summary>Runs ONLY this action, through the real runner — same ordering, identity scope and
    /// budget the platform uses; discovery is the one thing skipped, so a case sees its own action
    /// rather than everything the mesh happens to have registered.</summary>
    private Task RunRetentionAsync(string user, NotificationRetention policy)
    {
        var runner = Mesh.ServiceProvider.GetRequiredService<LogonActionRunner>();
        return runner.RunFor(
                new AccessContext { ObjectId = user, Name = user },
                [new NotificationRetentionLogonAction(policy)])
            .FirstAsync().Timeout(Bound).Await();
    }

    /// <summary>The row paths, ordered and joined — a legible one-line failure message.</summary>
    private static string Paths(IEnumerable<MeshNode> rows)
        => string.Join("|", rows.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal));

    /// <summary>One snapshot of a query.</summary>
    private IObservable<IReadOnlyCollection<MeshNode>> Snapshot(string query)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Select(c => (IReadOnlyCollection<MeshNode>)c.Items.ToArray())
            .Take(1);
    }

    private Task<IReadOnlyCollection<MeshNode>> SnapshotAsync(string query)
        => Snapshot(query).FirstAsync().Timeout(Bound).Await();

    /// <summary>
    /// Re-reads on an interval until <paramref name="predicate"/> holds — a wait on the CONDITION,
    /// never a sleep. On timeout it returns the final snapshot instead of throwing, so the caller's
    /// assertion names the rows that are actually there rather than reporting a bare
    /// <c>TimeoutException</c> with nothing in it.
    /// </summary>
    private async Task<IReadOnlyCollection<MeshNode>> SettleAsync(
        string query, Func<IReadOnlyCollection<MeshNode>, bool> predicate)
    {
        try
        {
            // The catch covers this awaited task's fault, which surfaces here synchronously — it is
            // not a try/catch wrapped around a Subscribe, which would see nothing.
            return await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .SelectMany(_ => Snapshot(query))
                .Where(predicate)
                .FirstAsync().Timeout(Bound).Await();
        }
        catch (TimeoutException)
        {
            return await SnapshotAsync(query);
        }
    }

    /// <summary>Seeds the user partition root as System — reserved to the platform by UserNodeType's
    /// access rule, exactly as onboarding does it.</summary>
    private Task SeedUserAsync(string user) => SeedAsync(MeshNode.FromPath(user) with
    {
        NodeType = "User", Name = user, State = MeshNodeState.Active, Content = new User(),
    });

    private Task SeedAsync(MeshNode node)
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => mesh.CreateNode(node))
            .FirstAsync().Timeout(Bound).Await();
    }
}
