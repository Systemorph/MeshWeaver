using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Shared helper for thread tests. Implements the portal flow:
/// create cells (verified) → submit → wait for execution.
/// </summary>
public static class ThreadTestHelper
{
    /// <summary>
    /// Creates user + response cells, submits message, waits for execution to complete.
    /// Returns the response text.
    /// </summary>
    public static async Task<string> SubmitAndWaitAsync(
        IMessageHub client, string threadPath, string text,
        string contextPath, int expectedMsgCount, CancellationToken ct)
    {
        var userMsgId = Guid.NewGuid().ToString("N")[..8];
        var responseMsgId = Guid.NewGuid().ToString("N")[..8];

        // Create user cell → verify
        var userResp = await client.AwaitResponse(new CreateNodeRequest(new MeshNode(userMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = contextPath,
            Content = new ThreadMessage
            {
                Role = "user", Text = text, Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            }
        }), o => o.WithTarget(new Address(threadPath)), ct);
        if (!userResp.Message.Success)
            throw new InvalidOperationException($"User cell creation failed: {userResp.Message.Error}");

        // Create response cell → verify
        var responseResp = await client.AwaitResponse(new CreateNodeRequest(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType, MainNode = contextPath,
            Content = new ThreadMessage
            {
                Role = "assistant", Text = "", Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        }), o => o.WithTarget(new Address(threadPath)), ct);
        if (!responseResp.Message.Success)
            throw new InvalidOperationException($"Response cell creation failed: {responseResp.Message.Error}");

        // Submit (state update — WatchForExecution triggers execution)
        await client.AwaitResponse(new SubmitMessageRequest
        {
            ThreadPath = threadPath, UserMessageText = text, ContextPath = contextPath,
            UserMessageId = userMsgId, ResponseMessageId = responseMsgId
        }, o => o.WithTarget(new Address(threadPath)), ct);

        // Wait for execution to complete
        return await WaitForExecutionAsync(client, threadPath, responseMsgId, expectedMsgCount, ct);
    }

    /// <summary>
    /// Polls until IsExecuting=false and Messages.Count >= expectedMsgCount.
    /// Returns the last response message text.
    /// </summary>
    public static async Task<string> WaitForExecutionAsync(
        IMessageHub client, string threadPath, string responseMsgId,
        int expectedMsgCount, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            var threadNodeId = threadPath.Contains('/') ? threadPath[(threadPath.LastIndexOf('/') + 1)..] : threadPath;
            var dataResp = await client.AwaitResponse(
                new GetDataRequest(new EntityReference(nameof(MeshNode), threadNodeId)),
                o => o.WithTarget(new Address(threadPath)), ct);
            var node = dataResp.Message.Data as MeshNode;
            var thread = node?.Content as MeshThread;
            if (thread == null && node?.Content is JsonElement je)
                thread = je.Deserialize<MeshThread>(client.JsonSerializerOptions);

            if (thread is { IsExecuting: false } && thread.Messages.Count >= expectedMsgCount)
            {
                var msgId = responseMsgId ?? thread.Messages[^1];
                var msgResp = await client.AwaitResponse(
                    new GetDataRequest(new EntityReference(nameof(MeshNode), msgId)),
                    o => o.WithTarget(new Address($"{threadPath}/{msgId}")), ct);
                var msgNode = msgResp.Message.Data as MeshNode;
                var tmsg = msgNode?.Content as ThreadMessage;
                if (tmsg == null && msgNode?.Content is JsonElement mje)
                    tmsg = mje.Deserialize<ThreadMessage>(client.JsonSerializerOptions);
                if (tmsg?.Text is { Length: > 0 })
                    return tmsg.Text;
            }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException($"Execution did not complete for {threadPath}");
    }
}
