namespace MeshWeaver.AI;

/// <summary>
/// One authored AI-content file (<c>content/ai/Agent/*.md</c>, <c>content/ai/Skill/*.md</c>) that did
/// NOT load into a node — almost always invalid YAML front matter.
///
/// <para>🚨 Why this type exists at all. These files are parsed during mesh startup, so a parse error
/// must NOT throw (one bad file would stop the host from starting — the 2026-08-07 incident, where an
/// unquoted <c>:</c> in one agent's description took every full-mesh suite red at once and reported it
/// as ~400 unrelated failures with nothing naming the file). The provider therefore SKIPS a file it
/// cannot parse. But "skip" must never mean "silently vanish": a skipped file leaves the catalog one
/// entry shorter with no error anywhere, which a user experiences as <i>"my skill doesn't appear and
/// nothing is red"</i>. Recording the failure — and naming the file — is what turns that invisible
/// degradation into a diagnosable one, and is what the catalog guard tests assert on
/// (<c>BuiltInSkillCatalogTest</c> / <c>BuiltInAgentContentTest</c>).</para>
/// </summary>
/// <param name="File">The file (or embedded resource) that failed to load, named so it can be fixed.</param>
/// <param name="Reason">Why it failed, in terms an author can act on.</param>
public sealed record AiContentLoadFailure(string File, string Reason)
{
    /// <summary>The one-line diagnostic written to stderr and used in test failure messages.</summary>
    public override string ToString() => $"{File}: {Reason}";
}
