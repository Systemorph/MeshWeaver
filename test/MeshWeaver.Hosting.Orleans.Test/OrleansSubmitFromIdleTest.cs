using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Submitting MORE chats from an already-IDLE thread is the suspected wedge: a thread
/// finishes a round (IsExecuting=false), then the user sends another chat (or several) —
/// and the re-execute-from-idle path / inbox two-stage drain must pick it up cleanly
/// rather than deadlocking or leaving messages stuck in PendingUserMessages forever.
///
/// <para>These tests drive exactly that and require the thread to reach a SETTLED idle
/// state — IsExecuting=false AND PendingUserMessages drained — within a bounded timeout.
/// A wedge (deadlock / never-drains) cannot produce that settled state, so it surfaces
/// here as a timeout. Complements <see cref="OrleansResubmitDeadlockTest"/> (which covers
/// a single Resubmit) with the multi-submit-from-idle scenarios.</para>
///
/// <para>Own class, per OrleansResubmitDeadlockTest's reasoning: each method boots its own
/// cluster + resets process statics, and a separate class keeps these off the back of the
/// heavy delegation test (the "passes alone, flakes right after Delegation" reproducer).
/// Uses only the default <c>FakeChatClientFactory</c> — no language model needed.</para>
/// </summary>
public class OrleansSubmitFromIdleTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"idle-submit-{name}-{Guid.NewGuid():N}", "TestUser");

    private async Task<string> CreateNode(IMessageHub client, MeshNode node, string targetAddress)
    {
        var response = await client.Observe(new CreateNodeRequest(node), o => o.WithTarget(new Address(targetAddress)))
            .Should().Within(45.Seconds()).Emit();
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        return response.Message.Node!.Path!;
    }

    // Canonical CQRS-correct LIVE read of the thread via the per-node MeshNode stream
    // (tolerant of an untyped JsonElement, like the sibling tests).
    private IObservable<MeshThread?> ThreadStream(IMessageHub client, string path)
        => client.GetWorkspace().GetMeshNodeStream(path)
            .Select(node =>
            {
                if (node?.Content is MeshThread t) return t;
                if (node?.Content is JsonElement je) return je.Deserialize<MeshThread>(Fixture.ClientMesh.JsonSerializerOptions);
                return null;
            });

    private void Log(string stage, MeshThread? t) => Output.WriteLine(
        $"[{stage}] Status={t?.Status} IsExecuting={t?.IsExecuting} Msgs={t?.Messages.Count} " +
        $"Pending={t?.PendingUserMessages.Count} Ingested={t?.IngestedMessageIds.Count} Active={t?.ActiveMessageId}");

    /// <summary>
    /// Submit THREE chats sequentially, each from the idle thread (submit → wait idle → submit …).
    /// Every round must reach settled idle and add a turn; a resubmit-from-idle wedge times out.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task SubmitFromIdle_ThreeSequential_EachReachesIdle()
    {
        var client = GetClient();
        var threadPath = await CreateNode(client,
            ThreadNodeType.BuildThreadNode("TestUser", "Idle submit sequential", "TestUser"), "TestUser");

        for (var round = 1; round <= 3; round++)
        {
            client.SubmitMessage(threadPath, $"Message {round}", contextPath: "TestUser");

            // The thread is idle BEFORE this submit (round>1); it must execute the new chat and
            // return to idle, having ingested `round` user messages and grown the conversation.
            var t = await ThreadStream(client, threadPath)
                .Do(x => Log($"after-submit-{round}", x))
                .Should().Within(60.Seconds())
                .Match(x => x is { IsExecuting: false }
                    && x.IngestedMessageIds.Count >= round
                    && x.PendingUserMessages.Count == 0
                    && x.Messages.Count >= round * 2);

            t!.PendingUserMessages.Should().BeEmpty($"round {round} from idle must fully drain the inbox");
            Output.WriteLine($"Round {round} from idle settled: {t.Messages.Count} messages, {t.IngestedMessageIds.Count} ingested.");
        }
    }

    /// <summary>
    /// Execute once to idle, then fire a RAPID BURST of three chats from idle (back-to-back, no
    /// waiting). The inbox two-stage drain must ingest + execute all three and settle idle with an
    /// empty PendingUserMessages — a drain wedge never settles and times out here.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task SubmitFromIdle_RapidBurst_DrainsAndSettlesIdle()
    {
        var client = GetClient();
        var threadPath = await CreateNode(client,
            ThreadNodeType.BuildThreadNode("TestUser", "Idle submit burst", "TestUser"), "TestUser");

        // Warm up to idle (one full round).
        client.SubmitMessage(threadPath, "Warm up", contextPath: "TestUser");
        await ThreadStream(client, threadPath)
            .Do(x => Log("warmup", x))
            .Should().Within(60.Seconds())
            .Match(x => x is { IsExecuting: false } && x.IngestedMessageIds.Count >= 1);

        // Burst: three submits from idle, back-to-back (the case the user suspects wedges).
        client.SubmitMessage(threadPath, "Burst 1", contextPath: "TestUser");
        client.SubmitMessage(threadPath, "Burst 2", contextPath: "TestUser");
        client.SubmitMessage(threadPath, "Burst 3", contextPath: "TestUser");

        // All four user messages (warmup + 3 burst) must be ingested, the inbox fully drained, and
        // the thread settled idle. If the burst-from-idle wedges, IngestedMessageIds never reaches 4
        // / PendingUserMessages never empties → this times out (the wedge, exposed).
        var settled = await ThreadStream(client, threadPath)
            .Do(x => Log("after-burst", x))
            .Should().Within(90.Seconds())
            .Match(x => x is { IsExecuting: false }
                && x.IngestedMessageIds.Count >= 4
                && x.PendingUserMessages.Count == 0);

        settled!.PendingUserMessages.Should().BeEmpty("the inbox must fully drain after a burst from idle (no stuck pending)");
        settled.Messages.Count.Should().BeGreaterThan(2, "the burst from idle must add turns beyond the warmup");
        Output.WriteLine($"Burst from idle settled: {settled.Messages.Count} messages, {settled.IngestedMessageIds.Count} ingested, pending drained.");
    }
}
