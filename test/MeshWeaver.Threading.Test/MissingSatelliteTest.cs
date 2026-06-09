using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// an emission/response that will never come — the per-node hub for the
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
///   <item>The <b>missing</b> satellite path surfaces a fast
///         <c>DeliveryFailureException</c> on the cache stream (OnError) —
///         NOT a forever-cold wait. Callers MUST attach an onError handler
///         to Subscribe; without it the exception is unhandled and takes
///         down the Blazor circuit (the prod "progress stuck" symptom).</item>
/// </list>
/// </summary>
public class MissingSatelliteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Per-test mesh — we mutate a Thread node's Messages list to inject a
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
    public void ValidSatellite_Emits_MissingSatellite_StarvesUntilDeadline()
    {
        // 1) Create a thread + a real satellite cell for one of its message ids.
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        var validId = Guid.NewGuid().AsString();
        var missingId = Guid.NewGuid().AsString();

        NodeFactory.CreateNode(new MeshNode(threadPath)
        {
            Name = "Reproducer thread",
            NodeType = ThreadNodeType.NodeType,
            MainNode = TestPartition,
            Content = new MeshThread
            {
                CreatedBy = TestUsers.Admin.ObjectId,
                // Both ids in Messages — the chat view will iterate the list
                // and try to render a bubble for each, including the missing one.
                Messages = ImmutableList.Create(validId, missingId)
            }
        }).Should().Emit();

        // Real satellite for `validId` only.
        NodeFactory.CreateNode(new MeshNode(validId, threadPath)
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
        }).Should().Emit();

        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // 2) Valid path: subscribe and expect a populated emission fast.
        var validPath = $"{threadPath}/{validId}";
        using (accessService.ImpersonateAsSystem())
        {
            var validEmission = cache.GetStream(validPath, Mesh.JsonSerializerOptions)
                .Where(n => n?.Content is not null)
                .Should().Within(TimeSpan.FromSeconds(5)).Emit(
                    "the existing satellite must emit via the cache within 5 s — happy path");
            (validEmission!.Content as ThreadMessage)?.Text.Should().Be("I exist");
        }

        // 3) Missing path: same subscription shape, but no satellite exists. The
        // cache stream surfaces NotFound as OnError(DeliveryFailureException) —
        // fast (sub-second), NOT a 5 s cold-observable wait. Materialize folds the
        // OnError notification into a value so we can assert it reactively (no
        // await, no TaskCompletionSource). Callers MUST attach an onError handler
        // to Subscribe — without it the exception is unhandled and propagates up
        // the Blazor circuit, crashing the page (the prod 2026-05-24 "progress
        // stuck" symptom this test guards). The Materialized OnError IS the proof
        // that a caller subscribing with an onError handler can observe the
        // failure and render a "missing" placeholder instead of taking down the
        // circuit.
        var missingPath = $"{threadPath}/{missingId}";
        using (accessService.ImpersonateAsSystem())
        {
            var errorNotification = cache.GetStream(missingPath, Mesh.JsonSerializerOptions)
                .Where(n => n?.Content is not null)
                .Materialize()
                .Should().Within(TimeSpan.FromSeconds(5)).Match(n => n.Kind == NotificationKind.OnError);
            errorNotification.Exception.Should().BeOfType<DeliveryFailureException>(
                "missing satellite paths must surface as DeliveryFailureException on the cache " +
                "stream — the onError handler ThreadChatView attaches catches this and renders a " +
                "'missing' placeholder instead of crashing the circuit.");
        }
    }

    /// <summary>
    /// Storm-breaker contract: re-reading the SAME missing path many times in a
    /// row (the resubscribe-storm shape that froze the portal on 2026-06-09 —
    /// AgentChatClient.Initialize re-reading an absent optional node every
    /// streaming rebuild) must stay responsive. Every read surfaces a fast
    /// <see cref="DeliveryFailureException"/>; the cache's negative cache replays
    /// the cached failure instead of re-opening an upstream SubscribeRequest per
    /// read, so the path never hangs and the contract never degrades under a flood.
    /// </summary>
    [Fact]
    public void MissingSatellite_ReReadInTightLoop_StaysResponsive_NoStorm()
    {
        var threadId = Guid.NewGuid().AsString();
        var threadPath = $"{TestPartition}/{ThreadNodeType.ThreadPartition}/{threadId}";
        NodeFactory.CreateNode(new MeshNode(threadPath)
        {
            Name = "Storm-breaker thread",
            NodeType = ThreadNodeType.NodeType,
            MainNode = TestPartition,
            Content = new MeshThread { CreatedBy = TestUsers.Admin.ObjectId }
        }).Should().Emit();

        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var missingPath = $"{threadPath}/{Guid.NewGuid().AsString()}";

        // Read the same missing path far past the storm threshold. Each read must
        // surface DeliveryFailureException within a tight deadline — proving the
        // breaker fast-fails (replays the cached error) rather than starving on a
        // fresh routing round-trip per iteration.
        using (accessService.ImpersonateAsSystem())
        {
            for (var i = 0; i < 10; i++)
            {
                var note = cache.GetStream(missingPath, Mesh.JsonSerializerOptions)
                    .Where(n => n?.Content is not null)
                    .Materialize()
                    .Should().Within(TimeSpan.FromSeconds(5)).Match(
                        n => n.Kind == NotificationKind.OnError,
                        $"missing-path read #{i} must stay responsive under the storm breaker");
                note.Exception.Should().BeOfType<DeliveryFailureException>(
                    $"read #{i} must keep surfacing the cached NotFound failure, never hang or change shape");
            }
        }
    }
}
