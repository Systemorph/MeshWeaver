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

public class ContentCollectionPlugin(IMessageHub hub, IAgentChat chat) : IAgentPlugin
{
    public string Name => "ContentCollection";

    public IEnumerable<AITool> CreateTools()
    {
        return [AIFunctionFactory.Create(UploadContent)];
    }

    [Description("Uploads text content (SVG, markdown, JSON, CSS, etc.) to a node's content collection. Use for storing diagrams, images (SVG), stylesheets, or any text-based files alongside a node.")]
    public Task<string> UploadContent(
        [Description("Canonical path to the node that owns the collection — use the MeshNode's `path` property, NOT its `name`. Use @/full/path for absolute or @relative/path relative to the current context. Example: @/PartnerRe/AIConsulting or @FinalReport. If you only know the display name, call Search('name:\"...\"') first and use the path field of the match.")] string nodePath,
        [Description("File name/path within the collection (e.g., 'diagram.svg', 'images/architecture.svg')")] string filePath,
        [Description("The text content to upload (SVG markup, markdown, JSON, etc.)")] string content,
        [Description("Collection name (default: 'content')")] string collectionName = "content",
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = MeshOperations.ResolvePath(MeshOperations.ResolveContextPath(chat, nodePath));
        var address = new Address(resolvedPath);

        // Post + RegisterCallback + TCS — never `await hub.RegisterCallback(..., ct)` from
        // a plugin method: that blocks the hub scheduler. The callback fires on a non-hub
        // thread when the response arrives and resolves the TCS to unblock the caller.
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = hub.Post(
            new SaveContentRequest
            {
                CollectionName = collectionName,
                FilePath = filePath,
                TextContent = content
            },
            o => o.WithTarget(address))!;

        hub.RegisterCallback(delivery, callback =>
        {
            switch (callback)
            {
                case IMessageDelivery<SaveContentResponse> typed:
                    tcs.TrySetResult(typed.Message.Success
                        ? $"Uploaded `{filePath}` to @{resolvedPath}/{collectionName}/{filePath}"
                        : $"Error: {typed.Message.Error}");
                    break;
                case IMessageDelivery<DeliveryFailure> failure:
                    tcs.TrySetResult(
                        $"Error uploading to {resolvedPath}: {failure.Message.Message ?? "delivery failed"}. " +
                        $"Check that '{nodePath}' resolves to an existing node — pass the MeshNode's " +
                        "`path` property, not its `name`.");
                    break;
                default:
                    tcs.TrySetResult($"Error: unexpected response {callback.Message?.GetType().Name ?? "null"} uploading to {resolvedPath}.");
                    break;
            }
            return callback;
        });

        return tcs.Task;
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
