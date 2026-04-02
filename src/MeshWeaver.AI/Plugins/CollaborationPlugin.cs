using System.ComponentModel;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Agent plugin providing tools for adding comments and suggesting edits
/// on Markdown documents via the collaborative editing infrastructure.
/// </summary>
public class CollaborationPlugin(IMessageHub hub, IAgentChat chat) : IAgentPlugin
{
    private readonly MeshOperations ops = new(hub);
    private readonly ILogger logger = hub.ServiceProvider.GetService(typeof(ILogger<CollaborationPlugin>)) as ILogger
        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <inheritdoc />
    public string Name => "Collaboration";

    /// <inheritdoc />
    public IEnumerable<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(AddComment),
            AIFunctionFactory.Create(SuggestEdit),
        ];
    }

    [Description("Adds a comment to a text passage in a Markdown document. The comment is anchored to the selected text and visible to all collaborators.")]
    public async Task<string> AddComment(
        [Description("Path to the document (e.g., @org/MyDoc)")] string documentPath,
        [Description("The exact text passage to comment on — must match document content")] string selectedText,
        [Description("The comment text")] string commentText,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("AddComment on {Path}: text='{SelectedText}', comment='{Comment}'",
            documentPath, selectedText, commentText);

        var resolvedPath = MeshOperations.ResolvePath(documentPath);

        // Get document content to find the text position
        var docJson = await ops.Get(documentPath);
        if (docJson.StartsWith("Not found") || docJson.StartsWith("Error"))
            return $"Document not found: {documentPath}";

        // Extract text content for position search
        var content = ExtractContent(docJson);
        if (string.IsNullOrEmpty(content))
            return $"Could not extract content from {documentPath}";

        var start = content.IndexOf(selectedText, StringComparison.Ordinal);
        if (start < 0)
            return $"Text '{selectedText}' not found in document {documentPath}";

        var end = start + selectedText.Length;

        // Use fragment-based matching: first/last few words for fuzzy position finding
        var words = selectedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var startFragment = string.Join(" ", words.Take(Math.Min(5, words.Length)));
        var endFragment = string.Join(" ", words.Skip(Math.Max(0, words.Length - 5)));

        var request = new CreateCommentRequest
        {
            DocumentId = resolvedPath,
            SelectedText = selectedText,
            StartFragment = startFragment,
            EndFragment = endFragment,
            CommentText = commentText,
            Author = chat.Context?.Path ?? "agent"
        };

        hub.Post(request, o => o.WithTarget(new Address(resolvedPath)));
        return $"Comment added on \"{selectedText}\" in {documentPath}";
    }

    [Description("Suggests a text edit (insertion, replacement, or deletion) on a Markdown document as a tracked change. Other collaborators can accept or reject the suggestion.")]
    public async Task<string> SuggestEdit(
        [Description("Path to the document (e.g., @org/MyDoc)")] string documentPath,
        [Description("The exact text to replace (empty string for pure insertion at document start)")] string originalText,
        [Description("The replacement text (empty string for deletion)")] string newText,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("SuggestEdit on {Path}: original='{Original}', new='{New}'",
            documentPath, originalText, newText);

        var resolvedPath = MeshOperations.ResolvePath(documentPath);

        // Get document content
        var docJson = await ops.Get(documentPath);
        if (docJson.StartsWith("Not found") || docJson.StartsWith("Error"))
            return $"Document not found: {documentPath}";

        var content = ExtractContent(docJson);
        if (string.IsNullOrEmpty(content))
            return $"Could not extract content from {documentPath}";

        int start;
        int end;
        if (string.IsNullOrEmpty(originalText))
        {
            // Pure insertion at start of document
            start = 0;
            end = 0;
        }
        else
        {
            start = content.IndexOf(originalText, StringComparison.Ordinal);
            if (start < 0)
                return $"Text '{originalText}' not found in document {documentPath}";
            end = start + originalText.Length;
        }

        var request = new CreateSuggestedEditRequest
        {
            DocumentId = resolvedPath,
            Position = start,
            DeletedText = string.IsNullOrEmpty(originalText) ? null : originalText,
            InsertedText = string.IsNullOrEmpty(newText) ? null : newText,
            Author = chat.Context?.Path ?? "agent"
        };

        hub.Post(request, o => o.WithTarget(new Address(resolvedPath)));

        if (string.IsNullOrEmpty(originalText))
            return $"Suggested insertion of \"{Truncate(newText)}\" in {documentPath}";
        if (string.IsNullOrEmpty(newText))
            return $"Suggested deletion of \"{Truncate(originalText)}\" in {documentPath}";
        return $"Suggested replacing \"{Truncate(originalText)}\" with \"{Truncate(newText)}\" in {documentPath}";
    }

    private static string? ExtractContent(string rawJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;
            if (root.TryGetProperty("content", out var contentProp))
            {
                if (contentProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    return contentProp.GetString();
                if (contentProp.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    contentProp.TryGetProperty("content", out var inner) &&
                    inner.ValueKind == System.Text.Json.JsonValueKind.String)
                    return inner.GetString();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string value, int maxLength = 40)
        => value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}
