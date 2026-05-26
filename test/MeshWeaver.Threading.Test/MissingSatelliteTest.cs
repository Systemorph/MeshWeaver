using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Reproducer for the prod 2026-05-24 sub-thread page hang.
///
/// <para><b>Scenario</b>: a thread's <c>MeshThread.Messages</c> list contains
/// a message id whose satellite cell was never materialised (or was
/// deleted). Anything that does <c>workspace.GetMeshNodeStream({thread}/{id})</c>
/// or a raw <c>GetDataRequest</c> targeting that path waits indefinitely for
/// an emission/response that will never come â€” the per-node hub for the
/// missing path has no data to serve, no NotFound is surfaced fast enough
/// to be useful, and downstream subscribers (the chat view's bubble
/// subscriptions, the header's <c>CollectUpdatedNodes</c>, the activity
/// tracker's UpdateRemote) sit on the cold observable forever.</para>
///
/// <para>What this test pins</para>
/// <list type="number">
///   <item>The <b>valid</b> satellite path emits a populated MeshNode via
///         <c>IMeshNodeStreamCache.GetStream</c> within a fast deadline
///         (the happy path).</item>
///   <item>The <b>missing</b> satellite path does NOT emit anything
///         populated within the same window â€” proving the cold-observable
///         starvation pattern. Callers MUST guard themselves with a
///         <c>Timeout(...)</c> or a side-channel probe (the missing-message
///         5 s probe in ThreadChatView relies on this).</item>
/// </list>
///
/// <para>Why it matters: before the fix, the chat-page SSR pre-render
/// piled up 30 s timeouts from multiple call sites that did
/// <c>GetDataRequest</c>/<c>cache.GetStream(missingPath)</c> without a
/// timeout, causing the HTTP response to hang past 30 s and leaking
/// hub callbacks into <c>QUIESCE-TIMEOUT</c>. The fix is two-layered:
/// (a) caller-side timeout/probe to surface "missing" inside 5 s,
/// (b) make sure the GUI renders a "â€” message missing â€”" placeholder
/// instead of a forever skeleton. This test only pins layer (a)'s
/// underlying invariant â€” the cache starvation â€” which is what the
/// caller-side probes assume.</para>
/// </summary>
public class MissingSatelliteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Per-test mesh â€” we mutate a Thread node's Messages list to inject a
    /// fabricated id; isolated state avoids cross-test pollution.
    /// </summary>
    protected override bool ShareMeshAcrossTests => false;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    [Fact]
    public async Task ValidSatellite_Emits_MissingSatellite_StarvesUntilDeadline()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1) Create a thread + a real satellite cell for one of its message ids.
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        var validId = Guid.NewGuid().AsString();
        var missingId = Guid.NewGuid().AsString();

        await NodeFactory.CreateNode(new MeshNode(threadPath)
        {
            Name = "Reproducer thread",
            NodeType = ThreadNodeType.NodeType,
            MainNode = TestPartition,
            Content = new MeshThread
            {
                CreatedBy = TestUsers.Admin.ObjectId,
                // Both ids in Messages â€” the chat view will iterate the list
                // and try to render a bubble for each, including the missing one.
                Messages = ImmutableList.Create(validId, missingId)
            }
        });

        // Real satellite for `validId` only.
        await NodeFactory.CreateNode(new MeshNode(validId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            MainNode = threadPath,
            Content = new ThreadMessage
            {
                Role = "user",
                Text = "I exist",
                Timestamp = DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            }
        });

        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // 2) Valid path: subscribe and expect a populated emission fast.
        var validPath = $"{threadPath}/{validId}";
        MeshNode? validEmission;
        using (accessService.ImpersonateAsSystem())
        {
            validEmission = await cache.GetStream(validPath)
                .Where(n => n?.Content is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(5))
                .FirstAsync()
                .ToTask(ct);
        }
        validEmission.Should().NotBeNull(
            "the existing satellite must emit via the cache within 5 s â€” happy path");
        (validEmission!.Content as ThreadMessage)?.Text.Should().Be("I exist");

        // 3) Missing path: same subscription shape, but no satellite exists.
        // The cache stream surfaces the NotFound as
        // OnError(DeliveryFailureException) â€” fast (sub-second), NOT a 5 s
        // cold-observable wait. Critical that callers attach an onError
        // handler â€” without it the exception is unhandled and crashes the
        // Blazor circuit (prod 2026-05-24 symptom: "still crashing / stuck
        // on progress screen" after the SSR fix). ThreadChatView's
        // SyncMessageSubscriptions now subscribes with an onError that
        // marks the bubble as missing.
        var missingPath = $"{threadPath}/{missingId}";
        Func<Task> badRead = async () =>
        {
            using var _ = accessService.ImpersonateAsSystem();
            await cache.GetStream(missingPath)
                .Where(n => n?.Content is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(5))
                .FirstAsync()
                .ToTask(ct);
        };

        await badRead.Should().ThrowAsync<DeliveryFailureException>(
            "missing satellite paths must surface as DeliveryFailureException on the " +
            "cache stream. Callers MUST attach an onError handler to Subscribe â€” " +
            "without it the exception is unhandled and propagates up the Blazor " +
            "circuit, crashing the page (the 'still crashing / progress stuck' prod " +
            "symptom this test was added for).");

        // 4) Sanity-check the safe call shape: Subscribe(onNext, onError) lets
        // the caller observe the failure WITHOUT throwing. This is the shape
        // SyncMessageSubscriptions now uses.
        Exception? capturedError = null;
        var done = new TaskCompletionSource<bool>();
        using (accessService.ImpersonateAsSystem())
        {
            cache.GetStream(missingPath)
                .Where(n => n?.Content is not null)
                .Subscribe(
                    _ => done.TrySetResult(true),
                    ex =>
                    {
                        capturedError = ex;
                        done.TrySetResult(true);
                    });
        }
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        capturedError.Should().NotBeNull(
            "the onError handler must fire â€” proving that a caller that attaches one " +
            "(like ThreadChatView's bubble subscription) catches the failure and can " +
            "render a 'missing' placeholder instead of taking down the circuit.");
        capturedError.Should().BeOfType<DeliveryFailureException>();
    }
}
