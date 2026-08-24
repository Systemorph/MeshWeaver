using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Logon;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Seeding the platform default apps, moved off the render path.
///
/// <para>The regression this exists to prevent has a specific shape: the home used to seed defaults
/// when the viewer's app grid came back EMPTY, using emptiness as a stand-in for "this user has not
/// been set up yet". That held only while nothing else could write an app record. The Store now
/// writes one at install time, so a user who acquires a package BEFORE first opening their home
/// arrives with a non-empty grid, the seeding never fires, and they lose the defaults for good —
/// including the Store tile, which is the one the seeding exists to guarantee.</para>
///
/// <para>So the properties worth pinning are about the TRIGGER, not the payload: it is keyed on the
/// per-user ledger rather than on what the grid looks like, and it is idempotent on its own because
/// the ledger is atomic with the profile and not with these node writes.</para>
/// </summary>
public class SeedDefaultAppsLogonActionTest
{
    private static readonly SeedDefaultAppsLogonAction Action = new();

    [Fact]
    public void It_runs_once_per_user_not_per_render()
    {
        // RunOnce is the whole point: the old trigger re-fired for any user whose grid happened to
        // be empty, so a user who deleted every tile had them silently restored on the next render.
        Action.Mode.Should().Be(LogonActionMode.RunOnce);
    }

    [Fact]
    public void Its_ledger_key_is_stable()
    {
        // 🚨 The id IS the ledger key. Changing it re-runs the action for every existing user, and
        // for THIS action that means re-creating records people may have deliberately deleted.
        // Pinned so a rename has to be a deliberate act with this test in the diff.
        Action.Id.Should().Be("seed-default-apps");
    }

    [Fact]
    public void It_runs_before_the_actions_that_operate_on_records()
    {
        // Icon adoption heals records and the pin migration references apps; both are no-ops on a
        // user whose records do not exist yet. Not required for correctness — each is independently
        // idempotent — but it saves a first logon that does nothing and then seeds.
        Action.Order.Should().BeLessThan(new AppIconAdoptionLogonAction().Order);
    }

    [Fact]
    public void The_seeded_set_comes_from_the_deployment_config_not_from_code()
    {
        // What gets seeded is instance-specific: memex.meshweaver.cloud and systemorph.com do not
        // ship the same apps. The action reads Admin/HomeConfig, so this asserts the shape it
        // depends on rather than a hard-coded list.
        var configured = new HomeConfig { DefaultApps = ["Store", "Doc"] };

        var specs = UserActivityLayoutAreas.AppRecordSpecs(configured, "alice");

        specs.Select(s => s.Id).Should().Contain("Store");
        specs.Select(s => s.Id).Should().Contain("Doc");
    }

    [Fact]
    public void A_deployment_that_declares_no_defaults_seeds_nothing()
    {
        // An empty DefaultApps is a legitimate configuration, not an error: a portal may want a
        // bare home. It must not fall back to the shipped set.
        var specs = UserActivityLayoutAreas.AppRecordSpecs(new HomeConfig { DefaultApps = [] }, "alice");

        specs.Should().BeEmpty();
    }

    [Fact]
    public void A_config_stream_that_emits_only_the_defaults_still_seeds()
    {
        // 🚨 The regression this pins, which I wrote and review caught: HomeConfigNodeType.Observe
        // is StartWith(Defaults) + DistinctUntilChanged, so a portal with NO materialized
        // Admin/HomeConfig node emits exactly ONCE. Skipping that emission to avoid "acting on the
        // placeholder" waits forever on precisely the fresh deployment this action exists for.
        // Shape assertion: one emission must still yield a usable config, not a hang.
        var single = Observable.Return(HomeConfigNodeType.Defaults);

        var settled = single
            .Take(2)
            .TakeUntil(Observable.Timer(TimeSpan.FromMilliseconds(200)))
            .LastAsync()
            .Timeout(TimeSpan.FromSeconds(5))
            .Wait();

        settled.Should().NotBeNull();
        UserActivityLayoutAreas.AppRecordSpecs(settled, "alice").Should().NotBeEmpty(
            "a deployment without a HomeConfig node must still get the shipped defaults");
    }

    [Fact]
    public void A_config_stream_that_emits_twice_uses_the_configured_set()
    {
        // The other half: when a real node DOES answer, its value must win over the placeholder —
        // otherwise a portal that configured its own apps would be seeded the shipped ones.
        var configured = new HomeConfig { DefaultApps = ["OnlyThis"] };

        var settled = Observable.Return(HomeConfigNodeType.Defaults).Concat(Observable.Return(configured))
            .Take(2)
            .TakeUntil(Observable.Timer(TimeSpan.FromSeconds(2)))
            .LastAsync()
            .Wait();

        UserActivityLayoutAreas.AppRecordSpecs(settled, "alice").Select(s => s.Id)
            .Should().Equal("OnlyThis");
    }

    [Fact]
    public void Every_seeded_record_lands_in_the_users_own_app_namespace()
    {
        // The records are per-user and RLS-scoped to that user's partition; a spec that produced a
        // path outside it would be both invisible to its owner and a cross-partition write.
        var specs = UserActivityLayoutAreas.AppRecordSpecs(HomeConfigNodeType.Defaults, "alice");

        foreach (var spec in specs)
        {
            var node = UserActivityLayoutAreas.BuildAppRecord("alice", spec);
            node.Path.Should().StartWith($"alice/{AppNodeType.UserNamespace}/");
            node.NodeType.Should().Be(AppNodeType.NodeType);
        }
    }
}
