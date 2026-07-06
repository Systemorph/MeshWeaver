using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Regression guard for the rule-chain dispatch race.
/// <para><see cref="MessageHub"/> dispatches a delivery by folding over its rule chain (a
/// <see cref="ThreadSafeLinkedList{T}"/>). It used to walk that chain via the raw
/// <see cref="System.Collections.Generic.LinkedListNode{T}.Next"/> OUTSIDE the list's lock, so a
/// concurrent <c>rules.Remove</c> — a handler disposable firing during teardown / rapid handler
/// churn — invalidated the node mid-walk (<see cref="System.Collections.Generic.LinkedList{T}"/>
/// nulls the removed node's owning-list reference before its <c>next</c>) and <c>get_Next()</c>
/// threw <see cref="NullReferenceException"/> → the delivery failed → the request never got its
/// response. This test hammers dispatch while concurrently registering/disposing rules.</para>
/// <para>With the snapshot fix every dispatch completes cleanly (deterministic — no flakiness);
/// a revert to the raw-node walk NREs the delivery and the round-trips below time out.</para>
/// </summary>
public class RuleChainConcurrentDisposeTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record PingReq : IRequest<PongEvent>;
    private record PongEvent;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) =>
        configuration.WithHandler<PingReq>((hub, request) =>
        {
            hub.Post(new PongEvent(), o => o.ResponseFor(request));
            return request.Processed();
        });

    [Fact(Timeout = 60_000)]
    public async Task Dispatch_WhileRulesAreConcurrentlyRemoved_NeverFaults()
    {
        var host = GetHost();
        var target = CreateHostAddress();

        // Fill the rule chain with many pass-through rules so a dispatch spends real time walking it,
        // widening the window in which a concurrent Remove can invalidate the current node.
        const int ruleCount = 100;
        var disposables = new List<IDisposable>(ruleCount);
        for (var i = 0; i < ruleCount; i++)
            disposables.Add(host.Register((d, _) => Observable.Return(d)));

        using var cts = new CancellationTokenSource();

        // Background churn: a tight loop disposing + re-registering pass-through rules (each Dispose is
        // a rules.Remove), racing the dispatch's chain walk — the exact remove-during-dispatch shape.
        // Only this task touches `disposables`; the dispatch loop below never does.
        var churn = Task.Run(() =>
        {
            var n = 0;
            while (!cts.IsCancellationRequested)
            {
                var idx = n++ % disposables.Count;
                disposables[idx].Dispose();
                disposables[idx] = host.Register((d, _) => Observable.Return(d));
            }
        });

        try
        {
            // Many round-trips; each dispatch folds over the churning rule chain. With the fix all
            // resolve; a raw-walk regression NREs the delivery so the response never lands → timeout.
            for (var i = 0; i < 300; i++)
                await host.Observe(new PingReq(), o => o.WithTarget(target))
                    .Should().Within(10.Seconds()).Emit();
        }
        finally
        {
            cts.Cancel();
            await churn;
            foreach (var d in disposables)
                d.Dispose();
        }
    }
}
