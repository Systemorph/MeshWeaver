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
        "provider-keys", "pull-request", "share-email", "slide", "space", "thread",
    ];

    /// <summary>
    /// Asserts that <paramref name="actualIds"/> is exactly <see cref="ExpectedIds"/>, and — when it
    /// is not — says WHICH ids are missing or unexpected.
    ///
    /// <para>🚨 This exists because the plain collection assertion truncates both sides after ten
    /// entries. With a 20-plus-entry catalog that prints two identical-looking prefixes ending in
    /// <c>…</c>, so a one-skill difference is invisible in the log AND in the trx — the failure
    /// says only that two collections differ, which is exactly the information you already had.
    /// Naming the difference turns "somebody's skill did not ship" into a one-line diagnosis.</para>
    ///
    /// <para>Ordering is compared ORDINALLY on purpose: the shipped set is sorted for comparison,
    /// and a culture-sensitive sort treats punctuation (the hyphen in <c>og-card</c>,
    /// <c>share-email</c>) as ignorable, which would make the expected order depend on the
    /// machine's locale.</para>
    /// </summary>
    public static void AssertMatches(IEnumerable<string> actualIds)
    {
        var actual = actualIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var expected = ExpectedIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();

        var missing = expected.Except(actual, StringComparer.Ordinal).ToArray();
        var unexpected = actual.Except(expected, StringComparer.Ordinal).ToArray();

        if (missing.Length > 0 || unexpected.Length > 0)
            throw new InvalidOperationException(
                "The shipped skill set does not match the expected catalog. "
                + $"Missing (expected but not shipped): [{string.Join(", ", missing)}]. "
                + $"Unexpected (shipped but not expected): [{string.Join(", ", unexpected)}]. "
                + $"Shipped {actual.Length}, expected {expected.Length}. "
                + "A skill ships by its FILE — content/ai/Skill/{id}.md, embedded via the glob in "
                + "MeshWeaver.AI.csproj — so a missing id usually means the file is absent from the "
                + "build, and an unexpected id means this expectation needs the new skill added. "
                + $"Shipped set: [{string.Join(", ", actual)}].");

        actual.Should().Equal(expected);
    }
}
