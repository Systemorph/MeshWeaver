using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: MeshWeaver.AI.AzureFoundry.AzureFoundryProviders]

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Boot-pack registration for the Anthropic + Azure Foundry providers. Loading this DLL via
/// <c>Modules:Assemblies</c> registers everything the old <c>AddAnthropic()</c>/
/// <c>AddAzureFoundry()</c> builder calls did: the catalog sources plus one
/// <see cref="IChatClientFactory"/> each. A deployment drops the pair by removing the DLL from
/// its module list.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AzureFoundryProvidersAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.AI.AzureFoundry")
        {
            Name = "Anthropic + Azure Foundry language-model providers",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services =>
        {
            services.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: "Anthropic", ProviderName: "Anthropic", Order: 1,
                DisplayLabel: "Anthropic",
                DefaultEndpoint: "https://api.anthropic.com/v1/messages",
                // Latest model PER CATEGORY — the one place to bump on a new snapshot; see
                // AzureFoundryExtensions.AddAnthropic for the rationale.
                DefaultModelIds: ImmutableArray.Create(
                    "claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5-20251001"),
                RequiresApiKey: true));
            services.AddOptions<AzureClaudeConfiguration>().BindConfiguration("Anthropic");
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IChatClientFactory, AzureClaudeChatClientAgentFactory>());

            services.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: "AzureFoundry", ProviderName: "AzureFoundry", Order: 2,
                DisplayLabel: "Azure Foundry", DefaultEndpoint: null,
                DefaultModelIds: ImmutableArray<string>.Empty, RequiresApiKey: true));
            services.AddOptions<AzureFoundryConfiguration>().BindConfiguration("AzureFoundry");
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IChatClientFactory, AzureFoundryChatClientAgentFactory>());
            return services;
        }),
    ];
}
