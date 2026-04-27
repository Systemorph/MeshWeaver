using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Fixture;
using Xunit;

using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Locks in the actor-model invariant: each hub runs its message dispatch on
/// its own <see cref="TaskScheduler"/>. Hosted hubs default to
/// <see cref="TaskScheduler.Default"/> so they're independent of whatever
/// scheduler created them. Hubs that explicitly call
/// <see cref="MessageHubConfiguration.WithTaskScheduler"/> use the chosen
/// scheduler â€” that's how Orleans glue couples the root grain hub to the
/// grain's scheduler. See <c>Doc/Architecture/OrleansTaskScheduler.md</c>.
/// </summary>
public class TaskSchedulerInvariantTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record WhereAmIRequest : IRequest<WhereAmIResponse>;

    private record WhereAmIResponse(int TaskSchedulerId, string TaskSchedulerTypeName);

    /// <summary>
    /// Default hub configuration (no <c>WithTaskScheduler</c>) dispatches on
    /// <see cref="TaskScheduler.Default"/> â€” the thread pool. Verifies that
    /// hosted hubs created from any context end up independent.
    /// </summary>
    [Fact]
    public async Task DefaultHub_DispatchesOnTaskSchedulerDefault()
    {
        var hub = Mesh.GetHostedHub(
            CreateHostAddress(),
            cfg => cfg.WithHandler<WhereAmIRequest>((h, request) =>
            {
                var current = TaskScheduler.Current;
                h.Post(new WhereAmIResponse(current.Id, current.GetType().FullName ?? "?"),
                    o => o.ResponseFor(request));
                return request.Processed();
            }));

        var response = await hub.Observe(new WhereAmIRequest(), o => o.WithTarget(hub.Address)).FirstAsync().ToTask(new CancellationTokenSource(10.Seconds()).Token);

        response.Message.TaskSchedulerId.Should().Be(TaskScheduler.Default.Id,
            because: "hosted hubs must default to TaskScheduler.Default â€” they are independent actors");
    }

    /// <summary>
    /// A hub configured with <c>WithTaskScheduler(custom)</c> dispatches its
    /// handlers on the custom scheduler. That's the mechanism Orleans glue
    /// uses to couple the root grain hub to the grain's scheduler so Orleans
    /// can attribute work.
    /// </summary>
    [Fact]
    public async Task ConfiguredHub_DispatchesOnTheConfiguredScheduler()
    {
        // ConcurrentExclusiveSchedulerPair.ExclusiveScheduler is a known scheduler
        // distinct from TaskScheduler.Default â€” easy to identify by Id.
        var pair = new ConcurrentExclusiveSchedulerPair();
        var customScheduler = pair.ExclusiveScheduler;

        var hub = Mesh.GetHostedHub(
            CreateHostAddress(),
            cfg => cfg.WithTaskScheduler(customScheduler)
                .WithHandler<WhereAmIRequest>((h, request) =>
                {
                    var current = TaskScheduler.Current;
                    h.Post(new WhereAmIResponse(current.Id, current.GetType().FullName ?? "?"),
                        o => o.ResponseFor(request));
                    return request.Processed();
                }));

        var response = await hub.Observe(new WhereAmIRequest(), o => o.WithTarget(hub.Address)).FirstAsync().ToTask(new CancellationTokenSource(10.Seconds()).Token);

        response.Message.TaskSchedulerId.Should().Be(customScheduler.Id,
            because: "WithTaskScheduler must couple the hub's ActionBlock to the supplied scheduler");
    }

    /// <summary>
    /// A hosted sub-hub created from inside a hub configured with a custom
    /// scheduler MUST default to <see cref="TaskScheduler.Default"/> â€” it does
    /// NOT inherit the parent's scheduler. This is the core actor-model
    /// invariant: each hub is an independent actor.
    /// </summary>
    [Fact]
    public async Task HostedHub_FromCustomSchedulerHub_DefaultsToTaskSchedulerDefault()
    {
        var pair = new ConcurrentExclusiveSchedulerPair();
        var parentScheduler = pair.ExclusiveScheduler;

        var subAddress = new Address("sub", "scheduler-test");
        WhereAmIResponse? subResponse = null;
        var subDone = new TaskCompletionSource<WhereAmIResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        var parent = Mesh.GetHostedHub(
            CreateHostAddress(),
            cfg => cfg.WithTaskScheduler(parentScheduler)
                .WithHandler<WhereAmIRequest>((h, request) =>
                {
                    // Create a hosted sub-hub from inside the parent's handler â€” TaskScheduler.Current
                    // is parentScheduler at this point. The sub-hub MUST NOT inherit it.
                    var subHub = h.GetHostedHub(
                        subAddress,
                        subCfg => subCfg.WithHandler<WhereAmIRequest>((sh, subReq) =>
                        {
                            var subCurrent = TaskScheduler.Current;
                            subResponse = new WhereAmIResponse(subCurrent.Id, subCurrent.GetType().FullName ?? "?");
                            subDone.TrySetResult(subResponse);
                            sh.Post(subResponse, o => o.ResponseFor(subReq));
                            return subReq.Processed();
                        }));

                    subHub.Post(new WhereAmIRequest(), o => o.WithTarget(subHub.Address));

                    h.Post(new WhereAmIResponse(TaskScheduler.Current.Id, TaskScheduler.Current.GetType().FullName ?? "?"),
                        o => o.ResponseFor(request));
                    return request.Processed();
                }));

        // Trigger parent â†’ which creates sub-hub + posts to it.
        await parent.Observe(new WhereAmIRequest(), o => o.WithTarget(parent.Address)).FirstAsync().ToTask(new CancellationTokenSource(10.Seconds()).Token);

        var observed = await subDone.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        observed.TaskSchedulerId.Should().Be(TaskScheduler.Default.Id,
            because: "hosted sub-hubs must default to TaskScheduler.Default even when created from a parent that uses a custom scheduler");
        observed.TaskSchedulerId.Should().NotBe(parentScheduler.Id,
            because: "if the sub-hub inherited the parent's scheduler the actor model would be violated");
    }
}
