using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Regression tests for <see cref="MeshWeaver.Graph.MeshNodeExtensions.HandleTrackActivity"/>.
///
/// Reproduces the live console pathology observed in
/// <c>Memex.Portal.Distributed</c> on 2026-05-10:
/// <list type="bullet">
///   <item><c>[ROUTE] NotFound: No node found at 'rbuergi@systemorph.com/_UserActivity/rbuergi@systemorph.com'</c>
///         — the email-shaped userId built a path with '@', which the Address
///         parser interprets as a hub-host separator. The path is
///         unaddressable; every TrackActivityRequest from a session whose
///         email→username resolution failed spammed this warning. The fix
///         skips activity tracking with a single warning rather than
///         producing unaddressable artefacts.</item>
///   <item><c>Failed to track activity ... Node already exists</c>
///         — two concurrent <see cref="TrackActivityRequest"/> for the same
///         encoded path both raced the <c>Take(1).Timeout(2s)</c> probe and
///         both fell through to <c>CreateNode</c>; one won, the other got
///         <c>InvalidOperationException("Node already exists")</c>. The fix
///         catches the "already exists" race and folds into the existing
///         record via <c>stream.Update</c>.</item>
/// </list>
/// </summary>
public class UserActivityTrackingTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Username-in-path is the contract: <c>userId/_UserActivity/encodedPath</c>
    /// where <c>userId</c> is the User MeshNode's <c>Id</c> (e.g. <c>"alice"</c>),
    /// never the email. A single TrackActivityRequest with a clean username
    /// must land a UserActivity node at the expected path.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public void TrackActivity_WithUsername_CreatesNodeAtExpectedPath()
    {
        const string user = "alice";
        const string nodePath = "alice/MyDoc";

        // ONBOARD-FIRST gate (HandleTrackActivity, commit 981a86c9e): the first-time
        // create is skipped unless the user's partition root already exists. Production
        // always has it (onboarding ran first); reproduce that precondition here.
        OnboardPartitionRoot(user);

        Mesh.Post(new TrackActivityRequest(
            NodePath: nodePath,
            UserId: user,
            NodeName: "My Doc",
            NodeType: "Markdown",
            Namespace: "alice"));

        var node = PollForFirst($"namespace:{user}/_UserActivity nodeType:UserActivity");

        node.Should().NotBeNull("a UserActivity node must be created at {user}/_UserActivity after a TrackActivityRequest");
        node!.Path.Should().Be($"{user}/_UserActivity/{nodePath.Replace("/", "_")}");
        node.NodeType.Should().Be("UserActivity");
    }

    /// <summary>
    /// REGRESSION: A TrackActivityRequest whose UserId contains '@' (e.g.
    /// because <c>UserContextMiddleware.TryLoadMeshUserAsync</c> failed to
    /// resolve email→username) must NOT produce a node whose path also
    /// contains '@' — the Address parser would mis-parse such a path. The
    /// handler must log a warning and skip.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public void TrackActivity_WithEmailShapedUserId_IsRejected()
    {
        const string emailUser = "bob@example.com";

        Mesh.Post(new TrackActivityRequest(
            NodePath: emailUser,
            UserId: emailUser,
            NodeName: "Bob",
            NodeType: "User",
            Namespace: ""));

        // No node with a '@'-shaped path should ever materialise — the handler
        // logs a warning and returns. Negative assertion: flatten the live
        // query's items and assert nothing matching arrives within the window.
        MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("nodeType:UserActivity"))
            .SelectMany(c => c.Items)
            .Where(n => n.Path != null && n.Path.Contains('@'))
            .Should().NotEmit(within: TimeSpan.FromSeconds(2),
                "tracking with an email-shaped userId must be skipped to avoid " +
                "the [ROUTE] NotFound floods observed in production. See " +
                "MeshNodeExtensions.HandleTrackActivity for the rejection guard.");
    }

    /// <summary>
    /// REGRESSION: Posting N TrackActivityRequest events for the same
    /// (userId, nodePath) pair concurrently must not log
    /// <c>"Node already exists"</c> errors. Before the fix, two simultaneous
    /// requests would both race the <c>Take(1).Timeout(2s)</c> probe, both
    /// fall through to <c>CreateNode</c>, and one would throw
    /// <c>InvalidOperationException</c>. The fix catches the race and
    /// folds via <c>stream.Update</c>.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void TrackActivity_ConcurrentSamePath_DoesNotRaceAlreadyExists()
    {
        const string user = "charlie";
        const string nodePath = "charlie/doc";

        // ONBOARD-FIRST gate (HandleTrackActivity, commit 981a86c9e): activity tracking
        // never creates a partition ahead of onboarding. Seed the partition root so the
        // gate lets the concurrent creates through (production state when activity flows).
        OnboardPartitionRoot(user);

        // Fire 5 requests for the SAME path. Under the buggy probe-and-fork
        // shape, all 5 see the path-not-found probe before any of them succeeds
        // in writing → all 5 attempt CreateNode → 4 throw "Node already exists".
        for (var i = 0; i < 5; i++)
        {
            Mesh.Post(new TrackActivityRequest(
                NodePath: nodePath,
                UserId: user,
                NodeName: "Concurrent Doc",
                NodeType: "Markdown",
                Namespace: user));
        }

        var node = PollForFirst($"namespace:{user}/_UserActivity nodeType:UserActivity");
        node.Should().NotBeNull("at least one concurrent TrackActivityRequest must land a node");

        // After settling, exactly one record per (user, encodedPath) — concurrent
        // tracks merge into the same record's AccessCount, not duplicate records.
        var all = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{user}/_UserActivity nodeType:UserActivity"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial).Items;
        all.Should().HaveCount(1,
            "concurrent tracks for the same path must coalesce into one record, not race-create duplicates");
    }

    /// <summary>
    /// Writes the user's partition-root <c>User</c> node (path = <c>{user}</c>, empty
    /// namespace) and waits until it is readable. <see cref="MeshWeaver.Graph.MeshNodeExtensions"/>'s
    /// <c>HandleTrackActivity</c> gate probes this root before its first-time create — absent
    /// root means the identity isn't onboarded and the activity write is skipped. Production
    /// onboarding (<c>UserOnboardingService.CreateUser</c>) always lands this row before any
    /// activity flows; these tests reproduce that precondition.
    /// </summary>
    private void OnboardPartitionRoot(string user)
    {
        // User-node creation is restricted to portal/own-scope identities (the
        // UserNodeType portal-create rule). Impersonate as the user so the
        // RlsNodeValidator own-scope bypass (nodePath == userId) lets the
        // partition-root create through — the shape production hits when the user
        // owns their just-created partition. The activity posted afterwards is then
        // an own-scope write under {user}/_UserActivity, also allowed.
        Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = user, Name = user });

        NodeFactory.CreateNode(new MeshNode(user)
        {
            NodeType = "User",
            Name = user,
            State = MeshNodeState.Active,
        }).Should().Emit();

        // The gate reads the root from storage; wait for the owner-hub round-trip to
        // confirm persistence before posting activity so the create branch isn't skipped.
        ReadNode(user).Should().Match(n => n is { State: MeshNodeState.Active });
    }

    /// <summary>
    /// Folds the live query's deltas into a running list and returns the first
    /// node the moment at least one matches — race-free replacement for the old
    /// <c>Observable.Interval</c> re-query poll. The activity handler's
    /// asynchronous create/update surfaces through the same change feed.
    /// </summary>
    private MeshNode? PollForFirst(string query)
        => MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan(ImmutableList<MeshNode>.Empty, (acc, c) =>
                c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset
                    ? c.Items.ToImmutableList()
                    : acc.AddRange(c.Items))
            .Where(list => list.Count > 0)
            .Select(list => list[0])
            .Should().Within(TimeSpan.FromSeconds(15)).Emit();
}
