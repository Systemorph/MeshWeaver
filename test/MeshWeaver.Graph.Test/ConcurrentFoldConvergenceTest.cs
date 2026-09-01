using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 N concurrent cross-hub folds onto ONE node: every writer reaches a terminal, and every
/// increment lands — issue #3001, the end-to-end half.
///
/// <para>This is the shape behind <c>TrackActivity_ConcurrentSamePath_DoesNotRaceAlreadyExists</c>
/// (MeshWeaver.Query.Test, in MeshWeaver.Plugins), whose handler —
/// <c>MeshNodeExtensions.HandleTrackActivity</c> — is CORE. Five tracks for one
/// <c>(userId, nodePath)</c> each fold an increment onto the live record inside
/// <c>stream.Update</c>; on CI one of the five never reached its DONE log and the counted settle
/// signal never completed. The guard for that belongs where the handler is.</para>
///
/// <para><b>Both halves of the issue are asserted here, and they are different claims.</b></para>
/// <list type="number">
///   <item><b>Every writer gets a VERDICT.</b> The cross-hub write path settles the caller from
///     inside its base read's <c>onNext</c>/<c>onError</c> callbacks only, so a base read that
///     completes with no value used to settle nothing — no value, no error, not even the outer
///     verdict deadline (which is armed inside the response wait a write with no base never
///     reaches). One writer of N silently never terminating is exactly "N started, N-1 finished".
///     The seam is pinned deterministically in <c>WriteBaseStateTotalityTest</c>; here it is
///     asserted end to end, on the real path, as "N writers ⇒ N terminals".</item>
///   <item><b>Every INCREMENT lands.</b> The fold is expressed inside the <c>Update</c> lambda
///     (read <c>AccessCount</c> off the LIVE node, write <c>+1</c>) rather than off a snapshot read
///     earlier, and the owner three-way-merges against the base the writer diffed: a writer whose
///     base the owner has moved past is REFUSED with <c>Conflict</c> and its lambda is re-run
///     against fresher state. So the value that must come out is exactly N — never fewer. A
///     "read 1, both write 2" lost update is what this number would catch.</item>
/// </list>
///
/// <para><b>Why the count, and not the node count.</b> The path IS the storage key, so N racing
/// writers produce ONE node either way — a count-only assertion never sees a lost increment. The
/// counter is the only quantity that distinguishes coalescing from clobbering.</para>
/// </summary>
public class ConcurrentFoldConvergenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Same width as the issue's trace: five concurrent tracks for one path.</summary>
    private const int Writers = 5;

    /// <summary>
    /// Comfortably above the write path's own outer bound (<c>LateResponseWatchBound</c> + grace =
    /// 31 s), so the FRAMEWORK's verdict wins the race and NAMES the cause instead of this test
    /// reporting a bare timeout. A shorter budget here would only re-report the hang as "the test
    /// timed out" — the mistake #2819 records.
    /// </summary>
    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(90);

    [Fact(Timeout = 180_000)]
    public async Task EveryConcurrentFold_ReachesATerminal_AndEveryIncrementLands()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();

        var id = "fold" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";

        await meshService.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Concurrent fold",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new UserActivityRecord
            {
                Id = id,
                NodePath = path,
                UserId = "fold-probe",
                ActivityType = ActivityType.Read,
                AccessCount = 0,
            },
        }).Take(1).Should().Within(60.Seconds()).Emit("the node the folds target must exist first");

        var stream = workspace.GetMeshNodeStream(path);

        // 🚨 Fold on the LIVE node inside the lambda — never off a snapshot read before it. The
        // owner serialises Updates, so each lambda sees the freshest count. This is the exact
        // shape HandleTrackActivity's FoldOntoLive uses.
        MeshNode Fold(MeshNode live)
        {
            var rec = live.ContentAs<UserActivityRecord>(Mesh.JsonSerializerOptions)
                      ?? new UserActivityRecord { Id = id, NodePath = path };
            return live with
            {
                Content = rec with
                {
                    AccessCount = rec.AccessCount + 1,
                    LastAccessedAt = DateTimeOffset.UtcNow,
                },
            };
        }

        // Every writer's TERMINAL, whatever it is — completion or error. Materialize keeps an error
        // from collapsing the merge, so a writer that FAILS is counted and reported rather than
        // taking the whole assertion down with it: "did every writer answer" and "did every writer
        // succeed" are separate questions and must be asked separately.
        var terminals = Enumerable.Range(0, Writers)
            .Select(_ => stream.Update(Fold)
                .Materialize()
                .Where(n => n.Kind != NotificationKind.OnNext)
                .Take(1))
            .ToArray();

        var settled = await Observable.Merge(terminals)
            .Take(Writers)
            .ToArray()
            .Should().Within(SettleBudget).Emit(
                $"all {Writers} concurrent folds must reach a terminal. A writer that reaches none "
                + "is the #3001 hang: the cross-hub write settles its caller only from inside its "
                + "base read's onNext/onError, so a base read that completes with NO value settles "
                + "nothing at all — no patch is posted, no deadline is armed, and nothing is "
                + "logged. It reads as 'N writes started, N-1 finished'.");

        var failures = settled.Where(n => n.Kind == NotificationKind.OnError).ToArray();
        failures.Should().BeEmpty(
            "every fold targets a node that exists on a live owner, so each must land: "
            + string.Join(" | ", failures.Select(f => f.Exception?.Message)));

        var final = await ReadNode(path).Should().Match(n => n is not null,
            "the folded node must still be readable after every writer has settled");

        var record = final!.ContentAs<UserActivityRecord>(Mesh.JsonSerializerOptions);
        record.Should().NotBeNull("the node must still carry a typed UserActivityRecord");
        record!.AccessCount.Should().Be(Writers,
            $"each of the {Writers} concurrent folds must land its increment on the ONE record. A "
            + "lower count is a LOST UPDATE — two writers read the same value and wrote the same "
            + "result — which the owner's three-way merge is supposed to refuse with Conflict so "
            + "the writer's lambda re-runs against fresher state. The node COUNT stays 1 either "
            + "way (the path is the storage key), so only this number can see it.");
    }
}
