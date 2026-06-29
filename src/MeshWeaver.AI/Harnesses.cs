namespace MeshWeaver.AI;

/// <summary>
/// The execution harnesses a thread can run under. A "harness" is the
/// top-level choice in the chat picker; it maps onto an agent's
/// <see cref="AgentDisplayInfo.GroupName"/> (projected from the agent node's Category).
/// For Claude Code / GitHub Copilot
/// the harness <i>is</i> the choice — each resolves to a single built-in agent.
/// For MeshWeaver the user additionally picks an agent + model within the group.
/// </summary>
/// <remarks>
/// These are immutable constant lookups (no runtime writes), so the
/// <see cref="All"/> array is a sanctioned <c>static readonly</c> under the
/// no-static-state rule.
/// </remarks>
public static class Harnesses
{
    // 🚨 These ids are used VERBATIM as the harness mesh-node id: BuiltInHarnessProvider
    // creates `new MeshNode(Id, "Harness")` → path `Harness/{Id}`. So an id MUST be a
    // path-safe slug (no spaces) — a space here produced `Harness/Claude Code`, and
    // reading that space-containing path back tripped the resolver's space fragility →
    // NotFound → resubscribe storm → the "harness change crashes" bug. The friendly label
    // lives on `Harness.DisplayName` (the picker shows that, never the id).

    /// <summary>The native MeshWeaver agent harness — exposes agent + model selection.</summary>
    public const string MeshWeaver = "MeshWeaver";

    /// <summary>The Claude Code harness id (slug; display name "Claude Code").</summary>
    public const string ClaudeCode = "ClaudeCode";

    /// <summary>The GitHub Copilot harness id (slug; display name "GitHub Copilot").</summary>
    public const string Copilot = "Copilot";

    /// <summary>
    /// All harnesses in picker display order. MeshWeaver leads (it is the
    /// default), followed by the external harnesses.
    /// </summary>
    public static readonly string[] All = [MeshWeaver, ClaudeCode, Copilot];
}
