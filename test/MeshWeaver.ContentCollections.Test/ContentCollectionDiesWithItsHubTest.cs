using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// 🚨 <b>A content collection dies with the hub it resolves services from.</b>
///
/// <para>Nothing disposed a <see cref="ContentCollection"/> before: <c>ContentService</c> is not
/// <c>IDisposable</c> and its per-name cache kept every collection — and the live
/// <see cref="FileSystemWatcher"/> its provider attached — for the life of the process. After the
/// hub's teardown the watcher kept firing on its native callback thread, and
/// <c>IngestContentFile</c>'s first act (resolving <c>IoPoolRegistry</c> off the hub's provider)
/// threw <see cref="ObjectDisposedException"/> from the dead <c>LifetimeScope</c> — the teardown
/// straggler class the collection's own defensive catches describe, and one seam of the FutuRe /
/// AI-suite teardown flakiness (the collections there are file-system backed).</para>
/// </summary>
public class ContentCollectionDiesWithItsHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly string path = Path.Combine(
        AppContext.BaseDirectory, "Files", "DiesWithHub", Guid.NewGuid().ToString("N"));

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddContentCollections()
            .AddFileSystemContentCollection("doomed", _ => path);

    [Fact(Timeout = 60_000)]
    public async Task DisposingTheHub_DisposesTheCollectionsItsContentServiceCreated()
    {
        Directory.CreateDirectory(path);
        var host = GetHost();

        var collection = await host.ServiceProvider.GetRequiredService<IContentService>()
            .GetCollection("doomed")
            .Where(c => c is not null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .Await(TestContext.Current.CancellationToken);

        collection!.IsDisposed.Should().BeFalse(
            "precondition: the collection (and its watcher) is live while the hub is");

        host.Dispose();
        await host.DisposalCompleted.ObserveCompletion(
            ex => Output.WriteLine($"late disposal fault: {ex}"),
            TestContext.Current.CancellationToken);

        collection.IsDisposed.Should().BeTrue(
            "the hub owns what its ContentService created — an undisposed collection keeps a live "
            + "FileSystemWatcher whose callbacks resolve services from the disposed scope on a "
            + "native thread, which is the teardown-straggler class this wiring removes");
    }
}
