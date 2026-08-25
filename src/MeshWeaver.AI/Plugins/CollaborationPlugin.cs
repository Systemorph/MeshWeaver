using MeshWeaver.Mesh.Operations;
using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Agent plugin providing tools for adding comments and suggesting edits on
/// Markdown documents via the collaborative editing infrastructure.
///
/// Every method is await-free — reads are moved onto the <see cref="TaskPoolScheduler"/>
/// with <c>.SubscribeOn(TaskPoolScheduler.Default)</c> (NEVER
/// <c>Observable.FromAsync</c>, which is forbidden outside <c>IoPool</c>) so blocking
/// enumeration never touches the hub scheduler, and writes go through
/// <c>hub.Observe(...)</c>, with <see cref="ToolTask.Bridge{T}"/> bridging the off-hub
/// callback thread back to the caller — every terminal settles (value, error, EMPTY
/// completion) and the round's cancellation token is observed (#1956).
/// See <c>Doc/Architecture/AsynchronousCalls</c> for the rationale:
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

    /// <summary>
    /// Agent tool: anchors a comment to a passage of a Markdown document via the collaborative
    /// editing infrastructure. Reads the document off the hub scheduler, locates
    /// <paramref name="selectedText"/>, and posts a comment-create request to the document's node hub.
    /// </summary>
    /// <param name="documentPath">Canonical mesh path of the document (the node's <c>path</c>, not its display name).</param>
    /// <param name="selectedText">The exact passage to anchor the comment to; must match document content.</param>
    /// <param name="commentText">The comment body.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A status message describing success, or the reason the comment could not be added.</returns>
    [Description("Adds a comment to a text passage in a Markdown document. The comment is anchored to the selected text and visible to all collaborators.")]
    public Task<string> AddComment(
        [Description("Canonical path to the document — NOT the display name. Use @/full/path for absolute or @relative/path relative to the current context. Example: @/Acme/AIConsulting/FinalReport or @FinalReport. If you only know the display name, call Search('name:\"...\"') first and use the path field.")] string documentPath,
        [Description("The exact text passage to comment on — must match document content")] string selectedText,
        [Description("The comment text")] string commentText,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("AddComment on {Path}: text='{SelectedText}', comment='{Comment}'",
            documentPath, selectedText, commentText);

        var resolvedInput = AgentChatPaths.ResolveContextPath(chat, documentPath);
        var resolvedPath = MeshOperations.ResolvePath(resolvedInput);

        // Read the document off the hub scheduler, then fan into hub.Observe(...).
        // No `await` anywhere — the subscription runs the read on TaskPoolScheduler and
        // the write's response arrives on the observe subscription.
        //
        // 🚨 Every terminal settles and the round's token is observed (#1956): before, a bare
        // TaskCompletionSource with a 2-arg Subscribe left the task pending forever when the read
        // completed WITHOUT emitting, and `cancellationToken` appeared only in the doc comment — so
        // a parked AddComment held its Ai-pool gate permit through IoPool.Drain and Stop no-opped.
        return ToolTask.Bridge(
            ops.Get(resolvedInput)
                .SubscribeOn(TaskPoolScheduler.Default)
                .Take(1)
                .SelectMany(docJson => AddCommentContinuation(
                    docJson, resolvedPath, documentPath, selectedText, commentText)),
            cancellationToken,
            answer => answer,
            err => $"Error reading '{documentPath}': {err.Message}",
            () => $"Document not found: {documentPath}");
    }

    /// <summary>
    /// The continuation, as an observable of the tool's ANSWER. It formats its own failures rather
    /// than erroring, so the bridge's error arm keeps naming the read that actually failed.
    /// </summary>
    private IObservable<string> AddCommentContinuation(
        string docJson,
        string resolvedPath,
        string documentPath,
        string selectedText,
        string commentText)
    {
        if (docJson.StartsWith("Not found") || docJson.StartsWith("Error"))
            return Observable.Return($"Document not found: {documentPath}");

        var content = ExtractContent(docJson);
        if (string.IsNullOrEmpty(content))
            return Observable.Return($"Could not extract content from {documentPath}");

        var start = content.IndexOf(selectedText, StringComparison.Ordinal);
        if (start < 0)
            return Observable.Return($"Text '{selectedText}' not found in document {documentPath}");

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

        return PostAndReport<CreateCommentResponse>(
            request,
            new Address(resolvedPath),
            documentPath,
            resp => resp.Success
                ? $"Comment added on \"{selectedText}\" in {documentPath}"
                : $"Error adding comment: {resp.Error ?? "unknown error"}");
    }

    /// <summary>
    /// Agent tool: applies a text edit (insertion, replacement, or deletion) to a Markdown document
    /// as a normal versioned write. An empty <paramref name="originalText"/> means insertion at
    /// document start; an empty <paramref name="newText"/> means deletion.
    /// <para>
    /// 🚨 The edit is NOT parked in a <c>_Tracking</c> satellite any more — it lands in the document
    /// and therefore in the version history, which records who changed what and when. Reviewers see
    /// it as a tracked change (projected from that history — see <c>ChangeProjection</c>) and revert
    /// it with one click; the revert is itself a versioned write. One source of truth, no anchors to
    /// go stale.
    /// </para>
    /// <para>
    /// The splice happens INSIDE the document's update lambda, against the LIVE node the owning hub
    /// hands us — a read-then-write would lose any edit that landed in between.
    /// </para>
    /// </summary>
    /// <param name="documentPath">Canonical mesh path of the document (the node's <c>path</c>, not its display name).</param>
    /// <param name="originalText">The exact text to replace; empty string for a pure insertion at document start.</param>
    /// <param name="newText">The replacement text; empty string for a deletion.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A status message describing the edit, or the reason it could not be made.</returns>
    [Description("Edits a Markdown document (insertion, replacement, or deletion). The edit is applied and recorded in the document's version history, where collaborators review it as a tracked change and can revert it.")]
    public Task<string> SuggestEdit(
        [Description("Canonical path to the document — NOT the display name. Use @/full/path for absolute or @relative/path relative to the current context. Example: @/Acme/AIConsulting/FinalReport or @FinalReport. If you only know the display name, call Search('name:\"...\"') first and use the path field.")] string documentPath,
        [Description("The exact text to replace (empty string for pure insertion at document start)")] string originalText,
        [Description("The replacement text (empty string for deletion)")] string newText,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("SuggestEdit on {Path}: original='{Original}', new='{New}'",
            documentPath, originalText, newText);

        var resolvedInput = AgentChatPaths.ResolveContextPath(chat, documentPath);
        var resolvedPath = MeshOperations.ResolvePath(resolvedInput);

        // Probe existence first — the single most common agent mistake is passing a display name
        // instead of a path, and "Document not found" is far more actionable than whatever the write
        // path reports for an address that routes nowhere. Same three-terminal bridge as
        // AddComment: value, error, and the EMPTY completion that used to park the round (#1956).
        return ToolTask.Bridge(
            ops.Get(resolvedInput)
                .SubscribeOn(TaskPoolScheduler.Default)
                .Take(1)
                .SelectMany(docJson => SuggestEditContinuation(
                    docJson, resolvedPath, documentPath, originalText, newText)),
            cancellationToken,
            answer => answer,
            err => $"Error reading '{documentPath}': {err.Message}",
            () => $"Document not found: {documentPath}");
    }

    /// <summary>
    /// The continuation, as an observable of the tool's ANSWER — see
    /// <see cref="AddCommentContinuation"/> for why it formats its own failures.
    /// </summary>
    private IObservable<string> SuggestEditContinuation(
        string docJson,
        string resolvedPath,
        string documentPath,
        string originalText,
        string newText)
    {
        if (docJson.StartsWith("Not found") || docJson.StartsWith("Error"))
            return Observable.Return($"Document not found: {documentPath}");

        var options = hub.JsonSerializerOptions;

        // The splice runs INSIDE the update lambda, against the LIVE node the owning hub hands us —
        // the probe above only established existence; splicing the text it returned would lose any
        // edit that landed in between. DefaultIfEmpty guarantees the tool always answers: an update
        // that completes without emitting must not leave the agent's task pending forever.
        return hub.GetMeshNodeStream(resolvedPath)
            .Update(live => MarkdownOverviewLayoutArea.WithMarkdownContent(
                live, Splice(MarkdownOverviewLayoutArea.GetMarkdownContent(live), originalText, newText), options))
            .Take(1)
            .Select(written => written is null
                ? $"The edit produced no change in {documentPath}."
                : Describe(documentPath, originalText, newText))
            .Catch((Exception err) =>
            {
                logger.LogWarning(err, "SuggestEdit failed for {Path}", resolvedPath);
                return Observable.Return($"Error editing '{documentPath}': {err.Message}");
            })
            .DefaultIfEmpty($"The edit produced no change in {documentPath}.");
    }

    /// <summary>
    /// The pure edit transition: replaces the single occurrence of <paramref name="originalText"/>
    /// with <paramref name="newText"/> (empty original ⇒ prepend). Delegates to the ONE anchored
    /// replace (<see cref="MeshOperations.AnchoredReplace"/>, #1716) with <c>replaceAll:false</c>,
    /// so it keeps its single-replace semantics but gains the uniqueness check: it throws when the
    /// text is no longer there (silently splicing at offset 0 would corrupt the document) AND when
    /// the anchor is ambiguous (splicing an arbitrary one of several would too).
    /// </summary>
    internal static string Splice(string? content, string originalText, string newText)
    {
        var text = content ?? "";
        if (string.IsNullOrEmpty(originalText))
            return newText + text;

        try
        {
            return MeshOperations.AnchoredReplace(text, originalText, newText ?? "", replaceAll: false, out _);
        }
        catch (AnchorNotFoundException)
        {
            throw new InvalidOperationException(
                $"Text '{Truncate(originalText)}' is not present in the document — it may have been edited since you read it.");
        }
        catch (AmbiguousAnchorException ex)
        {
            throw new InvalidOperationException(
                $"Text '{Truncate(originalText)}' occurs {ex.OccurrenceCount} times in the document — include more surrounding context to make the match unique.");
        }
    }

    private static string Describe(string documentPath, string originalText, string newText)
    {
        if (string.IsNullOrEmpty(originalText))
            return $"Inserted \"{Truncate(newText)}\" in {documentPath} — visible as a tracked change, revertible from the document.";
        if (string.IsNullOrEmpty(newText))
            return $"Deleted \"{Truncate(originalText)}\" from {documentPath} — visible as a tracked change, revertible from the document.";
        return $"Replaced \"{Truncate(originalText)}\" with \"{Truncate(newText)}\" in {documentPath} — visible as a tracked change, revertible from the document.";
    }

    /// <summary>
    /// Posts a request via <c>hub.Observe(...)</c> and projects the callback into the tool's ANSWER.
    /// No <c>await</c>: the response arrives on a non-hub thread. Routing failures surface as a
    /// user-actionable error pointing the agent back at the "use `path`, not `name`" rule, and a
    /// response that never comes at all still answers — <c>DefaultIfEmpty</c> is what keeps an
    /// evicted callback stream from parking the round.
    /// </summary>
    private IObservable<string> PostAndReport<TResponse>(
        IRequest<TResponse> request,
        Address target,
        string originalInput,
        Func<TResponse, string> formatSuccess)
    {
        var delivery = hub.Post(request, o => o.WithTarget(target))!;
        return hub.Observe(delivery)
            .Take(1)
            .Select(callback =>
            {
                if (callback.Message is not TResponse typed)
                    return $"Error: unexpected response {callback.Message?.GetType().Name ?? "null"} for {originalInput}.";
                try { return formatSuccess(typed); }
                catch (Exception ex) { return $"Error formatting response: {ex.Message}"; }
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(
                    ex,
                    "Delivery to {Target} failed for {RequestType}. Original input: {OriginalInput}",
                    target, request.GetType().Name, originalInput);
                return Observable.Return(
                    $"Error: {ex.Message ?? "delivery failed"}. " +
                    $"Check that '{originalInput}' resolves to an existing node — pass the MeshNode's " +
                    "`path` property, not its `name`. If you only know the display name, call " +
                    "Search('name:\"...\"') and use the `path` field of the match.");
            })
            .DefaultIfEmpty($"Error: no response for {originalInput} — the request was delivered but the callback stream ended without one.");
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
