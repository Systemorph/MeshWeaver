using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Pages;

/// <summary>
/// Content page that handles content URLs using dynamic path resolution.
/// Uses IMeshCatalog.ResolvePathAsync to resolve address from path, similar to static file endpoint.
/// </summary>
public partial class ContentPage : ComponentBase, IDisposable
{
    /// <summary>
    /// Full path from the URL route (catch-all parameter).
    /// </summary>
    [Parameter] public string? FullPath { get; set; }

    /// <summary>
    /// The resolved collection name.
    /// </summary>
    public string? ResolvedCollection { get; set; }

    /// <summary>
    /// The resolved path within the collection.
    /// </summary>
    public string? ResolvedPath { get; set; }

    /// <summary>
    /// The target address for the content.
    /// </summary>
    public Address? TargetAddress { get; set; }

    public Stream? Content { get; set; }
    public string? ContentType { get; set; }
    public string? ErrorMessage { get; set; }

    // CSV support
    public string[]? CsvHeaders { get; set; }
    public List<string[]>? CsvRows { get; set; }
    public int CsvVisibleRows { get; set; } = 50;

    [Inject] public PortalApplication PortalApplication { get; set; } = null!;
    private IContentService ContentService => PortalApplication.Hub.ServiceProvider.GetRequiredService<IContentService>();

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();

        if (string.IsNullOrEmpty(FullPath))
        {
            ErrorMessage = "Path is required";
            return;
        }

        var pathParts = FullPath.Split('/');
        if (pathParts.Length < 2)
        {
            ErrorMessage = "Invalid path format. Expected: /content/{collection}/{path} or /content/{address}/{collection}/{path}";
            return;
        }

        var firstSegment = pathParts[0];

        // Decode collection name: '~' is used as escape for '/' in collection names
        var decodedFirstSegment = DecodeCollectionName(firstSegment);

        // Check if first segment is a known collection name (global content)
        var knownCollection = ContentService.GetCollectionConfig(decodedFirstSegment);

        if (knownCollection != null)
        {
            // Pattern 1: /content/{collection}/{path} - global content from portal hub
            ResolvedCollection = decodedFirstSegment;
            ResolvedPath = string.Join("/", pathParts.Skip(1));
            TargetAddress = PortalApplication.Hub.Address;
        }
        else
        {
            // Pattern 2: /content/{address}/{collection}/{path} - address-scoped content
            // Use IPathResolver to resolve the address from the path
            var pathResolver = PortalApplication.Hub.ServiceProvider.GetRequiredService<IPathResolver>();
            var resolution = await pathResolver.ResolvePathAsync(FullPath);

            if (resolution == null)
            {
                ErrorMessage = $"No matching address found for path '{FullPath}'";
                return;
            }

            // Parse remainder: first segment is collection (with ~ encoding), rest is file path
            if (string.IsNullOrEmpty(resolution.Remainder))
            {
                ErrorMessage = "Collection and file path are required";
                return;
            }

            var remainderParts = resolution.Remainder.Split('/');
            if (remainderParts.Length < 1)
            {
                ErrorMessage = "Invalid path format. Expected: /content/{address}/{collection}/{path}";
                return;
            }

            // Decode collection name: '~' is used as escape for '/' (e.g., "Submissions@Microsoft~2026")
            ResolvedCollection = DecodeCollectionName(remainderParts[0]);
            ResolvedPath = remainderParts.Length > 1 ? string.Join("/", remainderParts.Skip(1)) : null;
            TargetAddress = (Address)resolution.Prefix;
        }

        // Add configuration for address-scoped content collections
        if (TargetAddress != null && !string.IsNullOrEmpty(ResolvedCollection))
        {
            ContentService.AddConfiguration(new ContentCollectionConfig
            {
                Name = ResolvedCollection,
                SourceType = HubStreamProviderFactory.SourceType,
                Address = TargetAddress
            });
        }

        var collection = await ContentService.GetCollectionAsync(ResolvedCollection!);
        if (collection is null)
        {
            ErrorMessage = $"Collection '{ResolvedCollection}' not found at address '{TargetAddress}'";
            return;
        }

        if (string.IsNullOrEmpty(ResolvedPath))
            return;

        ContentType = collection.GetContentType(ResolvedPath!);
        // Document types with converter support are rendered as markdown
        if (IsConvertibleDocument(ResolvedPath!))
        {
            ContentType = "text/markdown";
        }
        else if (ContentType != "text/markdown")
        {
            Content = await collection.GetContentAsync(ResolvedPath!);
        }

        if (ContentType == "text/csv" && Content != null)
        {
            ParseCsv(Content);
        }
    }

    public byte[] ReadStream(Stream stream)
    {
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    public string ReadStreamAsString(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        Content?.Dispose();
    }

    private void ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);

        if (lines.Count == 0)
            return;

        CsvHeaders = ParseCsvLine(lines[0]);
        CsvRows = lines.Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(ParseCsvLine)
            .ToList();
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var inQuotes = false;
        var field = new System.Text.StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields.ToArray();
    }

    private void LoadMoreCsvRows()
    {
        CsvVisibleRows += 50;
    }

    /// <summary>
    /// Checks if a file path points to a document that can be converted to markdown for preview.
    /// </summary>
    private static bool IsConvertibleDocument(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".docx";
    }

    /// <summary>
    /// Decodes a collection name from a URL by replacing '~' back to '/'.
    /// Collection names with slashes (e.g., "Submissions@Microsoft/2026") are encoded
    /// with '~' in URLs to avoid path parsing issues.
    /// </summary>
    private static string DecodeCollectionName(string encodedName) => encodedName.Replace("~", "/");
}
