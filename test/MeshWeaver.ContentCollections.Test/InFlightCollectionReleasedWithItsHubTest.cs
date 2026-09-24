using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// 🚨 <b>A caller waiting on a collection whose creation is still IN FLIGHT when the hub tears down
/// is told so — it is never parked (#5135).</b>
///
/// <para><b>The defect.</b> <c>ContentService.InitializeCollection</c> caches each collection as
/// <c>CreateCollection(config)…Replay(1).AutoConnectOwnedBy(hub, …)</c>. The hub's teardown
/// releases that connection, and releasing a connection unsubscribes the replay subject from its
/// upstream while emitting NOTHING to the subscribers already attached. So a caller that asked for
/// the collection while its initial scan was still running — an MCP <c>upload</c> waiting on
/// <c>GetCollection</c> when the hub it rode went away — received no value, no error and no
/// completion, ever. The refusal <c>AutoConnectOwnedBy</c> gave covered only callers arriving
/// AFTER the release.</para>
///
/// <para><b>Negative control</b> (run by hand when this test was written): removing the
/// <c>TakeUntil(released)</c> from <c>OwnedConnectionExtensions.AutoConnectOwnedBy</c> fails the
/// terminal <c>Emit</c> below on its timeout — the in-flight caller is silent. The control arm
/// proves the scan was genuinely running when the hub was disposed.</para>
/// </summary>
public class InFlightCollectionReleasedWithItsHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string ParkedSourceType = "ParkedScan";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddContentCollections()
            .WithServices(services => services
                .AddKeyedScoped<IStreamProviderFactory, ParkedScanFactory>(ParkedSourceType));

    [Fact(Timeout = 60_000)]
    public async Task ACallerWaitingOnAnInFlightCreation_IsTerminated_WhenTheHubTearsDown()
    {
        var host = GetHost();
        var service = host.ServiceProvider.GetRequiredService<IContentService>();
        var factory = (ParkedScanFactory)host.ServiceProvider
            .GetRequiredKeyedService<IStreamProviderFactory>(ParkedSourceType);
        service.AddConfiguration(new ContentCollectionConfig { Name = "parked", SourceType = ParkedSourceType });

        var waiting = service.GetCollection("parked").Materialize().Replay(1);
        using var connection = waiting.Connect();

        // CONTROL ARM: the scan started and is still running — the caller waits on a creation that
        // is genuinely in flight, not on one that already failed or resolved.
        await factory.Started.Should().Within(TestTimeouts.Convergence)
            .Emit("the collection's initial scan must have started before the teardown",
                TestContext.Current.CancellationToken);
        await waiting.Should().NotEmit(TimeSpan.FromMilliseconds(200),
            "precondition: the parked scan has neither resolved nor failed the collection",
            TestContext.Current.CancellationToken);

        host.Dispose();

        var terminal = await waiting.Should().Within(TestTimeouts.Convergence).Emit(
            "a caller attached while the creation was in flight must receive a TERMINAL when the hub "
            + "releases the promise — silence here is the upload that hung until the proxy gave up",
            TestContext.Current.CancellationToken);
        terminal.Kind.Should().Be(NotificationKind.OnError);
        terminal.Exception.Should().BeOfType<ObjectDisposedException>()
            .Which.ObjectName.Should().Be(nameof(ContentService),
                "the terminal names the owner whose teardown released the promise");
    }

    /// <summary>Hands out a provider whose markdown scan starts and then never yields.</summary>
    private sealed class ParkedScanFactory : IStreamProviderFactory
    {
        private readonly AsyncSubject<Unit> started = new();

        public IObservable<Unit> Started => started;

        public IObservable<IStreamProvider> Create(ContentCollectionConfig config)
            => Observable.Return<IStreamProvider>(new ParkedScanProvider(started));
    }

    private sealed class ParkedScanProvider(AsyncSubject<Unit> started) : IStreamProvider
    {
        public string ProviderType => ParkedSourceType;

        public async IAsyncEnumerable<(Stream? Stream, string Path, DateTime LastModified)> GetStreamsAsync(
            Func<string, bool> filter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            started.OnNext(Unit.Default);
            started.OnCompleted();
            // Parked until the pool's own token ends it at mesh teardown — never released by the test.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public Task<Stream?> GetStreamAsync(string reference, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream?>(null);

        public Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamWithMetadataAsync(
            string path, CancellationToken cancellationToken = default)
            => Task.FromResult<(Stream?, string, DateTime)>((null, path, DateTime.MinValue));

        public Task WriteStreamAsync(string reference, Stream content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public IAsyncEnumerable<FolderItem> GetFolders(string path, CancellationToken ct = default)
            => AsyncEnumerable.Empty<FolderItem>();

        public IAsyncEnumerable<FileItem> GetFiles(string path, CancellationToken ct = default)
            => AsyncEnumerable.Empty<FileItem>();

        public Task SaveFileAsync(string path, string fileName, Stream content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CreateFolderAsync(string folderPath) => Task.CompletedTask;

        public Task DeleteFolderAsync(string folderPath) => Task.CompletedTask;

        public Task DeleteFileAsync(string filePath) => Task.CompletedTask;

        public IDisposable? AttachMonitor(Action<string> onChanged) => null;

        public Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ImmutableDictionary<string, Author>.Empty);
    }
}
