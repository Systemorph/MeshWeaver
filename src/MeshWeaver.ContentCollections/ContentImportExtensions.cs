using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Handler + fluent API for the canonical content import (<see cref="ImportContentRequest"/>):
/// copy a folder of files from a <b>source content collection</b> (e.g. the embedded
/// <c>DocContent</c>) into a node's target content collection (e.g. <c>content</c>) — collection to
/// collection, no disk staging. Posted to the OWNING node's hub, which has both collections + the
/// <see cref="IFileContentProvider"/>. Reuse this (e.g. the static-repo import's content sync); do
/// NOT hand-roll a cross-hub write or add a second <c>ImportContentRequest</c>.
/// </summary>
public static class ContentImportExtensions
{
    /// <summary>Begin a fluent content import targeting <paramref name="nodePath"/>'s hub.</summary>
    public static ContentImportBuilder ImportContent(this IMessageHub hub, string nodePath)
        => new(hub, nodePath);

    /// <summary>
    /// Registers the <see cref="ImportContentRequest"/> handler on a content-enabled hub. Wired into
    /// <c>AddContentCollectionsInfrastructure</c> so every node hub that maps a content collection
    /// can receive an import.
    /// </summary>
    internal static MessageHubConfiguration AddContentImportHandler(this MessageHubConfiguration config)
        => config.WithHandler<ImportContentRequest>(HandleImportContent);

    private static IMessageDelivery HandleImportContent(
        IMessageHub hub, IMessageDelivery<ImportContentRequest> delivery)
    {
        var request = delivery.Message;
        var contentService = hub.ServiceProvider.GetService<IContentService>();
        if (contentService is null)
        {
            hub.Post(ImportContentResponse.Fail("Content collections not configured on this node"),
                o => o.ResponseFor(delivery));
            return delivery.Processed();
        }
        if (string.IsNullOrEmpty(request.SourceCollection))
        {
            // Disk-source (SourcePath) is not implemented here; only collection-to-collection.
            hub.Post(ImportContentResponse.Fail("ImportContentRequest.SourceCollection is required"),
                o => o.ResponseFor(delivery));
            return delivery.Processed();
        }

        var pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
                   ?? IoPool.Unbounded;
        pool.Run(ct => CopyAsync(contentService, request, ct))
            .Subscribe(
                count => hub.Post(ImportContentResponse.Ok(count), o => o.ResponseFor(delivery)),
                ex => hub.Post(ImportContentResponse.Fail(ex.Message), o => o.ResponseFor(delivery)));

        return delivery.Processed();
    }

    private static async Task<int> CopyAsync(
        IContentService contentService, ImportContentRequest request, CancellationToken ct)
    {
        var source = await contentService.GetCollectionAsync(request.SourceCollection!, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Source content collection '{request.SourceCollection}' not found");
        var target = await contentService.GetCollectionAsync(request.CollectionName, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Target content collection '{request.CollectionName}' not found");

        var targetDir = (request.TargetPath ?? string.Empty).Trim('/');
        var count = 0;
        await foreach (var file in source.GetFiles(request.SourcePath, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            var stream = await source.GetContentAsync(file.Path, ct).ConfigureAwait(false);
            if (stream is null)
                continue;
            await using (stream.ConfigureAwait(false))
                await target.SaveFileAsync(targetDir, file.Name, stream).ConfigureAwait(false);
            count++;
        }
        return count;
    }
}

/// <summary>
/// Fluent builder for <see cref="ContentImportExtensions.ImportContent"/>:
/// <code>
/// hub.ImportContent("Doc/DataMesh/UnifiedPath")
///    .From("DocContent", "DataMesh/UnifiedPath")   // embedded source collection + folder
///    .To("content")                                 // target collection on the node (default root)
///    .Post()                                         // IObservable&lt;ImportContentResponse&gt;
/// </code>
/// </summary>
public sealed class ContentImportBuilder
{
    private readonly IMessageHub _hub;
    private readonly string _nodePath;
    private string _sourceCollection = "";
    private string _sourcePath = "";
    private string _targetCollection = "content";
    private string _targetPath = "";

    internal ContentImportBuilder(IMessageHub hub, string nodePath)
    {
        _hub = hub;
        _nodePath = nodePath;
    }

    /// <summary>Source content collection + folder within it to copy from.</summary>
    public ContentImportBuilder From(string sourceCollection, string sourcePath = "")
    {
        _sourceCollection = sourceCollection;
        _sourcePath = sourcePath;
        return this;
    }

    /// <summary>Target content collection (default <c>"content"</c>) + folder within it.</summary>
    public ContentImportBuilder To(string targetCollection, string targetPath = "")
    {
        _targetCollection = targetCollection;
        _targetPath = targetPath;
        return this;
    }

    /// <summary>Post the import to the owning node's hub. Cold — subscribe to run.</summary>
    public IObservable<ImportContentResponse> Post()
    {
        var request = new ImportContentRequest(_targetCollection, _sourcePath, _targetPath)
        {
            SourceCollection = _sourceCollection
        };
        var address = new Address(_nodePath);
        return Observable.Defer(() =>
        {
            var delivery = _hub.Post(request, o => o.WithTarget(address));
            return _hub.Observe(delivery)
                .Select(d => d.Message)
                .OfType<ImportContentResponse>()
                .Take(1);
        });
    }
}
