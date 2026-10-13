using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>AN ACCEPTED DATA CHANGE OF A NODE'S OWN MESHNODE ADVANCES ITS VERSION AND REACHES THE STORE.</b>
///
/// <para>A <see cref="DataChangeRequest"/> addressed to the node's own hub and carrying the node at
/// its CURRENT version (the natural shape: read the node, change the content, send it back — a
/// synced subscriber's write-back, an undo, an in-mesh script) was committed in RAM at the OLD
/// version. The type source minted the next version only into its own diff bookkeeping, never into
/// the committed store, so the persistence sampler saved the change at the version the create had
/// already made durable — and <c>HandleSaveMeshNode</c> dropped it as "already persisted"
/// (<see cref="PostCommitFlushRegistry"/>: two distinct own-node states must never share a version).
/// The dispose flush skipped it on the same predicate, so a recycle reloaded the older row: the
/// acknowledged change was gone, with nothing logged.</para>
///
/// <para>The guard sends the change at the current version, recycles at once, and reads the durable
/// row and the fresh activation. The negative control sends the identical change carrying an
/// already-advanced version — the one shape that was always persisted — so the guard's failure is
/// the un-minted version and nothing else in the sequence.</para>
/// </summary>
public class ADataChangeOfTheOwnNodeSurvivesARecycleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static string NewId() => "dco" + Guid.NewGuid().ToString("N")[..8];

    private string? TextOf(MeshNode? node) => node?.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content;

    private IStorageAdapter Store => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>Re-reads the DURABLE row until it carries <paramref name="text"/> — what a recycle reloads.</summary>
    private Task<MeshNode?> DurableRowCarries(string path, string text, string because)
        => Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => Store.Read(path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => TextOf(n) == text)
            .Should().Within(30.Seconds()).Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>
    /// Creates a page, sends a <see cref="DataChangeRequest"/> to the node's own address carrying
    /// the live node with new content (at the live version + <paramref name="versionOffset"/>),
    /// and recycles the hub the moment the change is acknowledged.
    /// </summary>
    private async Task<(string Path, long CreatedVersion, long CommittedVersion)> DataChangeThenRecycle(long versionOffset)
    {
        var ct = TestContext.Current.CancellationToken;
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(new MeshNode(id, TestPartition)
            {
                Name = id,
                NodeType = "Markdown",
                State = MeshNodeState.Active,
                Content = new MarkdownContent { Content = "v1" },
            })
            .Take(1).Should().Within(60.Seconds()).Emit("the page must exist first", cancellationToken: ct);
        await DurableRowCarries(path, "v1", "the created row is durable");

        var live = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && TextOf(n) == "v1")
            .Should().Within(60.Seconds()).Emit("the node's hub serves the created state", cancellationToken: ct);
        var before = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        before.Should().NotBeNull("the node's hub is activated before the change");

        var response = await RequestHub
            .Observe<DataChangeResponse>(
                DataChangeRequest.Update([live! with
                {
                    Version = live.Version + versionOffset,
                    Content = new MarkdownContent { Content = "v2" },
                }]),
                o => o.WithTarget(new Address(path)))
            .Take(1)
            .Should().Within(60.Seconds()).Emit("the owner answers the data change", cancellationToken: ct);
        response!.Message.Status.Should().Be(DataChangeStatus.Committed, "the data change is accepted");

        var committed = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && TextOf(n) == "v2")
            .Should().Within(60.Seconds()).Emit("the change is live", cancellationToken: ct);

        await RequestHub
            .RecycleNode(path, reason: "ADataChangeOfTheOwnNodeSurvivesARecycleTest: re-activate the node from its durable row")
            .Take(1)
            .Should().Within(90.Seconds()).Emit("the recycled address answers again", cancellationToken: ct);
        Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never)
            .Should().NotBeSameAs(before, "the recycle replaced the activation, so what it serves was RE-READ");
        return (path, live.Version, committed!.Version);
    }

    /// <summary>
    /// 🚨 THE GUARD. A data change carrying the node at its CURRENT version must advance the version
    /// on commit, and must be in the durable row the fresh activation serves.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task ADataChangeAtTheCurrentVersion_AdvancesTheVersion_AndSurvivesARecycle()
    {
        var (path, created, committed) = await DataChangeThenRecycle(versionOffset: 0);

        await DurableRowCarries(path, "v2", "the accepted data change reached the store");
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && TextOf(n) == "v2")
            .Should().Within(60.Seconds()).Emit("the re-activated hub serves the change, not the older row",
                cancellationToken: TestContext.Current.CancellationToken);
        committed.Should().BeGreaterThan(created,
            "an accepted change of the own node mints the next version — two distinct states never share one");
    }

    /// <summary>
    /// Negative control: the identical sequence with the version already advanced by the sender. It
    /// passes with or without the fix, so the guard above fails only on the un-minted version.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task ADataChangeCarryingAnAdvancedVersion_SurvivesARecycle()
    {
        var (path, created, committed) = await DataChangeThenRecycle(versionOffset: 1);

        committed.Should().BeGreaterThan(created, "the sender advanced the version itself");
        await DurableRowCarries(path, "v2", "the durable row carries the change");
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null && TextOf(n) == "v2")
            .Should().Within(60.Seconds()).Emit("the re-activated hub serves the change",
                cancellationToken: TestContext.Current.CancellationToken);
    }
}
