using System;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// 🚨 <b>A content collection's creation promise is owned by the hub, and a request after the hub's
/// teardown is refused instead of served from a replay.</b>
///
/// <para><b>The site.</b> <c>ContentService.InitializeCollection</c> caches each collection as
/// <c>CreateCollection(config)…Replay(1)</c> connected on its first subscriber. With a bare
/// <c>AutoConnect(1)</c> the connection belonged to the chain: the hub's teardown disposed the
/// collection (see <c>ContentCollectionDiesWithItsHubTest</c>) but could not reach the promise, so a
/// creation still in flight ran its provider factory against the closed scope, and a
/// <c>GetCollection</c> after the teardown REPLAYED the disposed collection to its caller as if it
/// were live. It is now <c>AutoConnectOwnedBy(hub, nameof(ContentService))</c>.</para>
///
/// <para><b>Negative control</b> (run by hand when this test was written): restoring the bare
/// <c>.AutoConnect(1)</c> at the site fails the last assertion — the late <c>GetCollection</c> emits
/// the disposed collection (<c>OnNext</c>) instead of terminating with
/// <see cref="ObjectDisposedException"/>. The control arm proves the collection resolved on the live
/// hub first, so the refusal is measured against a promise that had genuinely been connected.</para>
/// </summary>
public class ContentCollectionConnectionOwnedByHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly string path = Path.Combine(
        AppContext.BaseDirectory, "Files", "OwnedByHub", Guid.NewGuid().ToString("N"));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddContentCollections()
            .AddFileSystemContentCollection("owned", _ => path);

    [Fact(Timeout = 60_000)]
    public async Task AfterTheHubsTeardown_GetCollectionIsRefused_NotServedFromTheReplay()
    {
        Directory.CreateDirectory(path);
        var host = GetHost();
        var service = host.ServiceProvider.GetRequiredService<IContentService>();

        var collection = await service
            .GetCollection("owned")
            .Where(c => c is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        collection!.IsDisposed.Should().BeFalse(
            "CONTROL ARM: the creation promise connected and resolved a live collection on the live hub");

        host.Dispose();
        await host.DisposalCompleted.ObserveCompletion(
            ex => Output.WriteLine($"late disposal fault: {ex}"),
            TestContext.Current.CancellationToken);
        collection.IsDisposed.Should().BeTrue("precondition: the hub disposed the collection it created");

        var terminal = await service
            .GetCollection("owned")
            .Materialize()
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        terminal.Kind.Should().Be(NotificationKind.OnError,
            "a GetCollection after the hub's teardown must TERMINATE: a bare AutoConnect(1) replays the "
            + "collection the hub has since disposed, handing the caller a dead watcher and a dead stream");
        terminal.Exception.Should().BeOfType<ObjectDisposedException>()
            .Which.ObjectName.Should().Be(nameof(ContentService),
                "the refusal names the owner whose teardown released the promise");
    }
}
