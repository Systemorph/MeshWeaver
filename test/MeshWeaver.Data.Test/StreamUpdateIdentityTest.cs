using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Verifies that <c>stream.Update(...)</c> preserves the caller's identity on
/// the resulting <c>UpdateStreamRequest</c> delivery. The post-pipeline fills
/// <c>delivery.AccessContext</c> from <c>AccessService.Context</c> (the
/// AsyncLocal); if the caller has set the AsyncLocal, the resulting delivery
/// MUST carry that identity — never fall back to "hub-as-user". When the
/// AsyncLocal is null, the post falls back to the posting hub's own address —
/// that is the design, but the caller should always set AsyncLocal before
/// calling Update from inside a Subscribe / Task.Run / async-iterator boundary.
///
/// Regression guard for the Orleans delegation flow bug where
/// <c>responseStream.Update(...)</c> ran inside the agent's streaming
/// <c>Task.Run</c> with the AsyncLocal lost — the resulting delivery carried
/// the thread hub's own address as identity, which caused the receiver's
/// AccessControlPipeline to deny "user 'thread-hub-path' lacks Thread permission
/// on 'thread-hub-path'".
/// </summary>
public class StreamUpdateIdentityTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))));
    }

    [HubFact]
    public void StreamUpdate_WithAsyncLocalIdentity_DelegateSeesCallerIdentity()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();

        var collectionName = workspace.DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collectionName))!;

        // Set the AsyncLocal context to a specific user identity.
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        // Capture identity SEEN INSIDE the update delegate — what really matters:
        // after Hub.Post(UpdateStreamRequest) → post-pipeline → delivery-pipeline
        // (which sets accessService.Context from delivery.AccessContext) → handler
        // invokes the delegate, can the delegate see "alice" from
        // accessService.Context?
        //
        // ReplaySubject — not Subject. The handler may fire OnNext BEFORE the
        // test's await subscribes; a hot Subject would drop those emissions
        // and the .Where(id == "alice") would never fire. ReplaySubject
        // buffers every OnNext and replays them to late subscribers. See
        // Doc/Architecture/WritingTests.md → "Stream assertions".
        var seen = new System.Reactive.Subjects.ReplaySubject<string?>();
        stream.Update(_ =>
        {
            seen.OnNext(accessService.Context?.ObjectId);
            return null;
        }, _ => { });

        var aliceSeen = seen
            .Should().Within(5.Seconds())
            .Match(id => id == "alice");

        aliceSeen.Should().Be(
            "alice",
            "stream.Update must preserve the caller's AsyncLocal identity into the delegate execution — " +
            "delivery.AccessContext flows through post-pipeline → delivery-pipeline → AccessService.Context");
    }

    [HubFact]
    public async Task StreamUpdate_WithoutAsyncLocalIdentity_FailsClosed()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();

        // Explicitly clear the AsyncLocal to simulate the "lost across async
        // boundary" case (Task.Run / Subscribe-callback / Throttle-tick).
        accessService.SetContext(null);
        accessService.SetCircuitContext(null);

        var collectionName = workspace.DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collectionName))!;

        var insideDelegate = new TaskCompletionSource<string?>();
        stream.Update(_ =>
        {
            insideDelegate.TrySetResult(accessService.Context?.ObjectId);
            return null;
        }, _ => { });

        var seen = await insideDelegate.Task.WaitAsync(5.Seconds());

        // 🚨 2026-05-21 — single fail-closed branch in the PostPipeline. Per
        // the directive "we should NEVER write something as hub", non-mesh
        // hubs no longer fall back to stamping their own address as principal.
        // Legitimate hub-internal flows (SyncStream SetCurrentRequest, etc.)
        // MUST opt in explicitly via AccessService.ImpersonateAsHub /
        // ImpersonateAsSystem at the callsite. With AsyncLocal cleared and no
        // explicit impersonation scope, the delegate sees null Context —
        // and any subsequent mesh write would be denied at the AccessControl
        // pipeline. See AccessContextPropagation.md.
        seen.Should().BeNull(
            "with no AsyncLocal / CircuitContext and no explicit ImpersonateAsHub scope, " +
            "the delegate must see null Context — the post-pipeline no longer fakes " +
            "hub-self identity. Wrap intentional hub-internal writes with " +
            "AccessService.ImpersonateAsHub() / ImpersonateAsSystem().");
    }

    /// <summary>
    /// The agent's streaming loop iterates a chat client's response via
    /// <c>await foreach</c>. Inside that loop body, the agent posts node-update
    /// messages (tool-call results, content chunks). Each of those posts must
    /// run with the user's identity — never with a hub-as-user fallback.
    ///
    /// This test pins the .NET runtime guarantee: <c>AsyncLocal</c> set by the
    /// consumer BEFORE the foreach loop persists across yields, even when the
    /// producer internally hops schedulers (<c>Task.Run</c>, thread-pool
    /// continuations). The consumer's frame retains its ExecutionContext and
    /// the AsyncLocal value flows into every iteration of the body.
    /// </summary>
    [HubFact]
    public async Task AsyncForeachStreaming_PreservesAccessContextAcrossYields()
    {
        var host = GetHost();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();

        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        // Producer that internally hops to the thread pool (mimics what chat-client
        // SDKs do under the hood — HttpClient response reads run on the pool, then
        // continue on whatever scheduler captured the call).
        async IAsyncEnumerable<int> ProducerWithSchedulerHop(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < 5; i++)
            {
                // Force a scheduler hop — this is what loses naive context-handling.
                await Task.Run(() => Task.Delay(1, ct), ct);
                yield return i;
            }
        }

        var seenInBody = new System.Collections.Concurrent.ConcurrentBag<string?>();
        await foreach (var _ in ProducerWithSchedulerHop())
        {
            // Inside the loop body we must still see "alice" — the consumer's
            // ExecutionContext flows here even though the producer hopped pools.
            seenInBody.Add(accessService.Context?.ObjectId);
        }

        seenInBody.Should().HaveCount(5, "producer yielded 5 items");
        seenInBody.Should().AllSatisfy(id => id.Should().Be(
            "alice",
            "AsyncLocal AccessContext set on the consumer side must flow into every " +
            "iteration of the async foreach body, regardless of producer scheduler hops"));
    }

    /// <summary>
    /// Tool calls are async functions invoked by the agent during streaming. When
    /// a tool function runs (e.g. <c>delegate_to_agent</c>), it must observe the
    /// originating user's identity — not the agent's hub address — because the
    /// tool will likely post mesh messages that hit the AccessControlPipeline.
    ///
    /// This test simulates the agent invoking a tool via <c>await</c> while a
    /// user identity is active in AsyncLocal; the tool's body must see that
    /// identity in <c>accessService.Context</c>.
    /// </summary>
    [HubFact]
    public async Task ToolCallInvocation_SeesOriginatingUserIdentity()
    {
        var host = GetHost();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();

        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        // Tool function: simulates a hub-reaching tool that captures
        // accessService.Context at invocation time. In real code the tool would
        // post messages via mesh services — those posts read accessService.Context
        // through the post-pipeline's user-service step. So the value seen here
        // is exactly what downstream posts will be stamped with.
        async Task<string?> ToolFunction(CancellationToken ct)
        {
            // Real tools do hub round-trips, which await deep into the runtime.
            // Force a scheduler hop to verify ExecutionContext still flows.
            await Task.Run(() => Task.Delay(1, ct), ct);
            return accessService.Context?.ObjectId;
        }

        var seenInTool = await ToolFunction(TestContext.Current.CancellationToken);

        seenInTool.Should().Be(
            "alice",
            "tool functions invoked from a user-identity-bearing context MUST see " +
            "the user identity in AccessService.Context — any hub posts they make " +
            "will be stamped with this identity by the post-pipeline");
    }
}
