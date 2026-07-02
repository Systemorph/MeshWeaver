namespace MeshWeaver.AI;

/// <summary>
/// Conventions for harness/agent/model selection values. Pickers and composer state store the
/// picked node's full PATH (e.g. <c>Harness/MeshWeaver</c>, <c>Provider/Anthropic/claude-…</c>,
/// <c>Agent/Assistant</c>) and that path flows end-to-end through composer → message → Pending* →
/// RoundParams (never pre-resolved at the GUI — see CLAUDE.md "pass node PATHS through").
/// Execution matches REGISTERED ids/names (<c>IHarness.Id</c>, agent name, model id), which are
/// always the LAST path segment — so the execution boundary normalizes with <see cref="IdOf"/>,
/// accepting both forms.
/// </summary>
public static class SelectionId
{
    /// <summary>
    /// The id (last path segment) of a picked node path; bare ids pass through unchanged.
    /// <para>🚨 Invariant: correct ONLY while no registered id (<c>IHarness.Id</c>, agent
    /// name, model id) itself contains <c>/</c>. Model ids are the risk case — some providers
    /// use org/model-shaped ids (e.g. HuggingFace). If such a provider is onboarded, its
    /// catalog node id must be the last segment only (the picker path supplies the prefix),
    /// or this normalization must learn the known catalog prefixes instead.</para>
    /// </summary>
    public static string? IdOf(string? pathOrId)
        => string.IsNullOrEmpty(pathOrId)
            ? pathOrId
            : pathOrId[(pathOrId.LastIndexOf('/') + 1)..];
}
