using MeshWeaver.AI.Connect;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: MeshWeaver.AI.Copilot.CopilotPack]

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// Boot-pack registration for the GitHub Copilot CLI harness: the harness + model catalog, the
/// options bound from the <c>Copilot</c> section (with the shared skills-directory derivation),
/// and the Copilot connect strategy — everything the portal used to wire by direct type
/// reference. A deployment without the co-hosted CLI omits the DLL from its module list.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class CopilotPackAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.AI.Copilot")
        {
            Name = "GitHub Copilot CLI harness",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services =>
        {
            services.AddCopilot();
            services.AddOptions<CopilotConfiguration>()
                .BindConfiguration("Copilot")
                .PostConfigure<IConfiguration>((config, configuration) =>
                    config.SkillsDirectory ??= DeriveSkillsDirectory(configuration));
            // The per-user Connect flow's strategy — previously a direct type reference in the
            // portal composition; travels with the pack it belongs to.
            services.AddSingleton<IConnectStrategy, CopilotConnectStrategy>();
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
