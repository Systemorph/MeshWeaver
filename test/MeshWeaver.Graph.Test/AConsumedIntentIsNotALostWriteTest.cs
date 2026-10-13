using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A FIELD ANOTHER WRITER CONSUMES IS NOT A LOST WRITE</b> — Systemorph/MeshWeaver#6635.
///
/// <para>An MCP <c>patch</c> of a triage item (a review plus <c>requestedAction: PostReview</c>)
/// committed at v279; the item's control plane claimed the request 63 ms later (v281), clearing
/// <c>requestedAction</c>. The tool then answered <i>"the patch … did not land within the
/// confirmation window … retry"</i>: its confirmation re-read the caller's own leaves from the LIVE
/// node, and the intent it was looking for had already been legitimately consumed. Telling a caller
/// to retry a non-idempotent request that DID land is the harm.</para>
///
/// <para>The watcher below plays the control plane, on the item's own hub where a real one lives:
/// it clears <c>requestedAction</c> the moment it sees one. The confirmation must be the owner's
/// commit verdict (what <c>UpdateNode</c> emits), so every patch and update answers as landed. The
/// NEGATIVE CONTROL is the <c>consumed</c> assertion: the intent really was written and really was
/// consumed, so a read-back of the live node had nothing left to find.</para>
/// </summary>
public class AConsumedIntentIsNotALostWriteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string IntentType = "IntentConsumedProbe";

    /// <summary>The content shape: an intent a control plane consumes, and a durable note.</summary>
    public record IntentContent
    {
        /// <summary>The intent — cleared by the watcher as soon as it lands.</summary>
        public string? RequestedAction { get; init; }

        /// <summary>A durable field nobody consumes.</summary>
        public string? Note { get; init; }
    }

    /// <summary>Every intent the owner-side watcher consumed, in order.</summary>
    private readonly ReplaySubject<string> consumed = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(IntentType)
            {
                Name = "Intent Consumed Probe",
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<IntentContent>())
                    .WithInitialization(hub => Observable.Defer(() =>
                    {
                        hub.RegisterForDisposal(ConsumeIntents(hub));
                        return Observable.Return(Unit.Default);
                    }))
            });

    /// <summary>
    /// The control plane, where a real one lives: on the item's OWN hub, watching its own node and
    /// clearing <c>requestedAction</c> the moment one lands — a local write on the owner, so it
    /// commits ahead of the round trip that carries the patch's verdict back to the caller. Writes
    /// are serialised through one Concat so the watcher's own clears never overlap.
    /// </summary>
    private IDisposable ConsumeIntents(IMessageHub hub)
    {
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        var own = hub.GetWorkspace().GetMeshNodeStream();
        return own
            .Where(node => node?.ContentAs<IntentContent>(hub.JsonSerializerOptions)?.RequestedAction is not null)
            .Select(node => Observable.Defer(() =>
            {
                var action = node!.ContentAs<IntentContent>(hub.JsonSerializerOptions)!.RequestedAction!;
                using (access.ImpersonateAsHub(hub))
                    return own.Update(current => current with
                        {
                            Content = (current.ContentAs<IntentContent>(hub.JsonSerializerOptions) ?? new IntentContent())
                                with { RequestedAction = null },
                        })
                        .Do(_ => consumed.OnNext(action));
            }).Catch<MeshNode, Exception>(ex =>
            {
                Output.WriteLine($"[watcher] clear faulted: {ex.GetType().Name}: {ex.Message}");
                return Observable.Empty<MeshNode>();
            }))
            .Concat()
            .Subscribe(_ => { }, ex => Output.WriteLine($"[watcher] faulted: {ex}"));
    }

    private static TimeSpan Budget => TimeSpan.FromSeconds(60);

    private async Task<string> CreateItem()
    {
        var id = "intent" + Guid.NewGuid().ToString("N")[..8];
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode(id, TestPartition)
            {
                Name = "Intent item",
                NodeType = IntentType,
                State = MeshNodeState.Active,
                Content = JsonSerializer.Deserialize<JsonElement>("""{"note":"created"}"""),
            })
            .Take(1).Should().Within(Budget).Emit("the item must exist first",
                cancellationToken: TestContext.Current.CancellationToken);
        return $"{TestPartition}/{id}";
    }

    [Fact(Timeout = 180_000)]
    public async Task APatchedIntent_ConsumedRightAfterItLands_IsReportedAsLanded()
    {
        var path = await CreateItem();
        var operations = new MeshOperations(Mesh);

        // Several rounds: a single one can win the race the read-back lost, which is exactly why the
        // defect was intermittent in production (a patch on the same item 12 minutes earlier
        // confirmed).
        for (var round = 0; round < 5; round++)
        {
            var action = $"PostReview{round}";
            var result = await operations
                .Patch(path, new JsonObject
                {
                    ["content"] = new JsonObject { ["requestedAction"] = action, ["note"] = $"round {round}" },
                }.ToJsonString())
                .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
            Output.WriteLine($"round {round}: {result}");

            result.Should().StartWith("Patched:",
                $"round {round}: the owner committed the patch; an intent the control plane consumed "
                + "afterwards is not a write that failed to land — and 'retry' is wrong advice for a "
                + "non-idempotent request (#6635)");

            // Negative control: the intent really landed and really was consumed.
            await consumed.Where(a => a == action).Take(1).Should().Within(Budget).Emit(
                $"round {round}: the watcher must have seen and consumed '{action}' — otherwise this "
                + "test never exercised a consumed intent",
                cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task AnUpdatedIntent_ConsumedRightAfterItLands_IsReportedAsLanded()
    {
        var path = await CreateItem();
        var operations = new MeshOperations(Mesh);
        var id = path[(path.LastIndexOf('/') + 1)..];

        for (var round = 0; round < 3; round++)
        {
            var action = $"Claim{round}";
            var nodes = new JsonArray(new JsonObject
            {
                ["id"] = id,
                ["namespace"] = TestPartition,
                ["name"] = "Intent item",
                ["nodeType"] = IntentType,
                ["content"] = new JsonObject { ["requestedAction"] = action, ["note"] = $"update {round}" },
            }).ToJsonString();
            var result = await operations.Update(nodes)
                .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
            Output.WriteLine($"round {round}: {result}");

            result.Should().StartWith("Updated:",
                $"round {round}: the owner committed the update; a consumed intent is not a lost write (#6635)");

            await consumed.Where(a => a == action).Take(1).Should().Within(Budget).Emit(
                $"round {round}: the watcher must have consumed '{action}'",
                cancellationToken: TestContext.Current.CancellationToken);
        }
    }
}
