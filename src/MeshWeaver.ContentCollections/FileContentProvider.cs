using System.Reactive.Linq;
using MeshWeaver.Data;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Implementation of <see cref="IFileContentProvider"/> that uses <see cref="IContentService"/> to access file content.
/// Automatically converts binary documents (.docx, .pptx, etc.) to markdown via <see cref="IContentTransformer"/>.
/// Pure reactive composition over the (pooled) observable <see cref="ContentCollection"/> surface —
/// every I/O leaf runs on the collection's own <c>IIoPool</c>, never on the subscriber's thread.
/// </summary>
public class FileContentProvider : IFileContentProvider
{
    private readonly IContentService contentService;
    private readonly IEnumerable<IContentTransformer> transformers;

    /// <summary>
    /// Raster image extensions → media type. A file with one of these and no registered
    /// <see cref="IContentTransformer"/> is guarded (a descriptive placeholder is returned) rather than
    /// having its raw bytes decoded as text. Read-only constant lookup — never mutated at runtime.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ImageMediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".tiff"] = "image/tiff",
            [".tif"] = "image/tiff"
        };

    /// <summary>
    /// Initializes the provider with the content service and the available document transformers.
    /// </summary>
    /// <param name="contentService">The service used to resolve collections and file streams.</param>
    /// <param name="transformers">Transformers that convert binary documents to markdown.</param>
    public FileContentProvider(
        IContentService contentService,
        IEnumerable<IContentTransformer> transformers)
    {
        this.contentService = contentService;
        this.transformers = transformers;
    }

    /// <inheritdoc />
    public IObservable<FileContentResult> GetFileContent(
        string collectionName,
        string filePath,
        int? numberOfRows = null) =>
        contentService.GetCollection(collectionName)
            .SelectMany(collection =>
            {
                if (collection == null)
                    return Observable.Return(
                        FileContentResult.Fail($"Content collection '{collectionName}' not found"));

                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var transformer = transformers.FirstOrDefault(t => t.SupportedExtensions.Contains(ext));
                if (transformer != null)
                    return collection.GetContentAsText(filePath, [transformer])
                        .Select(markdown => markdown is null
                            ? FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'")
                            : FileContentResult.Ok(markdown));

                if (ImageMediaTypes.TryGetValue(ext, out var imageMediaType))
                    // A binary image has no text transformer. NEVER decode its bytes as text (issue #379 —
                    // multi-MB of binary floods a model context and fails the next request with 400). Return
                    // a short descriptive placeholder instead. When content indexing is enabled, an AI
                    // description of the image is surfaced through the file's Document node.
                    return collection.GetContentSize(filePath)
                        .Select(size =>
                        {
                            if (size is null)
                                return FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'");
                            var sizeText = size >= 0 ? $", {size} bytes" : string.Empty;
                            return FileContentResult.Ok(
                                $"[image: {Path.GetFileName(filePath)} ({imageMediaType}{sizeText})] "
                                + "Binary image content is not returned as text. When content indexing is enabled, an "
                                + "AI-generated description is available on the file's Document node.");
                        });

                return collection.GetContentAsText(filePath, null, numberOfRows)
                    .Select(content => content is null
                        ? FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'")
                        : FileContentResult.Ok(content));
            })
            .Catch((FileNotFoundException _) => Observable.Return(
                FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'")))
            .Catch((Exception ex) => Observable.Return(
                FileContentResult.Fail($"Error accessing file '{filePath}' from collection '{collectionName}': {ex.Message}")));

    /// <inheritdoc />
    public IObservable<FileOperationResult> SaveFileContent(
        string collectionName,
        string filePath,
        Stream content) =>
        contentService.GetCollection(collectionName)
            .SelectMany(collection =>
            {
                if (collection == null)
                    return Observable.Return(
                        FileOperationResult.Fail($"Content collection '{collectionName}' not found"));

                var directory = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
                var fileName = Path.GetFileName(filePath);
                return collection.SaveFile(directory, fileName, content)
                    .Select(_ => FileOperationResult.Ok());
            })
            .Catch((Exception ex) => Observable.Return(
                FileOperationResult.Fail($"Error saving file '{filePath}' to collection '{collectionName}': {ex.Message}")));

    /// <inheritdoc />
    public IObservable<FileOperationResult> DeleteFile(
        string collectionName,
        string filePath) =>
        contentService.GetCollection(collectionName)
            .SelectMany(collection => collection == null
                ? Observable.Return(FileOperationResult.Fail($"Content collection '{collectionName}' not found"))
                : collection.DeleteFile(filePath).Select(_ => FileOperationResult.Ok()))
            .Catch((Exception ex) => Observable.Return(
                FileOperationResult.Fail($"Error deleting file '{filePath}' from collection '{collectionName}': {ex.Message}")));

    /// <inheritdoc />
    public IObservable<CollectionListingResult> ListCollectionItems(
        string collectionName,
        string path) =>
        contentService.GetCollection(collectionName)
            .SelectMany(collection => collection == null
                ? Observable.Return(CollectionListingResult.Fail($"Content collection '{collectionName}' not found"))
                : collection.GetCollectionItems(path)
                    .Select(item => new CollectionItemInfo(
                        item.Path,
                        item.Name,
                        item is FolderItem,
                        item is FileItem fileItem ? fileItem.LastModified : null))
                    .ToArray()
                    .Select(items => CollectionListingResult.Ok(items)))
            .Catch((Exception ex) => Observable.Return(
                CollectionListingResult.Fail($"Error listing collection '{collectionName}' at path '{path}': {ex.Message}")));
}
