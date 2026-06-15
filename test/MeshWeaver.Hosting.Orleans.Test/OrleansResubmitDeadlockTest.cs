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
/// Isolated replica of the resubmit-deadlock scenario, split out of
/// <see cref="OrleansNodeChangePropagationTest"/> so it no longer shares a test
/// class with <c>Delegation_NodeChanges_PropagateFromSubThread</c>.
///
/// <para><b>Why a separate class.</b> Each Orleans test method already boots its
/// OWN cluster + resets the process-wide statics (see
/// <see cref="OrleansSharedTestBase"/> / <see cref="SharedOrleansFixture.ResetSharedState"/>),
/// so thread ids and grain state never collide. What two <c>[Fact]</c>s in the
/// SAME class still share is the <em>process</em>: the heavy delegation chain in
/// <c>Delegation_NodeChanges_PropagateFromSubThread</c> leaves background async
/// work (sub-thread hubs, heartbeat tickers, streaming continuations on the
/// thread pool) that can still be draining when the next in-class test starts,
/// slowing its scheduling enough to surface timing-sensitive reactive paths.
/// Replicating this test into its own class keeps it from running back-to-back
/// with that delegation test — the empirical "passes alone, flakes right after
/// Delegation" reproducer. The resubmit test uses only the default
/// <c>FakeChatClientFactory</c> (it never swaps the factory), so it needs none of
/// the delegation chat-client fakes.</para>
/// </summary>
public class OrleansResubmitDeadlockTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"resubmit-{name}-{Guid.NewGuid():N}", "TestUser");

    private async Task<string> CreateNode(IMessageHub client, MeshNode node, string targetAddress)
    {
        Output.WriteLine($"CreateNodeRequest: id={node.Id}, path={node.Path}, target={targetAddress}");
        var response = await client.Observe(new CreateNodeRequest(node), o => o.WithTarget(new Address(targetAddress)))
            .Should().Within(45.Seconds()).Emit();
        Output.WriteLine($"CreateNodeResponse: success={response.Message.Success}, error={response.Message.Error ?? "(none)"}, path={response.Message.Node?.Path ?? "(null)"}, nodeType={response.Message.Node?.NodeType ?? "(null)"}");
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        return response.Message.Node!.Path!;
    }

    private IObservable<IReadOnlyList<string>> ObserveThreadMessages(IMessageHub client, string threadPath)
    {
        return client.GetMeshNodeStream(threadPath)
            .Select(node =>
            {
                var content = node?.Content as MeshThread;
                var ids = content?.Messages ?? [];
                Output.WriteLine($"[Stream] Thread {threadPath}: {ids.Count} message IDs");
                return (IReadOnlyList<string>)ids;
            });
    }

    // Canonical CQRS-correct LIVE read via the per-node MeshNode stream.
    private IObservable<T?> GetContent<T>(IMessageHub client, string path) where T : class
        => client.GetWorkspace().GetMeshNodeStream(path)
            .Select(node =>
            {
                if (node?.Content is T typed) return typed;
                if (node?.Content is JsonElement contentJe)
                    return contentJe.Deserialize<T>(Fixture.ClientMesh.JsonSerializerOptions);
                return null;
            });

    /// <summary>
    /// Resubmit test: after execution completes, click "Resubmit" (ArrowSync).
    /// The HandleResubmitMessage handler must not deadlock — it uses
    /// meshService.CreateNode (Observable) + workspace.UpdateMeshNode (non-blocking).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Resubmit_AfterExecution_DoesNotDeadlock()
    {
        var client = GetClient();

        // 1. Create and execute a thread
        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", "Resubmit deadlock test", "TestUser");
        var threadPath = await CreateNode(client, threadNode, "TestUser");

        client.SubmitMessage(
            threadPath,
            "First message",
            contextPath: "TestUser");
        var msgIds = await ObserveThreadMessages(client, threadPath)
            .Should().Within(45.Seconds()).Match(ids => ids.Count >= 2);
        Output.WriteLine($"Initial messages: [{string.Join(", ", msgIds)}]");

        // Wait for execution to complete
        await GetContent<MeshThread>(client, threadPath)
            .Should().Within(45.Seconds()).Match(t => t?.IsExecuting == false);
        Output.WriteLine("Initial execution complete");

        // 2. Resubmit — sends ThreadSubmission.ApplyResubmit to the thread grain.
        //    This was the original deadlock: the handler subscribed to workspace streams.
        //    Now uses Observable + workspace.UpdateMeshNode.
        // 🚨 Wait for the SETTLED state (IsExecuting=false AND msgIds changed),
        // not just any observable count>=2 emission. Mid-resubmit transitions
        // briefly show 3 messages [user, old-response, new-response] before the
        // truncate-then-re-add settles to [user, new-response] = 2.
        var workspace = client.GetWorkspace();

        Output.WriteLine("Resubmitting via workspace.ResubmitMessage...");
        client.ResubmitMessage(
            threadPath, msgIds[0],
            newUserText: "Resubmitted message");

        // Observe the SETTLED resubmitted state on the live replaying stream:
        // IsExecuting=false AND messages changed from the original set.
        var newThread = await workspace.GetMeshNodeStream(threadPath)
            .Select(node => node?.Content as MeshThread)
            .Do(t => Output.WriteLine(
                $"[Resubmit-wait] Status={t?.Status} IsExecuting={t?.IsExecuting} " +
                $"Msgs=[{string.Join(",", t?.Messages ?? [])}] " +
                $"Pending={t?.PendingUserMessages.Count} Ingested={t?.IngestedMessageIds.Count} " +
                $"Active={t?.ActiveMessageId}"))
            .Should().Within(45.Seconds()).Match(t => t is { IsExecuting: false }
                && t.Messages.Count >= 2
                && !t.Messages.SequenceEqual(msgIds));

        // 3. Wait for message IDs to change — if deadlocked, this times out
        var newMsgIds = (IReadOnlyList<string>)newThread!.Messages;
        newMsgIds.Should().HaveCount(2,
            "resubmit should keep user message and replace response");
        newMsgIds[0].Should().Be(msgIds[0], "user message should be preserved");
        newMsgIds[1].Should().NotBe(msgIds[1], "response should be a new cell");
        Output.WriteLine($"After resubmit: [{string.Join(", ", newMsgIds)}]");

        // 4. Resubmit succeeded — messages changed, no deadlock.
        Output.WriteLine("Resubmit completed — messages updated, no deadlock!");
    }
}
