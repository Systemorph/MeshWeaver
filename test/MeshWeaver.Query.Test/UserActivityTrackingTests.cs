using System;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
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
    public async Task TrackActivity_WithUsername_CreatesNodeAtExpectedPath()
    {
        const string user = "alice";
        const string nodePath = "alice/MyDoc";

        // ONBOARD-FIRST gate (HandleTrackActivity, commit 981a86c9e): the first-time
        // create is skipped unless the user's partition root already exists. Production
        // always has it (onboarding ran first); reproduce that precondition here.
        await OnboardPartitionRoot(user);

        var activityPath = $"{user}/_UserActivity/{nodePath.Replace("/", "_")}";

        // 🚨 Same shape as ActivityTrackingHubTest (#993): the handler DETACHES its write, so wait
        // on the write's own completion rather than polling the eventually-consistent query index,
        // then read authoritatively. Subscribe before the Post — WhenSettled needs the ENTER.
        var tracker = Mesh.GetActivityTrackingHub()
            .ServiceProvider.GetRequiredService<ActivityWriteTracker>();
        var settled = tracker.WhenSettled(activityPath).Replay(1);
        using var settledSubscription = settled.Connect();

        Mesh.Post(new TrackActivityRequest(
            NodePath: nodePath,
            UserId: user,
            NodeName: "My Doc",
            NodeType: "Markdown",
            Namespace: "alice"));

        await settled.Should().Within(TimeSpan.FromSeconds(15)).Emit(
            "the detached activity write must run to completion");

        var node = await ReadNode(activityPath).Should().Match(n => n is not null,
            "a UserActivity node must be created at {user}/_UserActivity after a TrackActivityRequest");
        node!.Path.Should().Be(activityPath);
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
    public async Task TrackActivity_WithEmailShapedUserId_IsRejected()
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
        await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery("nodeType:UserActivity"))
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
    public async Task TrackActivity_ConcurrentSamePath_DoesNotRaceAlreadyExists()
    {
        const string user = "charlie";
        const string nodePath = "charlie/doc";
        const int concurrentTracks = 5;

        // ONBOARD-FIRST gate (HandleTrackActivity, commit 981a86c9e): activity tracking
        // never creates a partition ahead of onboarding. Seed the partition root so the
        // gate lets the concurrent creates through (production state when activity flows).
        await OnboardPartitionRoot(user);

        var activityPath = $"{user}/_UserActivity/{nodePath.Replace("/", "_")}";

        // 🚨 Wait for ALL FIVE detached writes, not for the first node to surface (issue #1036).
        //
        // The handler detaches each write, so the five Posts below return while five pipelines are
        // still running. The previous shape waited for the first node to appear in the
        // EVENTUALLY-CONSISTENT query index and then asserted the coalescing — so writes still in
        // flight at that moment were simply not covered, and a duplicate-create regression could
        // pass unnoticed. That is a FALSE PASS in a regression test, which is worse than a flake:
        // it removes the guard without removing the green tick.
        //
        // WhenSettled(path) alone cannot close it either: it completes on the FIRST ENTER→LEAVE
        // cycle, and five posts need not overlap — post 1 can finish before post 2 begins. The
        // COUNTED overload spans every cycle: it completes only once five writes for this path have
        // each been through ENTER→LEAVE. Five writes, ONE node — the tracker counts the writes
        // (each accepted request registers exactly one Begin, whichever branch it takes: create,
        // update-fold, or the create→already-exists race fold), and the coalescing into a single
        // node is precisely what the assertions below check.
        //
        // Subscribe BEFORE the Posts: the signal counts the ENTERs it observes, so a subscription
        // that attaches after a write already finished can never count it (it then times out loudly
        // — the opposite of Drain(), which completes immediately on an idle tracker and would be a
        // deterministic false pass here).
        var tracker = Mesh.GetActivityTrackingHub()
            .ServiceProvider.GetRequiredService<ActivityWriteTracker>();
        var settled = tracker.WhenSettled(activityPath, writes: concurrentTracks).Replay(1);
        using var settledSubscription = settled.Connect();

        // Fire 5 requests for the SAME path. Under the buggy probe-and-fork
        // shape, all 5 see the path-not-found probe before any of them succeeds
        // in writing → all 5 attempt CreateNode → 4 throw "Node already exists".
        for (var i = 0; i < concurrentTracks; i++)
        {
            Mesh.Post(new TrackActivityRequest(
                NodePath: nodePath,
                UserId: user,
                NodeName: "Concurrent Doc",
                NodeType: "Markdown",
                Namespace: user));
        }

        await settled.Should().Within(TimeSpan.FromSeconds(15)).Emit(
            "all five detached activity writes must run to completion before their combined " +
            "result can be asserted — see the MeshWeaver.Graph.ActivityTracking trace for the " +
            "stage a stalled write last reached");

        // Read AUTHORITATIVELY — the writes have terminated, so the node either exists or the
        // handler skipped/failed every create. No eventual consistency in this assertion.
        var node = await ReadNode(activityPath).Should().Match(n => n is not null,
            "the concurrent TrackActivityRequests must land a UserActivity node at the shared path");
        node!.NodeType.Should().Be("UserActivity");

        // 🚨 The coalescing itself, and the assertion that actually pins the "Node already exists"
        // race: all five tracks folded their increment into the ONE record. Pre-fix, four of the
        // five CreateNodes threw and their increments were lost, leaving AccessCount = 1 — while
        // the node COUNT stayed 1 either way (the path is the storage key), so a count-only
        // assertion never saw the bug it was written for. Read off the authoritative node, not the
        // query index. The tracking hub's JSON options are the ones that know UserActivityRecord.
        var record = node.ContentAs<UserActivityRecord>(
            Mesh.GetActivityTrackingHub().JsonSerializerOptions);
        record.Should().NotBeNull("the UserActivity node must carry a typed UserActivityRecord");
        record!.AccessCount.Should().Be(concurrentTracks,
            "every concurrent track must fold its increment onto the live record — a lost increment " +
            "means a write raced 'Node already exists' instead of coalescing via stream.Update");

        // …and exactly one record per (user, encodedPath): they merged into one node rather than
        // race-creating siblings. A listing is a legitimate query use; it is only sound because
        // every write has already terminated above.
        var all = (await MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{user}/_UserActivity nodeType:UserActivity"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;
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
    private async Task OnboardPartitionRoot(string user)
    {
        // User-node creation is restricted to portal/own-scope identities (the
        // UserNodeType portal-create rule). Impersonate as the user so the
        // RlsNodeValidator own-scope bypass (nodePath == userId) lets the
        // partition-root create through — the shape production hits when the user
        // owns their just-created partition. The activity posted afterwards is then
        // an own-scope write under {user}/_UserActivity, also allowed.
        Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .SetHostIdentity(new AccessContext { ObjectId = user, Name = user });

        await NodeFactory.CreateNode(new MeshNode(user)
        {
            NodeType = "User",
            Name = user,
            State = MeshNodeState.Active,
        }).Should().Emit();

        // The gate reads the root from storage; await wait for the owner-hub round-trip to
        // confirm persistence before posting activity so the create branch isn't skipped.
        await ReadNode(user).Should().Match(n => n is { State: MeshNodeState.Active });
    }
}
