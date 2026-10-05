using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Logon;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The home page reads its spaces from the viewer's OWN profile — <see cref="User.SpacePaths"/> and
/// <see cref="User.SharedPaths"/>, kept by <see cref="RefreshSpacePathsLogonAction"/> — and issues no
/// mesh-wide query on its render path.
///
/// <para>Measured on the public instance 2026-10-04: the home's root leg
/// (<c>namespace: … partitions:all</c>) and its shared-targets read were cross-schema UNIONs over
/// 251 partition schemas on every render, and their relation locks queued every anchored read behind
/// them. Maintainer: "installed apps must be in manifest on user's home. only this must be read …
/// rest must be slow, page must load quickly".</para>
/// </summary>
public class HomeSpaceManifestTest
{
    private const string Owner = "alice";

    private static IEnumerable<string> HomeQueries(IReadOnlyList<string>? spaces, IReadOnlyList<string>? shared)
    {
        var user = new User { SpacePaths = spaces ?? [], SharedPaths = shared ?? [] };
        var apps = UserActivityLayoutAreas.BuildAppsBand(Owner, null, shared, spaces);
        var content = UserActivityLayoutAreas.BuildContentSection(Owner, null, user, null, null, shared, spaces);
        return new[] { apps, content }
            .SelectMany(c => new[] { c.HiddenQuery?.ToString() }
                .Concat(c.ScopeTabs?.Select(t => t.Query) ?? [])
                .Concat(c.ScopeTabs?.SelectMany(t => t.SortOptions?.Select(o => o.Query) ?? []) ?? [])
                .Concat(c.SortOptions?.Select(o => o.Query) ?? []))
            .Where(q => !string.IsNullOrEmpty(q))
            .Select(q => q!);
    }

    [Fact]
    public void No_home_query_fans_out_across_partitions()
    {
        foreach (var (spaces, shared) in new (IReadOnlyList<string>?, IReadOnlyList<string>?)[]
                 {
                     (null, null), ([], []), (["Acme"], null), (["Acme", "Globex"], ["Initech/Module"]),
                 })
            HomeQueries(spaces, shared).Should().AllSatisfy(q =>
                q.Should().NotContain(ParsedQuery.CrossPartitionQualifier,
                    "the home reads only the viewer's own partition and the paths its profile lists"));
    }

    [Fact]
    public void The_root_leg_is_the_profiles_space_manifest()
    {
        var all = UserActivityLayoutAreas
            .BuildContentSection(Owner, null, null, null, null, null, ["Acme", "Globex"])
            .ScopeTabs!.Single(t => t.Label == "All");

        all.Query.Should().Contain("path:Acme|Globex is:main is:content nodeType:Space");
        all.Query.Should().Contain($"namespace:{Owner} is:main");
        all.Query.Should().NotContain("namespace: is:main");
    }

    [Fact]
    public void An_empty_manifest_omits_the_root_leg_rather_than_falling_back_to_a_fan_out()
    {
        var all = UserActivityLayoutAreas
            .BuildContentSection(Owner, null, null, null, null, null, [])
            .ScopeTabs!.Single(t => t.Label == "All");

        all.Query.Split('\n').Should().ContainSingle("only the viewer's own leg is left")
            .Which.Should().StartWith($"namespace:{Owner} ");

        UserActivityLayoutAreas.SpacesQuery(Owner, [], []).Should().Be(
            $"namespace:{Owner} is:main is:content nodeType:Space sort:LastModified-desc",
            "the Spaces scope falls back to the viewer's own home, never to every partition");
    }

    [Fact]
    public void BuildHome_reads_the_manifest_from_the_profile()
    {
        // The legacy single-list style returns the list control itself, which makes the profile →
        // query wiring of BuildHome observable without unpacking the stack.
        var catalog = new HomeConfig { Style = HomeStyle.Catalog };
        var user = new User { SpacePaths = ["Acme", "Globex"] };

        UserActivityLayoutAreas.BuildHome(Owner, catalog, user: user)
            .Should().BeOfType<MeshSearchControl>().Subject
            .HiddenQuery!.ToString().Should().Contain("path:Acme|Globex ");
    }

    [Fact]
    public void The_presentation_screen_drops_a_marked_space_from_the_query()
    {
        // The manifest is interpolated into the query string, which the search view exposes in its
        // options editor and in "open in search" — so a marked name must not reach it (#1803).
        var catalog = new HomeConfig { Style = HomeStyle.Catalog };
        var user = new User { SpacePaths = ["Acme", "Globex"] };

        var query = UserActivityLayoutAreas
            .BuildHome(Owner, catalog, user: user, screen: PresentationScreen.For(true, ["Acme"]))
            .Should().BeOfType<MeshSearchControl>().Subject
            .HiddenQuery!.ToString()!;

        query.Should().NotContain("Acme").And.Contain("path:Globex ");
    }

    [Fact]
    public void The_refresh_runs_on_every_logon_under_a_stable_id()
    {
        var action = new RefreshSpacePathsLogonAction();
        action.Mode.Should().Be(LogonActionMode.EveryLogon, "spaces and grants appear after the first logon");
        action.Id.Should().Be("platform.refresh-space-paths");
    }

    [Fact]
    public void The_space_manifest_lists_partition_roots_and_never_the_owners_home()
    {
        var roots = new[]
        {
            new MeshNode("Globex"), new MeshNode("acme"), new MeshNode(Owner), new MeshNode("ALICE"),
            new MeshNode("Deep", "Acme"), new MeshNode("Acme"),
        };

        RefreshSpacePathsLogonAction.SpacePathsFrom(roots, Owner)
            .Should().Equal(["acme", "Globex"], "roots only, owner excluded, distinct, sorted");
    }

    [Fact]
    public void A_fresh_answer_writes_both_lists()
    {
        var user = new User { SpacePaths = ["Old"], SharedPaths = [] };

        var updated = RefreshSpacePathsLogonAction.Apply(user, ["Acme", "Globex"], ["Initech/Module"]);

        updated.SpacePaths.Should().Equal("Acme", "Globex");
        updated.SharedPaths.Should().Equal("Initech/Module");
    }

    [Fact]
    public void An_unchanged_answer_writes_nothing()
    {
        // The runner skips the write when the change returns the SAME instance — an every-logon action
        // must cost no write in the steady state.
        var user = new User { SpacePaths = ["Globex", "Acme"], SharedPaths = ["Initech/Module"] };

        RefreshSpacePathsLogonAction.Apply(user, ["Acme", "Globex"], ["initech/module"])
            .Should().BeSameAs(user, "order and case do not make a list different");

        var outcome = RefreshSpacePathsLogonAction.Decide(["globex", "acme"], ["Initech/Module"]);
        outcome.ProfileChange!.Invoke(user).Should().BeSameAs(user);
    }
}
