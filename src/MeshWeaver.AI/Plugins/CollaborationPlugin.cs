using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Agent plugin providing tools for adding comments and suggesting edits on
/// Markdown documents via the collaborative editing infrastructure.
///
/// Every method is await-free — reads are wrapped in <c>Observable.FromAsync</c> on
/// the <see cref="TaskPoolScheduler"/> so blocking enumeration never touches the hub
/// scheduler, and writes go through <c>hub.Post + hub.RegisterCallback</c> with a
/// <see cref="TaskCompletionSource{T}"/> bridging the off-hub callback thread back
/// to the caller. See <c>Doc/Architecture/AsynchronousCalls</c> for the rationale:
/// any <c>await</c> on a hub-backed operation from inside a plugin method will
/// deadlock the hub scheduler under load.
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
    public Task<string> AddComment(
        [Description("Canonical path to the document — NOT the display name. Use @/full/path for absolute or @relative/path relative to the current context. Example: @/PartnerRe/AIConsulting/FinalReport or @FinalReport. If you only know the display name, call Search('name:\"...\"') first and use the path field.")] string documentPath,
        [Description("The exact text passage to comment on — must match document content")] string selectedText,
        [Description("The comment text")] string commentText,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("AddComment on {Path}: text='{SelectedText}', comment='{Comment}'",
            documentPath, selectedText, commentText);

        var resolvedInput = MeshOperations.ResolveContextPath(chat, documentPath);
        var resolvedPath = MeshOperations.ResolvePath(resolvedInput);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Read the document off the hub scheduler, then fan into Post + RegisterCallback.
        // No `await` anywhere — the subscription runs the read on TaskPoolScheduler and
        // the write's callback fires on the response thread. Both resolve the TCS.
        ops.Get(resolvedInput)
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                docJson => AddCommentContinuation(
                    docJson, resolvedPath, documentPath, selectedText, commentText, tcs),
                err => tcs.TrySetResult($"Error reading '{documentPath}': {err.Message}"));

        return tcs.Task;
    }

    private void AddCommentContinuation(
        string docJson,
        string resolvedPath,
        string documentPath,
        string selectedText,
        string commentText,
        TaskCompletionSource<string> tcs)
    {
        if (docJson.StartsWith("Not found") || docJson.StartsWith("Error"))
        {
            tcs.TrySetResult($"Document not found: {documentPath}");
            return;
        }

        var content = ExtractContent(docJson);
        if (string.IsNullOrEmpty(content))
        {
            tcs.TrySetResult($"Could not extract content from {documentPath}");
            return;
        }

        var start = content.IndexOf(selectedText, StringComparison.Ordinal);
        if (start < 0)
        {
            tcs.TrySetResult($"Text '{selectedText}' not found in document {documentPath}");
            return;
        }

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

        PostAndReport<CreateCommentResponse>(
            request,
            new Address(resolvedPath),
            documentPath,
            tcs,
            resp => resp.Success
                ? $"Comment added on \"{selectedText}\" in {documentPath}"
                : $"Error adding comment: {resp.Error ?? "unknown error"}");
    }

    [Description("Suggests a text edit (insertion, replacement, or deletion) on a Markdown document as a tracked change. Other collaborators can accept or reject the suggestion.")]
    public Task<string> SuggestEdit(
        [Description("Canonical path to the document — NOT the display name. Use @/full/path for absolute or @relative/path relative to the current context. Example: @/PartnerRe/AIConsulting/FinalReport or @FinalReport. If you only know the display name, call Search('name:\"...\"') first and use the path field.")] string documentPath,
        [Description("The exact text to replace (empty string for pure insertion at document start)")] string originalText,
        [Description("The replacement text (empty string for deletion)")] string newText,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("SuggestEdit on {Path}: original='{Original}', new='{New}'",
            documentPath, originalText, newText);

        var resolvedInput = MeshOperations.ResolveContextPath(chat, documentPath);
        var resolvedPath = MeshOperations.ResolvePath(resolvedInput);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        ops.Get(resolvedInput)
            .SubscribeOn(TaskPoolScheduler.Default)
            .Subscribe(
                docJson => SuggestEditContinuation(
                    docJson, resolvedPath, documentPath, originalText, newText, tcs),
                err => tcs.TrySetResult($"Error reading '{documentPath}': {err.Message}"));

        return tcs.Task;
    }

    private void SuggestEditContinuation(
        string docJson,
        string resolvedPath,
        string documentPath,
        string originalText,
        string newText,
        TaskCompletionSource<string> tcs)
    {
        if (docJson.StartsWith("Not found") || docJson.StartsWith("Error"))
        {
            tcs.TrySetResult($"Document not found: {documentPath}");
            return;
        }

        var content = ExtractContent(docJson);
        if (string.IsNullOrEmpty(content))
        {
            tcs.TrySetResult($"Could not extract content from {documentPath}");
            return;
        }

        int start;
        if (string.IsNullOrEmpty(originalText))
        {
            start = 0;
        }
        else
        {
            start = content.IndexOf(originalText, StringComparison.Ordinal);
            if (start < 0)
            {
                tcs.TrySetResult($"Text '{originalText}' not found in document {documentPath}");
                return;
            }
        }

        var request = new CreateSuggestedEditRequest
        {
            DocumentId = resolvedPath,
            Position = start,
            DeletedText = string.IsNullOrEmpty(originalText) ? null : originalText,
            InsertedText = string.IsNullOrEmpty(newText) ? null : newText,
            Author = chat.Context?.Path ?? "agent"
        };

        PostAndReport<CreateSuggestedEditResponse>(
            request,
            new Address(resolvedPath),
            documentPath,
            tcs,
            resp =>
            {
                if (!resp.Success)
                    return $"Error suggesting edit: {resp.Error ?? "unknown error"}";
                if (string.IsNullOrEmpty(originalText))
                    return $"Suggested insertion of \"{Truncate(newText)}\" in {documentPath}";
                if (string.IsNullOrEmpty(newText))
                    return $"Suggested deletion of \"{Truncate(originalText)}\" in {documentPath}";
                return $"Suggested replacing \"{Truncate(originalText)}\" with \"{Truncate(newText)}\" in {documentPath}";
            });
    }

    /// <summary>
    /// Posts a request via Post + RegisterCallback and resolves <paramref name="tcs"/>
    /// from the callback. No <c>await</c>: the callback fires on a non-hub thread
    /// when the response arrives. Routing failures surface as a user-actionable
    /// error pointing the agent back at the "use `path`, not `name`" rule.
    /// </summary>
    private void PostAndReport<TResponse>(
        IRequest<TResponse> request,
        Address target,
        string originalInput,
        TaskCompletionSource<string> tcs,
        Func<TResponse, string> formatSuccess)
    {
        var delivery = hub.Post(request, o => o.WithTarget(target))!;
        hub.RegisterCallback(delivery, callback =>
        {
            switch (callback)
            {
                case IMessageDelivery<TResponse> typed:
                    try { tcs.TrySetResult(formatSuccess(typed.Message)); }
                    catch (Exception ex) { tcs.TrySetResult($"Error formatting response: {ex.Message}"); }
                    break;
                case IMessageDelivery<DeliveryFailure> failure:
                    logger.LogWarning(
                        "Delivery to {Target} failed for {RequestType}: {Reason}. Original input: {OriginalInput}",
                        target, request.GetType().Name, failure.Message.Message, originalInput);
                    tcs.TrySetResult(
                        $"Error: {failure.Message.Message ?? "delivery failed"}. " +
                        $"Check that '{originalInput}' resolves to an existing node — pass the MeshNode's " +
                        "`path` property, not its `name`. If you only know the display name, call " +
                        "Search('name:\"...\"') and use the `path` field of the match.");
                    break;
                default:
                    tcs.TrySetResult($"Error: unexpected response {callback.Message?.GetType().Name ?? "null"} for {originalInput}.");
                    break;
            }
            return callback;
        });
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
