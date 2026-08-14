using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: MeshWeaver.AI.ClaudeCode.ClaudeCodePack]

namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// Boot-pack registration for the Claude Code CLI harness. Loading this DLL via
/// <c>Modules:Assemblies</c> performs the same registration the portal's
/// <c>services.AddClaudeCode(configuration)</c> call did (harness + runtime info + options bound
/// from the <c>ClaudeCode</c> section). A deployment without the co-hosted CLI simply omits the
/// DLL — the harness catalog then never lists it (the graceful zero-harness path).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ClaudeCodePackAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.AI.ClaudeCode")
        {
            Name = "Claude Code CLI harness",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services =>
        {
            services.AddClaudeCode();
            // Self-sufficient config: bind the ClaudeCode section, and derive SkillsDirectory
            // exactly as the portal used to (Skills:Directory, else {ConfigDirRoot}/_skills).
            services.AddOptions<ClaudeCodeConfiguration>()
                .BindConfiguration("ClaudeCode")
                .PostConfigure<IConfiguration>((config, configuration) =>
                    config.SkillsDirectory ??= DeriveSkillsDirectory(configuration));
            return services;
        }),
    ];

    /// <summary>The shared skills folder: <c>Skills:Directory</c>, else <c>{ClaudeCode:ConfigDirRoot}/_skills</c>.</summary>
    public static string? DeriveSkillsDirectory(IConfiguration configuration)
    {
        var configured = configuration["Skills:Directory"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var claudeRoot = configuration["ClaudeCode:ConfigDirRoot"]?.TrimEnd('/', '\\');
        return string.IsNullOrEmpty(claudeRoot) ? null : $"{claudeRoot}/_skills";
    }
}
