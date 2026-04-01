using System;
using System.Collections.Generic;
using System.Linq;
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
/// </summary>
public class DelegationCompletionTest(ITestOutputHelper output) : TestBase(output)
{
    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<RlsChatSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<IMessageHub> GetClientAsync()
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", "completion"),
            config =>
            {
                config.TypeRegistry.AddAITypes();
                return config.AddLayoutClient();
            });
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "Roland",
            Name = "Roland Buergi"
        });
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }

    /// <summary>
    /// Verifies that SubmitMessageRequest produces two responses:
    /// 1. CellsCreated (immediate)
    /// 2. ExecutionCompleted (after agent finishes streaming)
    ///
    /// The test uses RegisterCallback (same pattern as delegation tool)
    /// and collects all responses until ExecutionCompleted arrives.
    /// </summary>
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

        _ = client.RegisterCallback((IMessageDelivery)delivery!, cb =>
        {
            if (cb is IMessageDelivery<SubmitMessageResponse> sr)
            {
                var msg = sr.Message;
                Output.WriteLine($"Response: Status={msg.Status}, Success={msg.Success}, Error={msg.Error}, TextLen={msg.ResponseText?.Length ?? 0}");
                responses.Add((msg.Status, msg.Success, msg.ResponseText));

                if (msg.Status != SubmitMessageStatus.CellsCreated)
                    completionTcs.TrySetResult(msg.Success);
            }
            else if (cb is IMessageDelivery<DeliveryFailure> df)
            {
                Output.WriteLine($"DeliveryFailure: {df.Message.Message}");
                completionTcs.TrySetResult(false);
            }
            return cb;
        });

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
