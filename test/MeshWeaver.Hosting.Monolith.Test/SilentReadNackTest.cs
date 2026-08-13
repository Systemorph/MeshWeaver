using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the terminal-answer contract of <c>DataExtensions.HandleGetDataRequest</c>:
/// <b>a read that is owed a reply always gets one, even when its source never emits.</b>
///
/// <para>The defect (#1362): the handler subscribes a LIVE workspace stream and posts every
/// emission — but had no arm for the stream going terminal WITHOUT ever emitting. That is not a
/// hypothetical: <c>SynchronizationStream.Dispose()</c> COMPLETES its store (deliberately — see
/// #1170/#1171) without publishing a value, and a reduced stream built over an already-disposed
/// parent completes on its very first subscribe. So the delivery was marked <c>Processed</c>, the
/// subscription died silently with the hub, and the CALLER's callback stayed registered for its
/// entire budget. On CI this surfaced as
/// <c>GetMeshNode('ACME/ProductLaunch') timed out after 60.0s … the owning per-node hub never
/// answered the GetDataRequest</c> — a message that names the wrong thing, because the hub HAD
/// activated (+6.7 s), the handler HAD entered and exited <c>Processed</c> (+7.27 s), and 30 ms
/// later four <c>[SYNC_STREAM] Not setting … — stream is disposed</c> warnings recorded the data
/// plane going away underneath it.
/// </para>
///
/// <para>The repro reproduces that state directly and deterministically — no sleeps, no races:
/// tear down the owning per-node hub's MeshNode data-source stream while the hub itself keeps
/// serving messages, which is exactly the "handler runs, streams are disposed" window the CI
/// trace shows. Before the fix the request below never receives anything and the test times out;
/// after it, the caller gets a transient <see cref="ErrorType.ShuttingDown"/> NACK — the same
/// classification routing already mints for a delivery that raced a hub's disposal, which
/// <c>GetMeshNode</c> re-probes once against a fresh activation.</para>
///
/// <para>🚨 The assertion is deliberately NOT "it answers within a timeout I chose". It is
/// "it answers with a specific, retry-worthy classification" — <c>ShuttingDown</c>, never a
/// null-data <c>GetDataResponse</c>. "The owner went away, ask again" and "the node does not
/// exist" are different facts and the caller has to be able to tell them apart.</para>
/// </summary>
public class SilentReadNackTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Fails BEFORE the fix by hanging until the xUnit method timeout (the request is never
    /// answered at all); passes after, with a ShuttingDown DeliveryFailure.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task GetDataRequest_WhoseStreamWasTornDown_IsNacked_NotLeftHanging()
    {
        var path = $"{TestPartition}/silent-read";
        await NodeFactory.CreateNode(
            new MeshNode("silent-read", TestPartition)
            {
                Name = "Silent Read",
                NodeType = "Markdown"
            }).Should().Emit();

        // Warm the owning per-node hub and prove the happy path answers, so a later
        // non-answer cannot be blamed on the node never having existed.
        var warm = await ReadNode(path).Should().Match(n => n is not null);
        warm!.Path.Should().Be(path);

        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull("the read above must have activated the owning per-node hub");

        // 🔻 Tear down the data plane, leave the hub serving. Disposing the data source's
        // partition stream completes its store without a value; every reduced stream built over
        // it from here on completes empty on subscribe. The data source hands the disposed
        // stream back on every subsequent GetStreamForPartition (no liveness check there), so
        // this is a stable state, not a window — which is what makes the repro deterministic.
        var dataSource = owner!.GetWorkspace().DataContext.GetDataSourceForType(typeof(MeshNode));
        dataSource.Should().NotBeNull("the per-node hub owns a MeshNode data source");
        // Assert.NotNull, not FluentAssertions: ISynchronizationStream is an IObservable, so
        // `.Should()` binds the observable-assertion extension instead of the object one.
        var primary = dataSource!.GetStreamForPartition(null);
        Assert.NotNull(primary);
        primary.Dispose();
        Output.WriteLine($"[TEST] disposed the MeshNode data-source stream of {path}");

        // Now issue the read that the handler can no longer answer from the stream.
        var reader = GetClient(c => c.AddData());
        var answer = await reader
            .Observe<GetDataResponse>(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(path)))
            .Select(d => (object?)d.Message)
            // A DeliveryFailure arrives as OnError (DeliveryFailureException) — turn it into a
            // value so one assertion covers both shapes and a hang is the only remaining failure.
            .Catch<object?, Exception>(ex => Observable.Return<object?>(ex))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(15))
            .ToTask(TestContext.Current.CancellationToken);

        Output.WriteLine($"[TEST] answer: {answer}");

        answer.Should().BeOfType<DeliveryFailureException>(
            "a read whose stream went terminal without a value must be NACKed, not left hanging — "
            + "the caller cannot distinguish 'still working' from 'will never answer'");
        var failure = ((DeliveryFailureException)answer!).Failure;
        failure.Should().NotBeNull();
        failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "the owner's data plane is gone — this is retry-worthy, NOT an absence");
        failure.Message.Should().Contain("shutting down",
            "MeshNodeStreamCache.IsTransientOwnerFailure classifies by this marker; without it a "
            + "long-lived stream consumer tears down instead of riding the recycle out");
        failure.Message.Should().NotContain("No node found",
            "that phrase turns a retryable stall into a PROVABLE absence (MeshNodeStreamCache"
            + ".IsMissingNodeFailure) — the exact confusion this NACK exists to avoid");
    }
}
