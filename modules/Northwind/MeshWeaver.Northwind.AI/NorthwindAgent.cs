using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.SemanticKernel;

namespace MeshWeaver.Northwind.AI;

/// <summary>
/// Northwind data agent that provides access to Northwind domain data and collections
/// </summary>
[ExposedInNavigator]
public class NorthwindAgent(IMessageHub hub) : IInitializableAgent, IAgentWithPlugins
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    /// <inheritdoc cref="IAgentDefinition"/>
    public string Name => "NorthwindAgent";

    /// <inheritdoc cref="IAgentDefinition"/>
    public string Description => "Any question around the Northwind domain should direct here. Provides access to Northwind domain data including customers, orders, products, and other business entities. Can query and analyze Northwind business data.";

    /// <inheritdoc cref="IAgentDefinition"/>
    public string Instructions =>
        """
        You are the NorthwindAgent, specialized in working with Northwind business data. You have access to:
        
        - Customer data: information about companies, contacts, and addresses
        - Order data: sales orders, order details, and order history
        - Product data: product catalog, categories, suppliers, and inventory
        - Employee data: staff information and territories
        - Geographic data: regions, territories, and shipping information
        
        You can help users:
        - Query and analyze business data
        - Generate reports and insights
        - Answer questions about customers, orders, products, and sales
        - Provide data-driven recommendations
        
        Use the DataPlugin to access structured domain data and the CollectionPlugin to work with any related files or documents.
        Always provide accurate, data-driven responses based on the available Northwind data.
        """;

    IEnumerable<KernelPlugin> IAgentWithPlugins.GetPlugins(IAgentChat chat)
    {
        var data = new DataPlugin(hub, chat, typeDefinitionMap);
        yield return data.CreateKernelPlugin();
    }

    private static readonly Address NorthwindAddress = new ApplicationAddress("Northwind");
    async Task IInitializableAgent.InitializeAsync()
    {
        var response = await hub.AwaitResponse(new GetDomainTypesRequest(), o => o.WithTarget(NorthwindAddress));
        typeDefinitionMap = response.Message.Types.ToDictionary(x => x.Name);
    }
}
