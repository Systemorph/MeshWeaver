using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Data;
using MeshWeaver.Insurance.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using Microsoft.SemanticKernel;

namespace MeshWeaver.Insurance.AI;

/// <summary>
/// Main Insurance agent that provides access to insurance pricing data and collections.
/// </summary>
[DefaultAgent]
public class InsuranceAgent(IMessageHub hub) : IInitializableAgent, IAgentWithPlugins
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private Dictionary<string, LayoutAreaDefinition>? layoutAreaMap;

    public string Name => "InsuranceAgent";

    public string Description =>
        "Handles all questions and actions related to insurance pricings, property risks, and dimensions. " +
        "Provides access to pricing data, allows creation and management of pricings and property risks.";

    public string Instructions =>
        $$$"""
        The agent is the InsuranceAgent, specialized in managing insurance pricings:

        To work with pricing data:
        1. You can retrieve the id of the pricing from the address of the context. The address is in the format insurance-pricing/{{pricingId}}
        2. Use the DataPlugin to get available data (function: {{{nameof(DataPlugin.GetData)}}}) with the appropriate type. The available types are:
            {{{nameof(Pricing)}}}, {{{nameof(PropertyRisk)}}}, {{{nameof(LineOfBusiness)}}}, {{{nameof(Country)}}}, {{{nameof(LegalEntity)}}}, {{{nameof(Currency)}}}
        3. When asked to modify an entity, load the entity using the {{{nameof(DataPlugin.GetData)}}} function with the entityId parameter,
           modify the entity as requested, and save it back using the {{{nameof(DataPlugin.UpdateData)}}} function.

        Furthermore, you can get a list of entities from the {{{nameof(DataPlugin.GetData)}}} function or retrieve a specific entity by
        its ID using the same function with the entityId parameter.

        When displaying layout areas, use the LayoutAreaPlugin with the {{{nameof(LayoutAreaPlugin.DisplayLayoutArea)}}} function.
        Available layout areas can be listed using the {{{nameof(LayoutAreaPlugin.GetLayoutAreas)}}} function.
        """;

    IEnumerable<KernelPlugin> IAgentWithPlugins.GetPlugins(IAgentChat chat)
    {
        yield return new DataPlugin(hub, chat, typeDefinitionMap).CreateKernelPlugin();
        yield return new LayoutAreaPlugin(hub, chat, layoutAreaMap).CreateKernelPlugin();
    }

    async Task IInitializableAgent.InitializeAsync()
    {
        try
        {
            var typesResponse = await hub.AwaitResponse(
                new GetDomainTypesRequest(),
                o => o.WithTarget(new PricingAddress("default")));
            typeDefinitionMap = typesResponse.Message.Types.ToDictionary(x => x.Name);
        }
        catch
        {
            typeDefinitionMap = null;
        }

        try
        {
            var layoutAreaResponse = await hub.AwaitResponse(
                new GetLayoutAreasRequest(),
                o => o.WithTarget(new PricingAddress("default")));
            layoutAreaMap = layoutAreaResponse.Message.Areas.ToDictionary(x => x.Area);
        }
        catch
        {
            layoutAreaMap = null;
        }
    }
}
