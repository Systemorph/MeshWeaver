using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Deterministic repro for the cross-hub patch message-loss defect.
///
/// <para>The owner-side <see cref="PatchDataRequest"/> apply
/// (<c>DataExtensions.ApplyJsonMergePatchAndUpdate</c>) used to read the merge base at
/// HANDLER time and DEFER the write to a separate <c>workspace.RequestChange</c> turn. Two
/// patches that queue back-to-back at the owner each read pre-the-other's-write state, then the
/// later FULL-NODE write (<c>collection.Merge({id: merged})</c>) replaced the entity wholesale —
/// DROPPING the first patch's just-added field. For a thread node that means a queued user
/// message vanishes from <see cref="MeshThread.PendingUserMessages"/> +
/// <see cref="MeshThread.UserMessageIds"/> entirely — the CI-only flake behind
/// <c>InboxToolIntegrationTest.Cancel_WithPendingMessages</c> /
/// <c>RapidSubmits_PileUpAndAllIngest</c> / the Hammer repro.</para>
///
/// <para>This pins the cause WITHOUT agent-round timing: two raw <see cref="PatchDataRequest"/>s
/// are posted to the owner back-to-back, both diffed off the SAME pre-u2 snapshot — exactly what
/// the <c>IMeshNodeStreamCache</c>'s optimistic per-path queue produces when a submit is followed
/// by a cancel/field-edit before the submit's owner-commit echo arrives. The fixed (atomic) apply
/// merges each patch onto the LIVE node at the write turn, so the queued message survives. The
/// thread is seeded <see cref="ThreadExecutionStatus.Done"/> so the submission watcher and
/// stale-round recovery stay dormant and cannot themselves move the message.</para>
/// </summary>
public class CrossHubPatchAtomicityTest : AITestBase
{
    public CrossHubPatchAtomicityTest(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task ConcurrentCrossHubPatches_DoNotDropAQueuedMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{MonolithMeshTestBase.TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        var u1 = Guid.NewGuid().AsString();
        var r1 = Guid.NewGuid().AsString();

        // Seed a settled thread (Status=Done) so the submission watcher's dispatch
        // (Idle/Cancelled + pending) and the Executing-round recovery both stay dormant —
        // we isolate the framework's cross-hub patch apply, not a dispatch round.
        await NodeFactory.CreateNode(MeshNode.FromPath(threadPath) with
        {
            Name = $"Patch Atomicity Thread {threadId}",
            NodeType = ThreadNodeType.NodeType,
            MainNode = MonolithMeshTestBase.TestPartition,
            Content = new MeshThread
            {
                CreatedBy = "rbuergi@systemorph.com",
                Status = ThreadExecutionStatus.Done,
                Messages = ImmutableList.Create(u1, r1),
                UserMessageIds = ImmutableList.Create(u1),
                IngestedMessageIds = ImmutableList.Create(u1)
            }
        }).FirstAsync().ToTask(ct);

        // Read the committed node from the owner — the common base BOTH patches diff off.
        var baseNode = await ReadNode(threadPath).FirstAsync().ToTask(ct);
        baseNode.Should().NotBeNull();
        var baseThread = baseNode!.Content as MeshThread;
        baseThread.Should().NotBeNull("seeded thread must round-trip as typed MeshThread content");

        var jsonOpts = Mesh.JsonSerializerOptions;
        var baseJson = (JsonObject)JsonSerializer.SerializeToNode(baseNode, jsonOpts)!;

        // Patch A — a follow-up user message lands in PendingUserMessages (+ UserMessageIds).
        var u2 = Guid.NewGuid().AsString();
        var msgU2 = new ThreadMessage { Role = "user", Text = "Second", CreatedBy = "rbuergi@systemorph.com" };
        var afterA = baseThread! with
        {
            UserMessageIds = ImmutableList.Create(u1, u2),
            PendingUserMessages = ImmutableDictionary<string, ThreadMessage>.Empty.SetItem(u2, msgU2)
        };
        var patchA = ComputeMergePatchDiff(
            baseJson, (JsonObject)JsonSerializer.SerializeToNode(baseNode with { Content = afterA }, jsonOpts)!);

        // Patch B — a concurrent field edit (the ESC / cancel) diffed off the SAME pre-u2 base.
        var afterB = baseThread with { Summary = "concurrent-edit" };
        var patchB = ComputeMergePatchDiff(
            baseJson, (JsonObject)JsonSerializer.SerializeToNode(baseNode with { Content = afterB }, jsonOpts)!);

        var accessCtx = new AccessContext { ObjectId = "rbuergi@systemorph.com", Name = "rbuergi@systemorph.com" };

        // Post BOTH to the owner back-to-back (no await between) so they queue adjacently and the
        // owner processes both before either's write lands — the exact ordering that lost u2.
        Mesh.Post(new PatchDataRequest(new MeshNodeReference(), new RawJson(patchA.ToJsonString(jsonOpts))),
            o => o.WithTarget(new Address(threadPath)).WithAccessContext(accessCtx));
        Mesh.Post(new PatchDataRequest(new MeshNodeReference(), new RawJson(patchB.ToJsonString(jsonOpts))),
            o => o.WithTarget(new Address(threadPath)).WithAccessContext(accessCtx));

        // Wait until BOTH patches have landed (Summary applied). The queued message MUST then
        // still be present — the atomic apply merges each patch onto live state, never
        // full-replaces from a handler-time snapshot.
        var settled = await Mesh.GetWorkspace().GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is not null && t!.Summary == "concurrent-edit")
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask(ct);

        settled!.PendingUserMessages.Should().ContainKey(u2,
            "a queued user message must survive a concurrent cross-hub patch — the owner-side apply "
            + "must merge each patch onto LIVE state, never full-replace from a handler-time snapshot");
        settled.UserMessageIds.Should().Contain(u2,
            "the queued message's id must not be clobbered out of UserMessageIds by the concurrent patch");
    }

    // RFC 7396 merge-patch diff — the same algorithm the client's
    // MeshNodeStreamHandle.UpdateRemote ships on the wire. Replicated here so the test computes
    // both patches off ONE base; the OWNER applies them through the real
    // DataExtensions.ApplyJsonMergePatchAndUpdate path under test.
    private static JsonObject ComputeMergePatchDiff(JsonObject current, JsonObject updated)
    {
        var patch = new JsonObject();
        foreach (var (key, updatedValue) in updated)
        {
            var currentValue = current[key];
            if (currentValue is JsonObject co && updatedValue is JsonObject uo)
            {
                var sub = ComputeMergePatchDiff(co, uo);
                if (sub.Count > 0)
                    patch[key] = sub;
                continue;
            }
            if (!JsonNode.DeepEquals(currentValue, updatedValue))
                patch[key] = updatedValue?.DeepClone();
        }
        foreach (var (key, _) in current)
            if (!updated.ContainsKey(key))
                patch[key] = null;
        return patch;
    }
}
