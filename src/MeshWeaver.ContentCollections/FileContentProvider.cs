using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Data;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Implementation of <see cref="IFileContentProvider"/> that uses <see cref="IContentService"/> to access file content.
/// Automatically converts binary documents (.docx, .pptx, etc.) to markdown via <see cref="IContentTransformer"/>.
/// All public methods return <see cref="IObservable{T}"/>; the I/O bridge sits inside each method body
/// via <see cref="Observable.FromAsync{TResult}(Func{System.Threading.CancellationToken, Task{TResult}})"/> —
/// the innermost async edge.
/// </summary>
public class FileContentProvider : IFileContentProvider
{
    private readonly IContentService contentService;
    private readonly IEnumerable<IContentTransformer> transformers;

    public FileContentProvider(IContentService contentService, IEnumerable<IContentTransformer> transformers)
    {
        this.contentService = contentService;
        this.transformers = transformers;
    }

    public IObservable<FileContentResult> GetFileContent(
        string collectionName,
        string filePath,
        int? numberOfRows = null) =>
        Observable.FromAsync(async ct =>
        {
            try
            {
                var collection = await contentService.GetCollectionAsync(collectionName, ct);
                if (collection == null)
                    return FileContentResult.Fail($"Content collection '{collectionName}' not found");

                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var transformer = transformers.FirstOrDefault(t => t.SupportedExtensions.Contains(ext));
                if (transformer != null)
                {
                    await using var stream = await collection.GetContentAsync(filePath, ct);
                    if (stream == null)
                        return FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'");

                    var markdown = await transformer.TransformToMarkdownAsync(stream, ct);
                    return FileContentResult.Ok(markdown);
                }

                await using var textStream = await collection.GetContentAsync(filePath, ct);
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
                        var line = await reader.ReadLineAsync(ct);
                        if (line is null)
                            break;
                        sb.AppendLine(line);
                        linesRead++;
                    }
                    content = sb.ToString();
                }
                else
                {
                    content = await reader.ReadToEndAsync(ct);
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
        });

    public IObservable<FileOperationResult> SaveFileContent(
        string collectionName,
        string filePath,
        Stream content) =>
        Observable.FromAsync(async ct =>
        {
            try
            {
                var collection = await contentService.GetCollectionAsync(collectionName, ct);
                if (collection == null)
                    return FileOperationResult.Fail($"Content collection '{collectionName}' not found");

                var directory = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
                var fileName = Path.GetFileName(filePath);

                await collection.SaveFileAsync(directory, fileName, content);
                return FileOperationResult.Ok();
            }
            catch (Exception ex)
            {
                return FileOperationResult.Fail($"Error saving file '{filePath}' to collection '{collectionName}': {ex.Message}");
            }
        });

    public IObservable<FileOperationResult> DeleteFile(
        string collectionName,
        string filePath) =>
        Observable.FromAsync(async ct =>
        {
            try
            {
                var collection = await contentService.GetCollectionAsync(collectionName, ct);
                if (collection == null)
                    return FileOperationResult.Fail($"Content collection '{collectionName}' not found");

                await collection.DeleteFileAsync(filePath);
                return FileOperationResult.Ok();
            }
            catch (Exception ex)
            {
                return FileOperationResult.Fail($"Error deleting file '{filePath}' from collection '{collectionName}': {ex.Message}");
            }
        });

    public IObservable<CollectionListingResult> ListCollectionItems(
        string collectionName,
        string path) =>
        Observable.FromAsync(async ct =>
        {
            try
            {
                var collection = await contentService.GetCollectionAsync(collectionName, ct);
                if (collection == null)
                    return CollectionListingResult.Fail($"Content collection '{collectionName}' not found");

                var items = new List<CollectionItemInfo>();
                await foreach (var item in collection.GetCollectionItems(path, ct))
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
        });
}
