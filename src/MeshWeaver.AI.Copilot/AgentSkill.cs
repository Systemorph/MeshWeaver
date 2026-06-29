namespace MeshWeaver.AI.Copilot;

/// <summary>
/// A MeshWeaver agent projected for the GitHub Copilot harness — its id/name/description plus the
/// agent's instructions. The Copilot CLI/SDK has no filesystem "skills" folder (unlike Claude Code),
/// so these are injected into the Copilot session's system message rather than written as files.
/// Built by <see cref="CopilotHarness"/> from the live agent registry and consumed by
/// <see cref="CopilotChatClient"/>.
/// </summary>
public sealed record AgentSkill(string Id, string Name, string? Description, string Instructions);
