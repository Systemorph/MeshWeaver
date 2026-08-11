namespace MeshWeaver.AI.Test;

/// <summary>
/// The ids of the skills the framework ships — the ONE expectation every built-in-catalog test
/// asserts against.
///
/// <para>🚨 The shipped set is derived from FILES, not from any list in production code:
/// <see cref="BuiltInSkillProvider"/> enumerates <c>content/ai/Skill/*.md</c> (embedded as
/// <c>Data/Skill/*.md</c> by a single <c>EmbeddedResource</c> glob), so adding a skill file IS
/// adding a skill. There is therefore nothing to update on the production side — only this
/// expectation.</para>
///
/// <para>It lives here because the literal used to be duplicated across two test files, and adding
/// one skill turned both red at once with no hint that a second copy existed. Adding a skill is now
/// a ONE-line change here rather than a hunt for every assertion that happens to spell the set
/// out.</para>
/// </summary>
internal static class BuiltInSkillCatalog
{
    /// <summary>Every shipped skill id, in sorted order — compare against
    /// <c>.Select(n =&gt; n.Id).OrderBy(x =&gt; x)</c>.</summary>
    public static readonly string[] ExpectedIds =
    [
        "access", "activity", "agent", "clear", "code", "feedback", "group", "harness",
        "layout-area", "markdown", "maui", "model", "navigate", "og-card", "presentation",
        "provider-keys", "pull-request", "slide", "space", "thread",
    ];
}
