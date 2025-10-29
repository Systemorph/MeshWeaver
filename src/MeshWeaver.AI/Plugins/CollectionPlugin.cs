using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using UglyToad.PdfPig;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Generalized plugin for reading and writing files to configured collections
/// </summary>
public class CollectionPlugin(IMessageHub hub)
{
    private readonly IContentService contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

    [KernelFunction]
    [Description("Gets the content of a file from a specified collection.")]
    public async Task<string> GetFile(
        [Description("The name of the collection to read from. If null, uses the default collection.")] string collectionName,
        [Description("The path to the file within the collection")] string filePath,
        [Description("Optional: number of rows to read. If null, reads entire file. For Excel files, reads first N rows from each worksheet.")] int? numberOfRows = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var collection = await contentService.GetCollectionAsync(collectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{collectionName}' not found.";

            await using var stream = await collection.GetContentAsync(filePath, cancellationToken);
            if (stream == null)
                return $"File '{filePath}' not found in collection '{collectionName}'.";

            // Check file type and read accordingly
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            if (extension == ".xlsx" || extension == ".xls")
            {
                return await ReadExcelFileAsync(stream, filePath, numberOfRows);
            }
            else if (extension == ".docx")
            {
                return await ReadWordFileAsync(stream, filePath, numberOfRows);
            }
            else if (extension == ".pdf")
            {
                return await ReadPdfFileAsync(stream, filePath, numberOfRows);
            }

            // For other files, read as text
            using var reader = new StreamReader(stream);
            if (numberOfRows.HasValue)
            {
                var sb = new StringBuilder();
                var linesRead = 0;
                while (!reader.EndOfStream && linesRead < numberOfRows.Value)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    sb.AppendLine(line);
                    linesRead++;
                }
                return sb.ToString();
            }
            else
            {
                var content = await reader.ReadToEndAsync(cancellationToken);
                return content;
            }
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

    private async Task<string> ReadExcelFileAsync(Stream stream, string filePath, int? numberOfRows)
    {
        try
        {
            using var wb = new XLWorkbook(stream);
            var sb = new StringBuilder();

            foreach (var ws in wb.Worksheets)
            {
                var used = ws.RangeUsed();
                sb.AppendLine($"## Sheet: {ws.Name}");
                sb.AppendLine();
                if (used is null)
                {
                    sb.AppendLine("(No data)");
                    sb.AppendLine();
                    continue;
                }

                var firstRow = used.FirstRow().RowNumber();
                var lastRow = numberOfRows.HasValue
                    ? Math.Min(used.FirstRow().RowNumber() + numberOfRows.Value - 1, used.LastRow().RowNumber())
                    : used.LastRow().RowNumber();
                var firstCol = 1;
                var lastCol = used.LastColumn().ColumnNumber();

                // Build markdown table with column letters as headers
                var columnHeaders = new List<string> { "Row" };
                for (var c = firstCol; c <= lastCol; c++)
                {
                    // Convert column number to Excel letter (1=A, 2=B, ..., 27=AA, etc.)
                    columnHeaders.Add(GetExcelColumnLetter(c));
                }

                // Header row
                sb.AppendLine("| " + string.Join(" | ", columnHeaders) + " |");
                // Separator row
                sb.AppendLine("|" + string.Join("", columnHeaders.Select(_ => "---:|")));

                // Data rows
                for (var r = firstRow; r <= lastRow; r++)
                {
                    var rowVals = new List<string> { r.ToString() };
                    for (var c = firstCol; c <= lastCol; c++)
                    {
                        var cell = ws.Cell(r, c);
                        var raw = cell.GetValue<string>();
                        var val = raw?.Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|").Trim();
                        // Empty cells show as empty in table
                        rowVals.Add(string.IsNullOrEmpty(val) ? "" : val);
                    }

                    sb.AppendLine("| " + string.Join(" | ", rowVals) + " |");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading Excel file '{filePath}': {ex.Message}";
        }
    }

    private static string GetExcelColumnLetter(int columnNumber)
    {
        var columnLetter = "";
        while (columnNumber > 0)
        {
            var modulo = (columnNumber - 1) % 26;
            columnLetter = Convert.ToChar('A' + modulo) + columnLetter;
            columnNumber = (columnNumber - 1) / 26;
        }
        return columnLetter;
    }

    private async Task<string> ReadWordFileAsync(Stream stream, string filePath, int? numberOfRows)
    {
        try
        {
            using var wordDoc = WordprocessingDocument.Open(stream, false);
            var body = wordDoc.MainDocumentPart?.Document?.Body;

            if (body == null)
                return $"Word document '{filePath}' has no readable content.";

            var sb = new StringBuilder();
            sb.AppendLine($"# Document: {Path.GetFileName(filePath)}");
            sb.AppendLine();

            var paragraphs = body.Elements<Paragraph>().ToList();
            var paragraphsToRead = numberOfRows.HasValue
                ? paragraphs.Take(numberOfRows.Value).ToList()
                : paragraphs;

            foreach (var paragraph in paragraphsToRead)
            {
                var text = paragraph.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                    sb.AppendLine();
                }
            }

            // Also handle tables
            var tables = body.Elements<Table>().ToList();
            foreach (var table in tables)
            {
                sb.AppendLine("## Table");
                sb.AppendLine();

                var rows = table.Elements<TableRow>().ToList();
                var rowsToRead = numberOfRows.HasValue
                    ? rows.Take(numberOfRows.Value).ToList()
                    : rows;

                foreach (var row in rowsToRead)
                {
                    var cells = row.Elements<TableCell>().ToList();
                    var cellTexts = cells.Select(c => c.InnerText.Replace('|', '\\').Trim()).ToList();
                    sb.AppendLine("| " + string.Join(" | ", cellTexts) + " |");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading Word document '{filePath}': {ex.Message}";
        }
    }

    private async Task<string> ReadPdfFileAsync(Stream stream, string filePath, int? numberOfRows)
    {
        try
        {
            using var pdfDocument = PdfDocument.Open(stream);
            var sb = new StringBuilder();
            sb.AppendLine($"# PDF Document: {Path.GetFileName(filePath)}");
            sb.AppendLine($"Total pages: {pdfDocument.NumberOfPages}");
            sb.AppendLine();

            var pagesToRead = numberOfRows.HasValue
                ? Math.Min(numberOfRows.Value, pdfDocument.NumberOfPages)
                : pdfDocument.NumberOfPages;

            for (int pageNum = 1; pageNum <= pagesToRead; pageNum++)
            {
                var page = pdfDocument.GetPage(pageNum);
                sb.AppendLine($"## Page {pageNum}");
                sb.AppendLine();

                var text = page.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                }
                else
                {
                    sb.AppendLine("(No text content)");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading PDF document '{filePath}': {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Saves content as a file to a specified collection.")]
    public async Task<string> SaveFile(
        [Description("The name of the collection to save to")] string collectionName,
        [Description("The path where the file should be saved within the collection")] string filePath,
        [Description("The content to save to the file")] string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var collection = await contentService.GetCollectionAsync(collectionName, cancellationToken);
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
        [Description("The path for which to load files.")] string path = "/",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var collection = await contentService.GetCollectionAsync(collectionName, cancellationToken);
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
        [Description("The path to the file within the collection")] string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var collection = await contentService.GetCollectionAsync(collectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{collectionName}' not found.";

            await using var stream = await collection.GetContentAsync(filePath, cancellationToken);
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

    [KernelFunction]
    [Description("Imports data from a file in a collection to a specified address.")]
    public async Task<string> Import(
        [Description("The path to the file to import")] string path,
        [Description("The name of the collection containing the file (optional if default collection is configured)")] string? collection = null,
        [Description("The target address for the import (optional if default address is configured), can be a string like 'AddressType/id' or an Address object")] object? address = null,
        [Description("The import format to use (optional, defaults to 'Default')")] string? format = null,
        [Description("Optional import configuration as JSON string. When provided, this will be used instead of the format parameter.")] string? configuration = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(collection))
                return "Collection name is required.";

            if (address == null)
                return "Target address is required.";

            // Parse the address - handle both string and Address types
            Address targetAddress;
            if (address is string addressString)
            {
                targetAddress = hub.GetAddress(addressString);
            }
            else if (address is Address addr)
            {
                targetAddress = addr;
            }
            else
            {
                return $"Invalid address type: {address.GetType().Name}. Expected string or Address.";
            }

            // Build ImportRequest JSON structure
            var importRequestJson = new JsonObject
            {
                ["$type"] = "MeshWeaver.Import.ImportRequest",
                ["source"] = new JsonObject
                {
                    ["$type"] = "MeshWeaver.Import.CollectionSource",
                    ["collection"] = collection,
                    ["path"] = path
                },
                ["format"] = format ?? "Default"
            };

            // Add configuration if provided
            if (!string.IsNullOrWhiteSpace(configuration))
            {
                var configNode = JsonNode.Parse(configuration);
                if (configNode != null)
                {
                    importRequestJson["configuration"] = configNode;
                }
            }

            // Serialize and deserialize through hub's serializer to get proper type
            var jsonString = importRequestJson.ToJsonString();
            var importRequestObj = JsonSerializer.Deserialize<object>(jsonString, hub.JsonSerializerOptions)!;

            // Post the request to the hub
            var responseMessage = await hub.AwaitResponse(
                importRequestObj,
                o => o.WithTarget(targetAddress),
                cancellationToken
            );

            // Serialize the response back to JSON for processing
            var responseJson = JsonSerializer.Serialize(responseMessage, hub.JsonSerializerOptions);
            var responseObj = JsonNode.Parse(responseJson)!;

            var log = responseObj["log"] as JsonObject;
            var status = log?["status"]?.ToString() ?? "Unknown";
            var messages = log?["messages"] as JsonArray ?? new JsonArray();

            var result = $"Import {status.ToLower()}.\n";
            if (messages.Count > 0)
            {
                result += "Log messages:\n";
                foreach (var msg in messages)
                {
                    if (msg is JsonObject msgObj)
                    {
                        var level = msgObj["logLevel"]?.ToString() ?? "Info";
                        var message = msgObj["message"]?.ToString() ?? "";
                        result += $"  [{level}] {message}\n";
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return $"Error importing file '{path}' from collection '{collection}' to address '{address}': {ex.Message}";
        }
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
