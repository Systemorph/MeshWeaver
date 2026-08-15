using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: MeshWeaver.AI.OpenAI.OpenAIProviders]

namespace MeshWeaver.AI.OpenAI;

/// <summary>
/// Boot-pack registration for the OpenAI-wire provider family — OpenAI, Azure OpenAI, the generic
/// OpenAI-compatible custom-URL provider, and OpenRouter (one assembly, one gate). Loading this
/// DLL via <c>Modules:Assemblies</c> registers everything the old <c>AddOpenAI()</c>/
/// <c>AddAzureOpenAI()</c>/<c>AddOpenAICompatible()</c>/<c>AddOpenRouter()</c> builder calls did:
/// catalog sources (declarative data) plus one <see cref="IChatClientFactory"/> per provider.
/// A deployment drops the family by removing the DLL from its module list.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class OpenAIProvidersAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.AI.OpenAI")
        {
            Name = "OpenAI-wire language-model providers",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services =>
        {
            services.AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: "OpenAI", ProviderName: "OpenAI", Order: 4,
                DisplayLabel: "OpenAI", DefaultEndpoint: null,
                DefaultModelIds: ImmutableArray.Create("gpt-4o", "gpt-4o-mini"),
                RequiresApiKey: true));
            services.AddOptions<OpenAIConfiguration>().BindConfiguration("OpenAI");
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IChatClientFactory, OpenAIChatClientAgentFactory>());
            // Endpoint model discovery for the OpenAI-compatible provider (GET /v1/models →
            // ModelDefinition nodes). Formerly a flag-gated portal registration
            // (Features:Ai:Providers:OpenAICompatible) — riding the module now: it self-gates on
            // OpenAICompatible:Endpoint + the opt-in DiscoverModels flag, so listing the module
            // without that config keeps it inert.
            services.AddHostedService<OpenAICompatibleModelSync>();
            return services.AddAzureOpenAI().AddOpenAICompatible().AddOpenRouter();
        }),
    ];
}
