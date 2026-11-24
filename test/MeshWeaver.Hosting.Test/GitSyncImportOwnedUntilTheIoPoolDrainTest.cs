using System.Reactive.Subjects;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#8510 — "pool 'Layout' was cancelled with N subscription(s) still attached" at Error on silo
/// stops, the unowned chain being <c>GitHubWebhookProcessor.TriggerSyncAtCommit</c> → the import's activity.
/// The webhook-triggered import is fire-and-forget; it is now owned until the IoPool drain while it runs
/// and released the moment it concludes.
/// </summary>
public class GitSyncImportOwnedUntilTheIoPoolDrainTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public void ARunningImport_IsOwned_AndReleasedBeforeTheCancelOnADrain()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        var feed = new Subject<string>();
        var owned = registry.OwnedUntilDrainCount;

        Mesh.SubscribeOwnedUntilIoDrain(feed, "test-import", _ => { }, _ => { });

        registry.OwnedUntilDrainCount.Should().Be(owned + 1, "an import in flight is owned until the drain");
        feed.HasObservers.Should().BeTrue();

        registry.Dispose();
        feed.HasObservers.Should().BeFalse("the drain released the import before it cancelled the pool");
        registry.OwnedUntilDrainCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AConcludedImport_ReleasesItsOwnership_OnCompletionAndOnFault(bool faults)
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        var feed = new Subject<string>();
        var owned = registry.OwnedUntilDrainCount;
        string? seen = null;
        Exception? failure = null;

        Mesh.SubscribeOwnedUntilIoDrain(feed, "test-import", v => seen = v, e => failure = e);
        feed.OnNext("activity/1");
        if (faults)
            feed.OnError(new InvalidOperationException("import failed"));
        else
            feed.OnCompleted();

        seen.Should().Be("activity/1");
        (failure is not null).Should().Be(faults, "the fault still reaches the caller's handler");
        registry.OwnedUntilDrainCount.Should().Be(owned, "a concluded import leaves nothing owned behind");
    }
}
