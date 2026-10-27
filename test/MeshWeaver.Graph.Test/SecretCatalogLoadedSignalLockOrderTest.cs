#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 The Instance Secrets catalog's "has it listed?" signal must never run a subscriber's code under
/// a lock its own feed needs (Doc/Architecture/CatalogLoadedSignal). The secret catalog's
/// <c>WhenLoaded</c> is the first half of <c>InstanceGitHubApp.WhenReadable</c>'s zip, and with the
/// signal a <c>BehaviorSubject&lt;bool&gt;</c> a subscriber's replay ran under the subject's gate —
/// while the catalog's own change handler reads that same subject's <c>Value</c> on every full
/// listing. A subscriber still inside its callback therefore froze the catalog's live feed: no
/// further secret, revocation or rotation reached the process until the callback returned.
///
/// <para>Deterministic: a <c>WhenLoaded</c> subscriber parks until a Reset delivered on another thread
/// has been PROCESSED (the feed delivers synchronously, so the producer's <c>OnNext</c> returning is
/// the handler having run). Under the old signal the handler blocks on the gate the parked callback
/// holds, so the delivery cannot return within the park.</para>
/// </summary>
public class SecretCatalogLoadedSignalLockOrderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static QueryResultChange<MeshNode> Listing(QueryChangeType type) =>
        new() { ChangeType = type, Items = [] };

    [Fact]
    public async Task AFullListing_IsProcessedWhileAWhenLoadedSubscriberIsStillRunning()
    {
        var live = new Subject<QueryResultChange<MeshNode>>();
        using var catalog = new InstanceSecretCatalog(Mesh, null, () => live, _ => System.Reactive.Disposables.Disposable.Empty);
        catalog.Start();
        live.OnNext(Listing(QueryChangeType.Initial));
        await catalog.WhenLoaded.Should().Within(TestTimeouts.Convergence)
            .Emit("the first listing marks the catalog loaded", TestContext.Current.CancellationToken);

        var callbackEntered = 0;
        var resetProcessed = 0;
        var resetSeenByCallback = 0;

        // The feed's own producer: a full listing arriving while a WhenLoaded subscriber is mid-callback.
        var producer = new Thread(() =>
        {
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref callbackEntered) == 1, TimeSpan.FromSeconds(30)))
                return;
            live.OnNext(Listing(QueryChangeType.Reset));
            Volatile.Write(ref resetProcessed, 1);
        }) { IsBackground = true, Name = "secret catalog feed" };
        producer.Start();

        try
        {
            using var subscription = catalog.WhenLoaded.Subscribe(_ =>
            {
                Volatile.Write(ref callbackEntered, 1);
                if (SpinWait.SpinUntil(() => Volatile.Read(ref resetProcessed) == 1, TimeSpan.FromSeconds(5)))
                    Volatile.Write(ref resetSeenByCallback, 1);
            });
        }
        finally
        {
            // Never strand the producer, whatever the assertion below decides.
            Volatile.Write(ref callbackEntered, 1);
        }

        Volatile.Read(ref resetSeenByCallback).Should().Be(1,
            "the catalog's feed must keep processing listings while a WhenLoaded subscriber's callback runs — "
            + "a signal that replays under a lock its own handler takes freezes the feed behind any slow subscriber");
    }
}
