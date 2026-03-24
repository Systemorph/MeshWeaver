using System.ComponentModel;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Agent plugin for uploading text content (SVG, markdown, JSON, CSS, etc.)
/// to a node's content collection. Routes through the node hub which has
/// the IFileContentProvider registered.
/// </summary>
public class ContentCollectionPlugin(IMessageHub hub, IAgentChat _) : IAgentPlugin
{
    public string Name => "ContentCollection";

    public IEnumerable<AITool> CreateTools()
    {
        return [AIFunctionFactory.Create(UploadContent)];
    }

    [Description("Uploads text content (SVG, markdown, JSON, CSS, etc.) to a node's content collection. Use for storing diagrams, images (SVG), stylesheets, or any text-based files alongside a node.")]
    public async Task<string> UploadContent(
        [Description("Path to the node that owns the collection (e.g., @PartnerRe/AiConsulting)")] string nodePath,
        [Description("File name/path within the collection (e.g., 'diagram.svg', 'images/architecture.svg')")] string filePath,
        [Description("The text content to upload (SVG markup, markdown, JSON, etc.)")] string content,
        [Description("Collection name (default: 'content')")] string collectionName = "content",
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = MeshOperations.ResolvePath(nodePath);

        try
        {
            // Route to the node's hub where IFileContentProvider is registered
            var address = new Address(resolvedPath);
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

            // Use RegisterCallback pattern to avoid blocking
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var delivery = hub.Post(
                new SaveContentRequest
                {
                    CollectionName = collectionName,
                    FilePath = filePath,
                    TextContent = content
                },
                o => o.WithTarget(address))!;

            var response = await hub.RegisterCallback(delivery,
                (d, _) => Task.FromResult(d), cts.Token);
            var result = ((IMessageDelivery<SaveContentResponse>)response).Message;

            return result.Success
                ? $"Uploaded `{filePath}` to @{resolvedPath}/{collectionName}:{filePath}"
                : $"Error: {result.Error}";
        }
        catch (Exception ex)
        {
            return $"Error uploading to {resolvedPath}: {ex.Message}";
        }
    }
}

/// <summary>
/// Request to save text content to a node's content collection.
/// Handled by the node hub which has IFileContentProvider registered.
/// </summary>
public record SaveContentRequest : IRequest<SaveContentResponse>
{
    public required string CollectionName { get; init; }
    public required string FilePath { get; init; }
    public required string TextContent { get; init; }
}

public record SaveContentResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }
}
