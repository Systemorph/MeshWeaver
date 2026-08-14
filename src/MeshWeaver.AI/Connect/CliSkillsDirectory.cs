using Microsoft.Extensions.Configuration;

namespace MeshWeaver.AI.Connect;

/// <summary>
/// The one derivation of the co-hosted CLIs' shared skills folder:
/// <c>Skills:Directory</c> when configured, else <c>{ClaudeCode:ConfigDirRoot}/_skills</c>.
/// Both CLI packs (ClaudeCode, Copilot) use this so the rule cannot drift between them.
/// </summary>
public static class CliSkillsDirectory
{
    /// <summary>Resolves the skills directory from configuration; null when neither key is set.</summary>
    public static string? Derive(IConfiguration configuration)
    {
        var configured = configuration["Skills:Directory"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var claudeRoot = configuration["ClaudeCode:ConfigDirRoot"]?.TrimEnd('/', '\\');
        return string.IsNullOrEmpty(claudeRoot) ? null : $"{claudeRoot}/_skills";
    }
}
