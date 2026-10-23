using System.Reactive.Linq;
using MeshWeaver.Data;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Implementation of <see cref="IFileContentProvider"/> that uses <see cref="IContentService"/> to access file content.
/// Converts binary documents to markdown via a registered <see cref="IContentTransformer"/>; any other
/// binary file is answered with a named placeholder and any text read is bounded (issue #7064).
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

                // 🚨 The staging folder is framework state, never content (#6585): a staged blob is
                // bytes the owner has not admitted yet, so it is answered exactly as an absent file.
                if (ContentStaging.IsStagingPath(NormalizedPath(filePath)))
                    return Observable.Return(
                        FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'"));

                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var transformer = transformers.FirstOrDefault(t => t.SupportedExtensions.Contains(ext));
                if (transformer != null)
                    return collection.GetContentAsText(filePath, [transformer])
                        .Select(markdown => markdown is null
                            ? FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'")
                            : FileContentResult.Ok(BoundConverted(filePath, markdown)));

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

                // Everything else is read BOUNDED and BINARY-SAFE (issue #7064): a binary file (a .pptx is a
                // ZIP container) is never decoded into text — its raw bytes reached an agent's conversation
                // and the next model request was refused for its size; below that cap the model would have
                // worked from garbage. A text file larger than the bound returns its head plus a marker
                // stating the total size — never a silent cut.
                return collection.GetBoundedText(filePath, MaxTextBytes, numberOfRows)
                    .Select(read => read is null
                        ? FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'")
                        : FileContentResult.Ok(Describe(filePath, read)));
            })
            .Catch((FileNotFoundException _) => Observable.Return(
                FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'")))
            .Catch((Exception ex) => Observable.Return(
                FileContentResult.Fail($"Error accessing file '{filePath}' from collection '{collectionName}': {ex.Message}")));

    /// <summary>
    /// The most bytes of a file a text read decodes and returns (256 KiB — tens of thousands of model
    /// tokens). A larger file returns its head followed by <see cref="TruncationMarkerPrefix"/> and the
    /// total size. The same bound, in characters, applies to a transformer's converted text.
    /// </summary>
    public const int MaxTextBytes = 256 * 1024;

    /// <summary>The opening of the marker appended to a text read cut at <see cref="MaxTextBytes"/>.</summary>
    public const string TruncationMarkerPrefix = "[truncated: ";

    /// <summary>The opening of the placeholder returned instead of a binary file's bytes.</summary>
    public const string BinaryPlaceholderPrefix = "[binary file: ";

    private static string Describe(string filePath, BoundedTextRead read)
    {
        var name = Path.GetFileName(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (read.IsBinary)
        {
            var (mediaType, description) = BinaryContentSniffer.Describe(ext);
            // A size the store does not report (-1) is said to be unknown, never printed as a byte count.
            var sizeText = read.TotalBytes >= 0 ? $"{read.TotalBytes} bytes" : "size unknown";
            return $"{BinaryPlaceholderPrefix}{name} ({description}, {mediaType}, {sizeText})] "
                + $"Its content is not returned as text because {read.BinaryReason}, and no text converter is "
                + $"registered for '{(ext.Length > 0 ? ext : name)}'. To read it, convert it to a text format "
                + "(for example export it as PDF, Markdown or CSV) and read that file, or process it server-side "
                + "in a script that opens the collection stream.";
        }

        if (!read.Truncated)
            return read.Text ?? string.Empty;

        var totalText = read.TotalBytes >= 0
            ? $"{name} is {read.TotalBytes} bytes"
            : $"{name} is larger than {read.ReturnedBytes} bytes (total size unknown)";
        return read.Text
            + $"\n\n{TruncationMarkerPrefix}{totalText}; only the first {read.ReturnedBytes} "
            + $"bytes are returned (limit {MaxTextBytes} bytes). Read fewer rows or split the file to see the rest.]";
    }

    private static string BoundConverted(string filePath, string markdown)
        => markdown.Length <= MaxTextBytes
            ? markdown
            // Never split a surrogate pair at the cut.
            : markdown[..(char.IsHighSurrogate(markdown[MaxTextBytes - 1]) ? MaxTextBytes - 1 : MaxTextBytes)]
              + $"\n\n{TruncationMarkerPrefix}the converted text of {Path.GetFileName(filePath)} is {markdown.Length} "
              + $"characters; only the first {MaxTextBytes} characters are returned.]";

    // A collection-relative path as ContentStaging.IsStagingPath reads it: forward slashes, no
    // leading slash (a FileSystem provider can answer '\' separators on Windows).
    private static string NormalizedPath(string? path) => (path ?? "").Replace('\\', '/').TrimStart('/');

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
                // 🚨 The staging folder lists like an absent one and is never listed itself (#6585).
                : ContentStaging.IsStagingPath(NormalizedPath(path))
                ? Observable.Return(CollectionListingResult.Fail(
                    $"Error listing collection '{collectionName}' at path '{path}': not found"))
                : collection.GetCollectionItems(path)
                    .Where(item => !ContentStaging.IsStagingPath(NormalizedPath(item.Path)))
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
