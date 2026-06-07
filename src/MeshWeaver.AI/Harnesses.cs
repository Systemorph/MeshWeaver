namespace MeshWeaver.AI;

/// <summary>
/// The execution harnesses a thread can run under. A "harness" is the
/// top-level choice in the chat picker; it maps onto an agent's
/// <see cref="AgentConfiguration.GroupName"/>. For Claude Code / GitHub Copilot
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
    /// <summary>The native MeshWeaver agent harness — exposes agent + model selection.</summary>
    public const string MeshWeaver = "MeshWeaver";

    /// <summary>The Claude Code harness (built-in <c>ClaudeCode</c> agent).</summary>
    public const string ClaudeCode = "Claude Code";

    /// <summary>The GitHub Copilot harness (built-in <c>Copilot</c> agent).</summary>
    public const string Copilot = "GitHub Copilot";

    /// <summary>
    /// All harnesses in picker display order. MeshWeaver leads (it is the
    /// default), followed by the external harnesses.
    /// </summary>
    public static readonly string[] All = [MeshWeaver, ClaudeCode, Copilot];
}
