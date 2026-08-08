#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// DETERMINISTIC pins of the two #648 owner-side ack invariants:
/// <list type="number">
///   <item><b>All-keys-refused ⇒ never Success.</b> A cross-hub three-way merge that refuses
///   EVERY intended change (the writer's base is stale and the live value is newer) used to
///   fall into the no-change backstop and ack SUCCESS — the caller's resilient machinery
///   stopped retrying and the write was lost while reported applied. The owner must NACK
///   (Conflict) so the caller re-reads and re-applies.</item>
///   <item><b>A superseded activation refuses-and-redirects, never merge-and-acks.</b> A patch
///   delivered to a QUIESCING/DISPOSING activation used to be merged into state that dies with
///   the hub and acked Success. The single-writer activation invariant: only the CURRENT
///   activation applies patches; a superseded one NACKs retryable (OwnerDisposing) so the
///   caller's re-enqueue lands the same update on the fresh activation.</item>
/// </list>
/// Both pins drive the REAL owner pipeline over the wire (PatchDataRequest — the exact message
/// <c>UpdateRemote</c> posts), because the defect is the owner's ACK SEMANTICS.
/// </summary>
public class SupersededActivationPatchNackTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private JsonSerializerOptions JsonOptions => Mesh.JsonSerializerOptions;

    private IObservable<MeshNode?> ReadDurable(string path) => Storage.Read(path, JsonOptions);

    private string NameKey => JsonOptions.PropertyNamingPolicy?.ConvertName(nameof(MeshNode.Name))
        ?? nameof(MeshNode.Name);

    private async Task<Address> ResolveOwner(string path)
    {
        var resolution = await PathResolver.ResolvePath(path).Should().Within(30.Seconds()).Emit();
        resolution.Should().NotBeNull($"'{path}' must resolve to an owning hub");
        return new Address(resolution!.Prefix.ToString()!);
    }

    /// <summary>
    /// Creates a node, advances its live Name to <c>live-truth</c> through the owner, and
    /// returns the crafted stale-mirror patch: Name → <c>stale-mirror-write</c> diffed against
    /// the pre-advance base <c>created</c>. The owner's three-way merge must refuse the key
    /// (whole-string rewrites on both sides overlap), leaving the node unchanged.
    /// </summary>
    private async Task<(string Path, Address Owner, PatchDataRequest Patch)> ArrangeRefusedPatch()
    {
        var id = $"refused-nack-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "created", NodeType = "Markdown", State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();
        await ReadNode(path).Should().Within(30.Seconds()).Match(n => n is { Name: "created" });

        var client = GetClient(c => c.AddData());
        await client.GetWorkspace().GetMeshNodeStream(path)
            .Update(n => n with { Name = "live-truth" })
            .Should().Within(30.Seconds()).Emit();
        await ReadNode(path).Should().Within(30.Seconds()).Match(n => n is { Name: "live-truth" });

        var owner = await ResolveOwner(path);
        var patch = new PatchDataRequest(
            new MeshNodeReference(),
            new RawJson($"{{\"{NameKey}\":\"stale-mirror-write\"}}"))
        {
            BaseValues = new RawJson($"{{\"{NameKey}\":\"created\"}}")
        };
        return (path, owner, patch);
    }

    /// <summary>
    /// #648 pin 1 — fail-without: the no-change backstop acked the all-refused patch
    /// SUCCESS; pass-with: the owner NACKs Conflict and keeps the live value.
    /// </summary>
    [Fact(Timeout = 55_000)]
    public async Task AllKeysRefusedThreeWayMerge_IsNackedConflict_NeverAckedSuccess()
    {
        var (path, owner, patch) = await ArrangeRefusedPatch();

        var response = await AwaitResponseAsync(patch, o => o.WithTarget(owner));
        var patchResponse = response.Message;

        Output.WriteLine(
            $"[response] success={patchResponse.Success} code={patchResponse.NodeError?.Code} "
            + $"error={patchResponse.Error}");
        patchResponse.Success.Should().BeFalse(
            "a merge that refused EVERY intended change did not apply the caller's write — "
            + "acking it Success is the #648 acked-write-loss (the resilient caller stops "
            + "retrying a write that never landed)");
        patchResponse.NodeError.Should().NotBeNull();
        patchResponse.NodeError!.Code.Should().Be(MeshNodeErrorCode.Conflict,
            "the caller must be told to re-read and re-apply — a terminal typed conflict, "
            + "not a silent success and not a retry storm");

        // And the owner kept the newer live value — the refusal is the merge's verdict.
        (await ReadNode(path).Should().Within(30.Seconds()).Emit())!
            .Name.Should().Be("live-truth");
    }

    /// <summary>
    /// #648 pin 2 — single-writer activation invariant. The patch is posted right after the
    /// owner's <c>Dispose()</c>: the DisposeRequest is queued ahead of it in the same inbox,
    /// so the handler sees a hub past Started. Fail-without: the quiescing activation merges
    /// into dying state and acks Success (for this all-refused patch, the no-change backstop's
    /// Success lie). Pass-with: the owner refuses at handler entry with the retryable
    /// OwnerDisposing — or, when the window has already closed, the delivery fails typed —
    /// and under NO outcome is the patch acked Success.
    /// </summary>
    [Fact(Timeout = 55_000)]
    public async Task PatchDeliveredToDisposingActivation_IsNackedRetryable_NeverMergeAndAck()
    {
        var (path, ownerAddress, patch) = await ArrangeRefusedPatch();

        var owner = Mesh.GetHostedHub(ownerAddress, HostedHubCreation.Never);
        owner.Should().NotBeNull("the owner hub must be live after the warm read");
        owner!.Dispose();
        Output.WriteLine($"[dispose] posted for {ownerAddress}");

        // Sequence on the OBSERVED lifecycle, not on hope: post the patch only once the owner
        // is provably past Started (Quiescing or later) — the superseded-activation window the
        // invariant is about. Routing may still hand the patch to a FRESH activation when the
        // old one has already left the routing table; both branches are asserted below.
        await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
            .Select(_ => owner.RunLevel)
            .Should().Within(20.Seconds()).Match(level => level > MessageHubRunLevel.Started);
        Output.WriteLine($"[runlevel] owner at {owner.RunLevel}");

        try
        {
            var response = await AwaitResponseAsync(patch, o => o.WithTarget(ownerAddress));
            var patchResponse = response.Message;
            Output.WriteLine(
                $"[response] success={patchResponse.Success} code={patchResponse.NodeError?.Code} "
                + $"error={patchResponse.Error}");

            patchResponse.Success.Should().BeFalse(
                "a superseded (quiescing/disposing) activation must refuse-and-redirect — "
                + "merging into state that dies with the hub and acking Success is the #648 "
                + "acked-write-loss");
            patchResponse.NodeError.Should().NotBeNull();
            patchResponse.NodeError!.Code.Should().BeOneOf(
                [MeshNodeErrorCode.OwnerDisposing, MeshNodeErrorCode.Conflict],
                "the disposing activation NACKs OwnerDisposing (retryable — the caller "
                + "re-enqueues against the fresh activation); if routing already reached a "
                + "FRESH activation instead, the stale-base patch is refused as Conflict — "
                + "either way, never Success");
        }
        catch (Exception ex) when (ex is DeliveryFailureException or TimeoutException or TaskCanceledException)
        {
            // The disposal window closed before the patch was handled — the delivery fails
            // typed (shutting-down NACK / hub torn down). A refusal, not a lie: acceptable.
            Output.WriteLine($"[nack-shape-2] {ex.GetType().Name}: {ex.Message}");
        }

        // Whatever the NACK shape, the write must not have half-landed anywhere durable.
        var final = await Observable.Timer(TimeSpan.FromSeconds(1))
            .SelectMany(_ => ReadDurable(path))
            .Should().Within(20.Seconds()).Emit();
        final!.Name.Should().NotBe("stale-mirror-write",
            "a refused/NACKed patch must leave no durable trace");
    }
}
