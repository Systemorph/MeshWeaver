using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.ContentCollections.Test;

/// <summary>
/// Pins the delete half of the content-observer seam (MeshWeaver.Plugins#2605). Deleting a file
/// used to notify nothing, so the content index kept the file's chunks, hash row and
/// <c>Document</c> node, and the deleted text stayed searchable. Every delete path in the platform
/// ends in <see cref="ContentCollection.DeleteFile"/> or <see cref="ContentCollection.DeleteFolder"/>,
/// so the collection itself raises <see cref="IContentUploadObserver.OnDeleted"/> — with the same
/// qualified collection path an upload is keyed by, the caller's identity, and one notification per
/// file for a folder.
/// </summary>
public class ContentDeleteNotificationTest(ITestOutputHelper output) : HubTestBase(output)
{
    private readonly RecordingObserver recorder = new();

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddContentCollections()
            .WithServices(services => services.AddSingleton<IContentUploadObserver>(sp =>
            {
                recorder.Bind(sp.GetRequiredService<AccessService>());
                return recorder;
            }));

    private ContentCollection CreateCollection(Address? address = null)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Files", "DeleteNotification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new ContentCollection(
            new ContentCollectionConfig
            {
                Name = "content",
                SourceType = "FileSystem",
                IsEditable = true,
                BasePath = dir,
                Address = address,
            },
            new FileSystemStreamProvider(dir),
            GetHost());
    }

    [Fact]
    public async Task Deleting_a_file_notifies_observers_with_the_qualified_collection_path()
    {
        var ct = TestContext.Current.CancellationToken;
        var collection = CreateCollection((Address)"ACME");
        await collection.Initialize().FirstAsync().Await(ct);
        await collection.SaveFile("/docs", "secret.txt", () => new MemoryStream("confidential"u8.ToArray())).Await(ct);
        recorder.Deleted.Should().BeEmpty("nothing has been deleted yet");

        var accessService = GetHost().ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.SwitchAccessContext(new AccessContext { ObjectId = "deleter", Name = "Deleter" }))
            await collection.DeleteFile("/docs/secret.txt").Await(ct);

        recorder.Deleted.Should().ContainSingle().Which.Should().Be(
            new DeleteNotification("ACME/content", "docs/secret.txt", "deleter"),
            "the index is keyed by the qualified collection path and the root-relative file path, and the "
            + "clean-up must run as the user who deleted the file");
    }

    [Fact]
    public async Task Deleting_a_folder_notifies_observers_once_per_contained_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var collection = CreateCollection((Address)"ACME");
        await collection.Initialize().FirstAsync().Await(ct);
        await collection.SaveFile("/folder", "a.txt", () => new MemoryStream("a"u8.ToArray())).Await(ct);
        await collection.SaveFile("/folder/nested", "b.txt", () => new MemoryStream("b"u8.ToArray())).Await(ct);
        await collection.SaveFile("/", "keep.txt", () => new MemoryStream("k"u8.ToArray())).Await(ct);

        await collection.DeleteFolder("/folder").Await(ct);

        recorder.Deleted.Select(d => $"{d.CollectionPath}|{d.FilePath}").OrderBy(x => x, StringComparer.Ordinal)
            .Should().Equal(["ACME/content|folder/a.txt", "ACME/content|folder/nested/b.txt"],
            "a folder delete removes every file below it, and each one's derived index must go too — "
            + "while a file outside the folder is not reported");
    }

    [Fact]
    public async Task A_collection_already_qualified_by_its_address_is_not_qualified_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(AppContext.BaseDirectory, "Files", "DeleteNotification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var collection = new ContentCollection(
            new ContentCollectionConfig
            {
                Name = "ACME/content",
                SourceType = "FileSystem",
                IsEditable = true,
                BasePath = dir,
                Address = (Address)"ACME",
            },
            new FileSystemStreamProvider(dir),
            GetHost());
        await collection.Initialize().FirstAsync().Await(ct);
        await collection.SaveFile("/", "x.txt", () => new MemoryStream("x"u8.ToArray())).Await(ct);

        await collection.DeleteFile("x.txt").Await(ct);

        recorder.Deleted.Should().ContainSingle().Which.CollectionPath.Should().Be("ACME/content",
            "MeshOperations.Upload registers collections under their qualified name, and the upload "
            + "notification uses that name unchanged");
    }

    private sealed record DeleteNotification(string CollectionPath, string FilePath, string? ObjectId);

    private sealed class RecordingObserver : IContentUploadObserver
    {
        private ImmutableList<DeleteNotification> deleted = ImmutableList<DeleteNotification>.Empty;
        private AccessService? accessService;

        public ImmutableList<DeleteNotification> Deleted => deleted;

        public void Bind(AccessService service) => accessService = service;

        public void OnUploaded(string collectionPath, string filePath) { }

        public void OnDeleted(string collectionPath, string filePath)
            => ImmutableInterlocked.Update(ref deleted,
                list => list.Add(new DeleteNotification(collectionPath, filePath, accessService?.Context?.ObjectId)));
    }
}
