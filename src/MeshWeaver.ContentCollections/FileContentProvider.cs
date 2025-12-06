using System.Text;
using MeshWeaver.Data;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Implementation of IFileContentProvider that uses IContentService to access file content.
/// </summary>
public class FileContentProvider : IFileContentProvider
{
    private readonly IContentService contentService;

    public FileContentProvider(IContentService contentService)
    {
        this.contentService = contentService;
    }

    public async Task<FileContentResult> GetFileContentAsync(
        string collectionName,
        string filePath,
        int? numberOfRows = null,
        CancellationToken ct = default)
    {
        try
        {
            var collection = await contentService.GetCollectionAsync(collectionName, ct);
            if (collection == null)
            {
                return FileContentResult.Fail($"Content collection '{collectionName}' not found");
            }

            await using var stream = await collection.GetContentAsync(filePath, ct);
            if (stream == null)
            {
                return FileContentResult.Fail($"File '{filePath}' not found in collection '{collectionName}'");
            }

            // Read file as text
            using var reader = new StreamReader(stream);
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
    }

    public async Task<FileOperationResult> SaveFileContentAsync(
        string collectionName,
        string filePath,
        Stream content,
        CancellationToken ct = default)
    {
        try
        {
            var collection = await contentService.GetCollectionAsync(collectionName, ct);
            if (collection == null)
            {
                return FileOperationResult.Fail($"Content collection '{collectionName}' not found");
            }

            var directory = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
            var fileName = Path.GetFileName(filePath);

            await collection.SaveFileAsync(directory, fileName, content);
            return FileOperationResult.Ok();
        }
        catch (Exception ex)
        {
            return FileOperationResult.Fail($"Error saving file '{filePath}' to collection '{collectionName}': {ex.Message}");
        }
    }

    public async Task<FileOperationResult> DeleteFileAsync(
        string collectionName,
        string filePath,
        CancellationToken ct = default)
    {
        try
        {
            var collection = await contentService.GetCollectionAsync(collectionName, ct);
            if (collection == null)
            {
                return FileOperationResult.Fail($"Content collection '{collectionName}' not found");
            }

            await collection.DeleteFileAsync(filePath);
            return FileOperationResult.Ok();
        }
        catch (Exception ex)
        {
            return FileOperationResult.Fail($"Error deleting file '{filePath}' from collection '{collectionName}': {ex.Message}");
        }
    }
}
