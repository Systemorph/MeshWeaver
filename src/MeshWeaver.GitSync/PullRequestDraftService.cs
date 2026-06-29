using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.AI;
using MeshWeaver.Reactive;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>The AI-suggested draft for a pull request — a title and a markdown body.</summary>
public record PullRequestDraft(string Title, string Body);

/// <summary>
/// Drafts a pull-request title + body from the change context (the Space name/summary,
/// the head vs base branch). Implementations delegate to the existing AI agent surface —
/// never a hand-rolled LLM HTTP call. Reactive: emits exactly once on success, OnError on
/// failure (no model configured, empty response, network error).
/// </summary>
public interface IPullRequestDraftService
{
    /// <summary>
    /// Produces a suggested PR title + body. <paramref name="spaceName"/> /
    /// <paramref name="spaceSummary"/> describe what changed; <paramref name="headBranch"/> /
    /// <paramref name="baseBranch"/> are the PR's branches.
    /// </summary>
    IObservable<PullRequestDraft> DraftAsync(
        string spaceName, string? spaceSummary, string headBranch, string baseBranch,
        CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IPullRequestDraftService"/> — spins up a fresh <see cref="AgentChatClient"/>
/// per call, selects the built-in <c>PullRequestWriter</c> agent, sends a single user message
/// describing the change context, and parses the <c>Title:</c> / <c>Body:</c> blocks from the
/// response. Mirrors <see cref="DescriptionGenerator"/> exactly — the same one-shot agent pattern,
/// so no new AI plumbing is introduced.
/// </summary>
public sealed class PullRequestDraftService : IPullRequestDraftService
{
    private const string AgentName = "PullRequestWriter";

    private readonly IServiceProvider services;
    private readonly ILogger<PullRequestDraftService>? logger;

    /// <summary>Initializes a new instance of the <c>PullRequestDraftService</c> class.</summary>
    /// <param name="services">The service provider used to build a per-call <see cref="AgentChatClient"/> and resolve the logger.</param>
    public PullRequestDraftService(IServiceProvider services)
    {
        this.services = services;
        logger = (ILogger<PullRequestDraftService>?)services.GetService(typeof(ILogger<PullRequestDraftService>));
    }

    /// <inheritdoc />
    public IObservable<PullRequestDraft> DraftAsync(
        string spaceName, string? spaceSummary, string headBranch, string baseBranch,
        CancellationToken ct = default)
    {
        var chat = new AgentChatClient(services);
        chat.Initialize(contextPath: "Agent");
        return chat.WhenInitialized.Take(1).SelectMany(_ =>
        {
            chat.SetSelectedAgent(AgentName);
            var prompt = BuildPrompt(spaceName, spaceSummary, headBranch, baseBranch);
            var messages = new[] { new ChatMessage(ChatRole.User, prompt) };
            return chat.GetResponseAsync(messages, ct).ToObservableSequence()
                .Aggregate(new StringBuilder(), (sb, msg) =>
                {
                    foreach (var content in msg.Contents.OfType<TextContent>())
                        sb.Append(content.Text);
                    return sb;
                })
                .Select(sb =>
                {
                    var raw = sb.ToString();
                    var draft = Parse(raw, spaceName, headBranch, baseBranch);
                    if (draft is null)
                    {
                        logger?.LogWarning(
                            "PullRequestWriter response was not parsable. Raw: {Raw}", raw);
                        throw new InvalidOperationException("Agent did not return a usable PR draft.");
                    }
                    return draft;
                });
        });
    }

    private static string BuildPrompt(string spaceName, string? spaceSummary, string headBranch, string baseBranch)
    {
        var sb = new StringBuilder();
        sb.Append("Space: ").AppendLine(string.IsNullOrWhiteSpace(spaceName) ? "(unnamed)" : spaceName.Trim());
        if (!string.IsNullOrWhiteSpace(spaceSummary))
            sb.Append("Summary: ").AppendLine(spaceSummary.Trim());
        sb.Append("Head branch: ").AppendLine(headBranch);
        sb.Append("Base branch: ").Append(baseBranch);
        return sb.ToString();
    }

    // The PullRequestWriter agent answers with a "Title:" line then a "Body:" block (everything
    // after the Body label, possibly multi-line) — parse by label prefix.
    private static readonly Regex TitleLineRegex = new(
        @"(?im)^\s*Title:\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex BodyBlockRegex = new(
        @"(?ims)^\s*Body:\s*(.+)\z", RegexOptions.Compiled);

    private static PullRequestDraft? Parse(string text, string spaceName, string headBranch, string baseBranch)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var titleMatch = TitleLineRegex.Match(text);
        var bodyMatch = BodyBlockRegex.Match(text);

        var title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim().Trim('"') : null;
        var body = bodyMatch.Success ? bodyMatch.Groups[1].Value.Trim() : null;

        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body))
            return null; // unparsable — surface as error so the caller can fall back

        // A title is required for the PR; synthesize a sane default when the agent omitted it.
        title ??= $"Sync {spaceName} ({headBranch} → {baseBranch})";
        body ??= "";
        return new PullRequestDraft(title, body);
    }
}
