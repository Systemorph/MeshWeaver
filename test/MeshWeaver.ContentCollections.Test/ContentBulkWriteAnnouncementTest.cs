using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// The bulk-write half of the content-observer seam (<see cref="IContentUploadObserver.OnFilesWritten"/>).
/// A collection→collection import, a repository/GitSync content mirror and an
/// <see cref="IFileContentProvider.SaveFileContent"/> write used to announce NOTHING, so the files they
/// wrote were never indexed (MeshWeaver.Plugins <c>Indexing/ContentIndexingCoverage</c>, "Which writes
/// reach the pipeline"). Each now announces ONCE when it has committed — once for the whole write,
/// never once per file, because a boot re-import re-sends every content file of a repository and the
/// content index opens one activity per announcement (<c>Doc/Architecture/ContentRoute503</c>).
/// </summary>
public class ContentBulkWriteAnnouncementTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string NodePath = "host/1";

    private readonly RecordingObserver recorder = new();

    private readonly string contentPath = Path.Combine(
        AppContext.BaseDirectory, "Files", "BulkWriteAnnouncement", Guid.NewGuid().ToString("N")[..8], "content");

    private readonly string sourcePath = Path.Combine(
        AppContext.BaseDirectory, "Files", "BulkWriteAnnouncement", Guid.NewGuid().ToString("N")[..8], "source");

    private static readonly Type[] WireTypes =
    [
        typeof(SyncContentFilesRequest), typeof(InlineContentFile), typeof(ImportContentResponse),
        typeof(ImportContentRequest),
    ];

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration configuration)
        => base.ConfigureMesh(configuration).WithTypes(WireTypes);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        Directory.CreateDirectory(contentPath);
        Directory.CreateDirectory(sourcePath);
        return base.ConfigureHost(configuration)
            .WithTypes(WireTypes)
            .AddContentCollections()
            .AddFileSystemContentCollection("content", _ => contentPath)
            .AddFileSystemContentCollection("source", _ => sourcePath)
            .WithServices(services => services.AddSingleton<IContentUploadObserver>(sp =>
            {
                recorder.Bind(sp.GetRequiredService<AccessService>());
                return recorder;
            }));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).WithTypes(WireTypes);

    private static InlineContentFile Text(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task A_content_sync_is_announced_once_with_every_file_it_wrote()
    {
        var response = await GetClient().SyncContentFiles(NodePath)
            .To("content", "docs")
            .Add([Text("a.md", "# A"), Text("nested/b.txt", "b"), Text("c.yaml", "c: 1")])
            .Mirror(false)
            .Post()
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync().Await(TestContext.Current.CancellationToken);
        response.Success.Should().BeTrue(response.Error ?? string.Empty);

        var announcement = recorder.Written.Should().ContainSingle(
            "a sync is announced ONCE — the observer opens one unit of work per announcement").Which;
        announcement.CollectionPath.Should().Be($"{NodePath}/content",
            "the announcement is keyed exactly as an upload and a delete of the same files are");
        announcement.FilePaths.Order(StringComparer.Ordinal).Should().Equal(["docs/a.md", "docs/c.yaml", "docs/nested/b.txt"]);
        recorder.Uploaded.Should().BeEmpty("the bulk write is never fanned out into per-file upload notifications");
    }

    [Fact]
    public async Task A_sync_split_across_deliveries_is_still_announced_once()
    {
        // 8 × 300,000 bytes is ~3.2 MB packaged: several deliveries (ContentSyncIsNeverBuiltWholeTest).
        var files = Enumerable.Range(0, 8)
            .Select(i => new InlineContentFile($"videos/asset-{i}.mp4", new byte[300_000]))
            .ToArray();

        var response = await GetClient().SyncContentFiles(NodePath)
            .To("content")
            .Add(files)
            .Mirror(true)
            .Post()
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync().Await(TestContext.Current.CancellationToken);
        response.Success.Should().BeTrue(response.Error ?? string.Empty);
        response.FilesImported.Should().Be(files.Length);

        recorder.Written.Should().ContainSingle(
                "the deliveries of one sync defer to the last, which announces the whole set once")
            .Which.FilePaths.Order(StringComparer.Ordinal).Should().Equal(files.Select(f => f.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_collection_import_is_announced_once_with_every_file_it_copied()
    {
        Directory.CreateDirectory(sourcePath);
        await File.WriteAllTextAsync(Path.Combine(sourcePath, "one.md"), "# One", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(sourcePath, "two.log"), "line", TestContext.Current.CancellationToken);

        var response = await GetClient().ImportContent(NodePath)
            .From("source")
            .To("content", "imported")
            .Post()
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync().Await(TestContext.Current.CancellationToken);
        response.Success.Should().BeTrue(response.Error ?? string.Empty);
        response.FilesImported.Should().Be(2);

        var announcement = recorder.Written.Should().ContainSingle().Which;
        announcement.CollectionPath.Should().Be($"{NodePath}/content");
        announcement.FilePaths.Order(StringComparer.Ordinal).Should().Equal(["imported/one.md", "imported/two.log"]);
    }

    [Fact]
    public async Task A_file_content_provider_write_is_announced_as_its_writer()
    {
        var host = GetHost();
        var provider = host.ServiceProvider.GetRequiredService<IFileContentProvider>();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();

        FileOperationResult result;
        using (accessService.SwitchAccessContext(new AccessContext { ObjectId = "writer", Name = "Writer" }))
        {
            var write = provider.SaveFileContent("content", "notes/agent.md", new MemoryStream("# Agent notes"u8.ToArray()));
            result = await write.FirstAsync().Await(TestContext.Current.CancellationToken);
        }
        result.Success.Should().BeTrue(result.Error ?? string.Empty);

        var announcement = recorder.Written.Should().ContainSingle().Which;
        announcement.FilePaths.Should().Equal(["notes/agent.md"]);
        announcement.ObjectId.Should().Be("writer",
            "what the index derives from the file is attributed to the identity that wrote it");
    }

    [Fact]
    public async Task A_refused_sync_announces_nothing()
    {
        // The control: an unsafe path refuses the whole request before a byte is written.
        var response = await GetClient().SyncContentFiles(NodePath)
            .To("content")
            .Add([Text("../escape.txt", "no")])
            .Mirror(false)
            .Post()
            .Timeout(TestTimeouts.Convergence)
            .FirstAsync()
            .Catch((Exception ex) => Observable.Return(ImportContentResponse.Fail(ex.Message)))
            .Await(TestContext.Current.CancellationToken);

        response.Success.Should().BeFalse();
        recorder.Written.Should().BeEmpty("only a COMMITTED write is announced");
    }

    private sealed record WriteNotification(string CollectionPath, ImmutableList<string> FilePaths, string? ObjectId);

    private sealed class RecordingObserver : IContentUploadObserver
    {
        private ImmutableList<WriteNotification> written = ImmutableList<WriteNotification>.Empty;
        private ImmutableList<string> uploaded = ImmutableList<string>.Empty;
        private AccessService? accessService;

        public ImmutableList<WriteNotification> Written => written;

        public ImmutableList<string> Uploaded => uploaded;

        public void Bind(AccessService service) => accessService = service;

        public void OnUploaded(string collectionPath, string filePath)
            => ImmutableInterlocked.Update(ref uploaded, list => list.Add($"{collectionPath}|{filePath}"));

        public void OnFilesWritten(string collectionPath, IReadOnlyList<string> filePaths)
            => ImmutableInterlocked.Update(ref written, list => list.Add(
                new WriteNotification(collectionPath, filePaths.ToImmutableList(), accessService?.Context?.ObjectId)));
    }
}
