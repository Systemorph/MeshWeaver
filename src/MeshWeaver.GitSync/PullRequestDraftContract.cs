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
/// The draft used when no better drafter is registered: a deterministic title and a body that says
/// what the PR mirrors and asks the human to edit it.
///
/// <para>🚨 This exists so PR creation does not DEPEND on AI. The real drafter lives in
/// <c>MeshWeaver.AI</c> and registers with a plain <c>AddSingleton</c> while this one registers
/// with <c>TryAddSingleton</c> — which makes the override order-independent: whichever configures
/// first, the AI implementation is the one resolved, and a deployment without the AI module gets
/// this instead of a DI resolution failure (issue #2276, the LogIncidentEndpoints shape).</para>
/// </summary>
public sealed class PlaceholderPullRequestDraftService : IPullRequestDraftService
{
    /// <inheritdoc />
    public IObservable<PullRequestDraft> DraftAsync(
        string spaceName, string? spaceSummary, string headBranch, string baseBranch,
        CancellationToken ct = default)
        => System.Reactive.Linq.Observable.Return(new PullRequestDraft(
            $"Sync {spaceName} ({headBranch} → {baseBranch})",
            $"Mirrors the **{spaceName}** Space from MeshWeaver into `{headBranch}`.\n\n"
            + "_(No AI drafter is installed — edit this body before submitting.)_"));
}
