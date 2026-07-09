using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Layout area matched by the MOUNTED collection name itself: <c>/{node}/{collection}/{path…}</c>.
/// A collection can be mounted under any name — the URL segment IS the configured name (encoded
/// with <see cref="ContentCollectionsExtensions.EncodeCollectionName"/>), never a hard-coded word.
/// A folder path renders the file browser scoped to that folder (mirroring its position back into
/// the URL); a file path renders the file's content.
/// </summary>
public static class CollectionNamedLayoutArea
{
    /// <summary>
    /// True when <paramref name="context"/>'s area names a collection mounted on
    /// <paramref name="hub"/>. <c>$</c>-prefixed framework areas never match. A collection
    /// deliberately mounted under the name of a registered layout area shadows that area —
    /// don't mount collections under area names like <c>Overview</c> or <c>Settings</c>, nor
    /// under the reserved URL keywords (<c>area</c>/<c>data</c>/<c>schema</c>/<c>model</c>,
    /// which the URL parser claims before area matching).
    /// </summary>
    public static bool IsCollectionArea(IMessageHub hub, RenderingContext context)
    {
        var area = context.Area;
        if (string.IsNullOrEmpty(area) || area.StartsWith('$'))
            return false;
        var contentService = hub.ServiceProvider.GetService<IContentService>();
        return contentService?.GetCollectionConfig(
            ContentCollectionsExtensions.DecodeCollectionName(area)) is not null;
    }

    /// <summary>
    /// Renders the collection position addressed by the area id: empty id or a folder path →
    /// the file browser at that folder; a file path → the file's content.
    /// </summary>
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext context)
        // Defer so a synchronously-throwing prologue surfaces through the rendered-error path.
        => Observable.Defer(() => RenderCore(host, context))
            .Catch((Exception ex) => Observable.Return<UiControl?>(
                new MarkdownControl($"Error loading collection '{context.Area}': {ex.Message}")));

    private static IObservable<UiControl?> RenderCore(LayoutAreaHost host, RenderingContext context)
    {
        var collectionName = ContentCollectionsExtensions.DecodeCollectionName(context.Area);
        var idString = host.Reference.Id?.ToString() ?? "";
        // The URL id is percent-encoded by the browser; decode each segment before it becomes a
        // collection path so names with spaces (or other reserved chars) match the stored items.
        var path = ContentCollectionsExtensions.DecodeCollectionPath(idString.Split('?')[0].Trim('/'));

        var contentService = host.Hub.GetContentService();
        var ioPool = ContentLayoutArea.GetIoPool(host.Hub);
        return ioPool
            .Invoke(ct => contentService.GetCollectionAsync(collectionName, ct))
            .SelectMany(collection =>
            {
                if (collection is null)
                    return Observable.Return<UiControl?>(
                        new MarkdownControl($"Collection '{collectionName}' not found."));

                if (path.Length == 0)
                    return Observable.Return<UiControl?>(Browser(host, contentService, collectionName, "/"));

                // Type the target by listing its parent folder — the same listing the browser
                // itself performs — so folders and files are told apart by the provider, not by
                // an extension heuristic.
                var lastSlash = path.LastIndexOf('/');
                var parent = lastSlash < 0 ? "/" : $"/{path[..lastSlash]}";
                return ioPool
                    .Invoke<CollectionItem?>(async ct =>
                    {
                        await foreach (var item in collection.GetCollectionItems(parent, ct).ConfigureAwait(false))
                            // Item paths can carry '\' separators on Windows (FileSystem provider).
                            if (string.Equals(item.Path.Replace('\\', '/').Trim('/'), path, StringComparison.OrdinalIgnoreCase))
                                return item;
                        return null;
                    })
                    .SelectMany(item => item switch
                    {
                        FolderItem => Observable.Return<UiControl?>(
                            Browser(host, contentService, collectionName, $"/{path}")),
                        FileItem => ContentLayoutArea.RenderFile(host, collection, collectionName, path),
                        _ => Observable.Return<UiControl?>(
                            new MarkdownControl($"'{path}' not found in collection '{collectionName}'.")),
                    });
            });
    }

    /// <summary>
    /// The file browser scoped to <paramref name="path"/>, mirroring navigation back into this
    /// collection's URL space (<c>/{node}/{collection}</c>).
    /// </summary>
    private static UiControl Browser(
        LayoutAreaHost host, IContentService contentService, string collectionName, string path)
    {
        var browser = new FileBrowserControl(collectionName)
            .WithPath(path)
            .WithUrlBasePath(
                $"/{host.Hub.Address}/{ContentCollectionsExtensions.EncodeCollectionName(collectionName)}");
        var config = contentService.GetCollectionConfig(collectionName);
        if (config is not null)
            browser = browser
                .WithCollectionConfiguration(config)
                .WithCollectionInfo(config.SourceType, config.BasePath, config.Settings);
        return browser;
    }
}
