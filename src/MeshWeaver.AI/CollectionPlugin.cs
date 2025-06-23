using System.ComponentModel;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI;

/// <summary>
/// Generalized plugin for reading and writing files to configured collections
/// </summary>
public class CollectionPlugin(IMessageHub hub)
{
    private readonly IContentService contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

    [KernelFunction]
    [Description("Gets the content of a file from a specified collection.")]
    public async Task<string> GetFile(
        [Description("The name of the collection to read from")] string collectionName,
        [Description("The path to the file within the collection")] string filePath)
    {
        try
        {
            var collection = contentService.GetCollection(collectionName);
            if (collection == null)
                return $"Collection '{collectionName}' not found.";

            await using var stream = await collection.GetContentAsync(filePath);
            if (stream == null)
                return $"File '{filePath}' not found in collection '{collectionName}'.";

            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();

            return content;
        }
        catch (FileNotFoundException)
        {
            return $"File '{filePath}' not found in collection '{collectionName}'.";
        }
        catch (Exception ex)
        {
            return $"Error reading file '{filePath}' from collection '{collectionName}': {ex.Message}";
        }
    }
    [KernelFunction]
    [Description("Saves content as a file to a specified collection.")]
    public async Task<string> SaveFile(
        [Description("The name of the collection to save to")] string collectionName,
        [Description("The path where the file should be saved within the collection")] string filePath,
        [Description("The content to save to the file")] string content)
    {
        try
        {
            var collection = contentService.GetCollection(collectionName);
            if (collection == null)
                return $"Collection '{collectionName}' not found.";            // Ensure directory structure exists if the collection has a base path
            EnsureDirectoryExists(collection, filePath);

            // Extract directory and filename components
            var directoryPath = Path.GetDirectoryName(filePath) ?? "";
            var fileName = Path.GetFileName(filePath);

            await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            await collection.SaveFileAsync(directoryPath, fileName, stream);

            return $"File '{filePath}' successfully saved to collection '{collectionName}'. Full path: {directoryPath}/{fileName}";
        }
        catch (Exception ex)
        {
            return $"Error saving file '{filePath}' to collection '{collectionName}': {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Lists all files in a specified collection.")]
    public async Task<string> ListFiles(
        [Description("The name of the collection to list files from")] string collectionName,
        [Description("The path for which to load files.")] string path = "/")
    {
        try
        {
            var collection = contentService.GetCollection(collectionName);
            if (collection == null)
                return $"Collection '{collectionName}' not found.";

            var files = await collection.GetFilesAsync(path);
            var fileList = files.Select(f => new { f.Name, f.Path }).ToList();

            if (!fileList.Any())
                return $"No files found in collection '{collectionName}'.";

            return string.Join("\n", fileList.Select(f => $"- {f.Name} ({f.Path})"));
        }
        catch (Exception ex)
        {
            return $"Error listing files in collection '{collectionName}': {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Checks if a specific file exists in a collection.")]
    public async Task<string> FileExists(
        [Description("The name of the collection to check")] string collectionName,
        [Description("The path to the file within the collection")] string filePath)
    {
        try
        {
            var collection = contentService.GetCollection(collectionName);
            if (collection == null)
                return $"Collection '{collectionName}' not found.";

            await using var stream = await collection.GetContentAsync(filePath);
            if (stream == null)
                return $"File '{filePath}' does not exist in collection '{collectionName}'.";

            return $"File '{filePath}' exists in collection '{collectionName}'.";
        }
        catch (FileNotFoundException)
        {
            return $"File '{filePath}' does not exist in collection '{collectionName}'.";
        }
        catch (Exception ex)
        {
            return $"Error checking file '{filePath}' in collection '{collectionName}': {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Generates a unique filename with timestamp for saving temporary files.")]
    public string GenerateUniqueFileName(
        [Description("The base name for the file (without extension)")] string baseName,
        [Description("The file extension (e.g., 'json', 'txt')")] string extension)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        return $"{baseName}_{timestamp}.{extension.TrimStart('.')}";
    }

    /// <summary>
    /// Ensures that the directory structure exists for the given file path within the collection.
    /// </summary>
    /// <param name="collection">The collection to check</param>
    /// <param name="filePath">The file path that may contain directories</param>
    private void EnsureDirectoryExists(object collection, string filePath)
    {
        try
        {
            // Normalize path separators and get the directory path from the file path
            var normalizedPath = filePath.Replace('/', Path.DirectorySeparatorChar);
            var directoryPath = Path.GetDirectoryName(normalizedPath);

            if (string.IsNullOrEmpty(directoryPath) || directoryPath == "." || directoryPath == Path.DirectorySeparatorChar.ToString())
            {
                // No directory structure needed, file is in root
                return;
            }

            // Try to get the collection's base path using reflection if available
            var collectionType = collection.GetType();
            var basePathProperty = collectionType.GetProperty("BasePath") ??
                                 collectionType.GetProperty("Path") ??
                                 collectionType.GetProperty("RootPath");

            if (basePathProperty != null)
            {
                var basePath = basePathProperty.GetValue(collection) as string;
                if (!string.IsNullOrEmpty(basePath))
                {
                    var fullDirectoryPath = Path.Combine(basePath, directoryPath);
                    Directory.CreateDirectory(fullDirectoryPath);
                }
            }
        }
        catch (Exception)
        {
            // If we can't create directories through reflection, 
            // let the SaveFileAsync method handle any directory creation or fail gracefully
        }
    }
}
