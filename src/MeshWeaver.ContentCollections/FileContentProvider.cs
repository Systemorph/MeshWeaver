using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Threading;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Implementation of <see cref="IFileContentProvider"/> that uses <see cref="IContentService"/> to access file content.
/// Automatically converts binary documents (.docx, .pptx, etc.) to markdown via <see cref="IContentTransformer"/>.
/// All public methods return <see cref="IObservable{T}"/>; the I/O leaf is pushed onto the file-system
/// <see cref="IIoPool"/> (see <see cref="IoPoolExtensions.Run{T}"/>) so the await continuations resume on the
/// pool — never on a captured/blocking subscriber scheduler — and cannot deadlock under a blocking consumer.
/// </summary>
public class FileContentProvider : IFileContentProvider
{
    private readonly IContentService contentService;
    private readonly IEnumerable<IContentTransformer> transformers;
    private readonly IIoPool _ioPool;

    /// <summary>
    /// Initializes the provider with the content service, the available document transformers,
    /// and the file-system I/O pool used to run file access off the hub.
    /// </summary>
    /// <param name="contentService">The service used to resolve collections and file streams.</param>
    /// <param name="transformers">Transformers that convert binary documents to markdown.</param>
    /// <param name="ioPoolRegistry">Registry supplying the file-system I/O pool; falls back to an unbounded pool when <c>null</c>.</param>
    public FileContentProvider(
        IContentService contentService,
        IEnumerable<IContentTransformer> transformers,
        IoPoolRegistry? ioPoolRegistry = null)
    {
        this.contentService = contentService;
        this.transformers = transformers;
        _ioPool = ioPoolRegistry?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
    }

    /// <inheritdoc />
    public IObservable<FileContentResult> GetFileContent(
        string collectionName,
        string filePath,
        int? numberOfRows = null) =>
        _ioPool.Run(ct => GetFileContentCore(collectionName, filePath, numberOfRows, ct));

    private async Task<FileContentResult> GetFileContentCore(
        string collectionName,
        string filePath,
        int? numberOfRows,
        CancellationToken ct)
    {
        try
        {
            var collection = await contentService.GetCollectionAsync(collectionName, ct).ConfigureAwait(false);
            if (collection == null)
                return FileContentResult.Fail($"Content collection '{collectionName}' not found");

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var transformer = transformers.FirstOrDefault(t => t.SupportedExtensions.Contains(ext));
            if (transformer != null)
            {
                await using var stream = await collection.GetContentAsync(filePath, ct).ConfigureAwait(false);
                if (stream == null)
                    return FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'");

                var markdown = await transformer.TransformToMarkdownAsync(stream, ct).ConfigureAwait(false);
                return FileContentResult.Ok(markdown);
            }

            await using var textStream = await collection.GetContentAsync(filePath, ct).ConfigureAwait(false);
            if (textStream == null)
                return FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'");

            using var reader = new StreamReader(textStream);
            string content;
            if (numberOfRows.HasValue)
            {
                var sb = new StringBuilder();
                var linesRead = 0;
                while (linesRead < numberOfRows.Value)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null)
                        break;
                    sb.AppendLine(line);
                    linesRead++;
                }
                content = sb.ToString();
            }
            else
            {
                content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            return FileContentResult.Ok(content);
        }
        catch (FileNotFoundException)
        {
            return FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'");
        }
        catch (Exception ex)
        {
            return FileContentResult.Fail($"Error accessing file '{filePath}' from collection '{collectionName}': {ex.Message}");
        }
    }

    /// <inheritdoc />
    public IObservable<FileOperationResult> SaveFileContent(
        string collectionName,
        string filePath,
        Stream content) =>
        _ioPool.Run(ct => SaveFileContentCore(collectionName, filePath, content, ct));

    private async Task<FileOperationResult> SaveFileContentCore(
        string collectionName,
        string filePath,
        Stream content,
        CancellationToken ct)
    {
        try
        {
            var collection = await contentService.GetCollectionAsync(collectionName, ct).ConfigureAwait(false);
            if (collection == null)
                return FileOperationResult.Fail($"Content collection '{collectionName}' not found");

            var directory = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
            var fileName = Path.GetFileName(filePath);

            await collection.SaveFileAsync(directory, fileName, content).ConfigureAwait(false);
            return FileOperationResult.Ok();
        }
        catch (Exception ex)
        {
            return FileOperationResult.Fail($"Error saving file '{filePath}' to collection '{collectionName}': {ex.Message}");
        }
    }

    /// <inheritdoc />
    public IObservable<FileOperationResult> DeleteFile(
        string collectionName,
        string filePath) =>
        _ioPool.Run(ct => DeleteFileCore(collectionName, filePath, ct));

    private async Task<FileOperationResult> DeleteFileCore(
        string collectionName,
        string filePath,
        CancellationToken ct)
    {
        try
        {
            var collection = await contentService.GetCollectionAsync(collectionName, ct).ConfigureAwait(false);
            if (collection == null)
                return FileOperationResult.Fail($"Content collection '{collectionName}' not found");

            await collection.DeleteFileAsync(filePath).ConfigureAwait(false);
            return FileOperationResult.Ok();
        }
        catch (Exception ex)
        {
            return FileOperationResult.Fail($"Error deleting file '{filePath}' from collection '{collectionName}': {ex.Message}");
        }
    }

    /// <inheritdoc />
    public IObservable<CollectionListingResult> ListCollectionItems(
        string collectionName,
        string path) =>
        _ioPool.Run(ct => ListCollectionItemsCore(collectionName, path, ct));

    private async Task<CollectionListingResult> ListCollectionItemsCore(
        string collectionName,
        string path,
        CancellationToken ct)
    {
        try
        {
            var collection = await contentService.GetCollectionAsync(collectionName, ct).ConfigureAwait(false);
            if (collection == null)
                return CollectionListingResult.Fail($"Content collection '{collectionName}' not found");

            var items = new List<CollectionItemInfo>();
            await foreach (var item in collection.GetCollectionItems(path, ct).ConfigureAwait(false))
            {
                items.Add(new CollectionItemInfo(
                    item.Path,
                    item.Name,
                    item is FolderItem,
                    item is FileItem fileItem ? fileItem.LastModified : null));
            }

            return CollectionListingResult.Ok(items);
        }
        catch (Exception ex)
        {
            return CollectionListingResult.Fail($"Error listing collection '{collectionName}' at path '{path}': {ex.Message}");
        }
    }
}
