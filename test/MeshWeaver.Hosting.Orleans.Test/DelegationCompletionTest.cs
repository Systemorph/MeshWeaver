using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Tests that a SubmitMessageRequest returns TWO SubmitMessageResponse messages:
/// 1. Status=CellsCreated (cells created, execution starting)
/// 2. Status=ExecutionCompleted (agent finished, response text available)
///
/// This is critical for delegation: the parent thread's RegisterCallback
/// waits for the second response to resolve the delegation TCS.
/// Without it, the parent thread hangs forever after delegation.
///
/// TODO(append-migration): SubmitMessageRequest still used because this test
/// specifically validates the dual-response (CellsCreated + ExecutionCompleted)
/// semantic of the legacy submit pipeline. The new AppendUserMessageRequest API
/// returns a single Success/Error response and the agent's response text lives
/// only on the response satellite cell — there's no equivalent of the second
/// completion response for this test to assert against. Internal production code
/// (thread hub → _Exec sub-hub) still uses SubmitMessageRequest with this dual
/// response semantic, so the underlying behaviour is still worth exercising
/// while the legacy contract is in place.
/// </summary>
[Collection(nameof(OrleansClusterCollection))]
public class DelegationCompletionTest(SharedOrleansFixture fixture, ITestOutputHelper output) : TestBase(output)
{
    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await fixture.GetClientAsync($"completion-{name}-{Guid.NewGuid():N}", "Roland");

    /// <summary>
    /// Verifies that SubmitMessageRequest produces two responses:
    /// 1. CellsCreated (immediate)
    /// 2. ExecutionCompleted (after agent finishes streaming)
    ///
    /// The test uses RegisterCallback (same pattern as delegation tool)
    /// and collects all responses until ExecutionCompleted arrives.
    /// </summary>
    // TODO(append-migration): kept on SubmitMessageRequest — see class-level comment.
    [Fact(Timeout = 60000)]
    public async Task SubmitMessage_ReceivesBothCellsCreated_AndExecutionCompleted()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        // Create thread
        var response = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode("User/Roland", "Completion test", "Roland")),
            o => o.WithTarget(new Address("User/Roland")), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        var threadPath = response.Message.Node!.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        // Post SubmitMessageRequest and collect responses via RegisterCallback
        var responses = new List<(SubmitMessageStatus Status, bool Success, string? ResponseText)>();
        var completionTcs = new TaskCompletionSource<bool>();

        var delivery = client.Post(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Test completion notification",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(threadPath)));
        delivery.Should().NotBeNull("Post should return delivery");

        // RegisterCallback removes after first invocation — re-register for second response
        void RegisterForResponse(IMessageDelivery del)
        {
            _ = client.RegisterCallback(del, cb =>
            {
                if (cb is IMessageDelivery<SubmitMessageResponse> sr)
                {
                    var msg = sr.Message;
                    Output.WriteLine($"Response: Status={msg.Status}, Success={msg.Success}, Error={msg.Error}, TextLen={msg.ResponseText?.Length ?? 0}");
                    responses.Add((msg.Status, msg.Success, msg.ResponseText));

                    if (msg.Status == SubmitMessageStatus.CellsCreated)
                        RegisterForResponse(del); // Re-register for completion
                    else
                        completionTcs.TrySetResult(msg.Success);
                }
                else if (cb is IMessageDelivery<DeliveryFailure> df)
                {
                    Output.WriteLine($"DeliveryFailure: {df.Message.Message}");
                    completionTcs.TrySetResult(false);
                }
                return cb;
            });
        }
        RegisterForResponse((IMessageDelivery)delivery!);

        // Wait for execution to complete (with timeout)
        var timeoutTask = Task.Delay(45_000, ct);
        var completed = await Task.WhenAny(completionTcs.Task, timeoutTask);
        if (completed == timeoutTask)
        {
            Output.WriteLine($"TIMEOUT! Received {responses.Count} response(s): [{string.Join(", ", responses.Select(r => r.Status))}]");
        }
        completed.Should().Be(completionTcs.Task,
            "should receive ExecutionCompleted before timeout — parent thread would hang otherwise");

        // Verify we got both responses
        responses.Should().HaveCountGreaterThanOrEqualTo(2,
            "should receive CellsCreated + ExecutionCompleted");

        responses[0].Status.Should().Be(SubmitMessageStatus.CellsCreated,
            "first response should be CellsCreated");
        responses[0].Success.Should().BeTrue();

        var lastResponse = responses.Last();
        lastResponse.Status.Should().Be(SubmitMessageStatus.ExecutionCompleted,
            "final response should be ExecutionCompleted");
        lastResponse.Success.Should().BeTrue();
        lastResponse.ResponseText.Should().NotBeNullOrEmpty(
            "ExecutionCompleted should include the agent's response text");

        Output.WriteLine($"Delegation completion verified: {responses.Count} responses, final text length={lastResponse.ResponseText?.Length}");
    }
}
