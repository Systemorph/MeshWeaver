using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
    private CancellationToken TestTimeout => CancellationTokenSource.CreateLinkedTokenSource(
        new CancellationTokenSource(15.Seconds()).Token,
        TestContext.Current.CancellationToken).Token;

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

        Mesh.Post(new TrackActivityRequest(
            NodePath: nodePath,
            UserId: user,
            NodeName: "My Doc",
            NodeType: "Markdown",
            Namespace: "alice"));

        var node = await PollForFirstAsync(
            $"namespace:{user}/_UserActivity nodeType:UserActivity",
            TestTimeout);

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
    public async Task TrackActivity_WithEmailShapedUserId_IsRejected()
    {
        const string emailUser = "bob@example.com";

        Mesh.Post(new TrackActivityRequest(
            NodePath: emailUser,
            UserId: emailUser,
            NodeName: "Bob",
            NodeType: "User",
            Namespace: ""));

        // Give the handler a window to either skip (correct) or produce a
        // malformed node (the regression). No node should ever materialise
        // — the handler logs a warning and returns.
        await Task.Delay(2_000, TestTimeout);

        var any = await MeshQuery
            .QueryAsync<MeshNode>("nodeType:UserActivity")
            .ToListAsync();

        any.Should().NotContain(n => n.Path != null && n.Path.Contains('@'),
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

        // Fire 5 concurrent requests for the SAME path. Under the buggy
        // probe-and-fork shape, all 5 see the path-not-found probe before
        // any of them succeeds in writing → all 5 attempt CreateNode → 4
        // throw "Node already exists".
        var tasks = new Task[5];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                Mesh.Post(new TrackActivityRequest(
                    NodePath: nodePath,
                    UserId: user,
                    NodeName: "Concurrent Doc",
                    NodeType: "Markdown",
                    Namespace: user));
            }, TestTimeout);
        }

        await Task.WhenAll(tasks);

        var node = await PollForFirstAsync(
            $"namespace:{user}/_UserActivity nodeType:UserActivity",
            TestTimeout);
        node.Should().NotBeNull("at least one concurrent TrackActivityRequest must land a node");

        // After settling, exactly one record per (user, encodedPath) — concurrent
        // tracks merge into the same record's AccessCount, not duplicate records.
        var all = await MeshQuery
            .QueryAsync<MeshNode>($"namespace:{user}/_UserActivity nodeType:UserActivity")
            .ToListAsync();
        all.Should().HaveCount(1,
            "concurrent tracks for the same path must coalesce into one record, not race-create duplicates");
    }

    /// <summary>
    /// Polls the mesh query layer until at least one result arrives or the
    /// token cancels. Each first-emission of the synced collection settles
    /// fast; this poll re-queries every 200ms to surface the entry that
    /// the activity handler's asynchronous create/update produced.
    /// </summary>
    // Stream-based poll: re-issue the MeshQuery on a 50 ms interval until at
    // least one node matches, capped by the caller's cancellation. Replaces
    // a `while + Task.Delay(200)` loop with an Observable.Interval + Where +
    // FirstAsync — the cancellation token threads through ToTask, so the
    // operation cleanly cancels when the test framework's timeout fires.
    private async Task<MeshNode?> PollForFirstAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            return await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .SelectMany(_ => Observable.FromAsync(async () =>
                    await MeshQuery.QueryAsync<MeshNode>(query).ToListAsync()))
                .Where(list => list.Count > 0)
                .Select(list => list[0])
                .FirstAsync()
                .ToTask(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
