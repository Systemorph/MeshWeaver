namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// A MeshWeaver agent projected for the Claude Code harness — its id/name/description plus the
/// agent's instructions, written per spawn as a Claude Code skill
/// (<c>{CLAUDE_CONFIG_DIR}/skills/&lt;id&gt;/SKILL.md</c>). The mesh is the CLI's workspace, so its
/// skills ARE the user's selectable agents. Built by <see cref="ClaudeCodeHarness"/> from the live
/// agent registry and consumed by <see cref="ClaudeCodeChatClient"/>.
/// </summary>
public sealed record AgentSkill(string Id, string Name, string? Description, string Instructions);
