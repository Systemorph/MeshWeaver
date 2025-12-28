using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.Northwind.AI;

/// <summary>
/// Northwind data agent that provides access to Northwind domain data and collections
/// </summary>
[ExposedInDefaultAgent]
public class NorthwindAgent(IMessageHub hub) : IInitializableAgent, IAgentWithTools, IAgentWithContext
{
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private Dictionary<string, LayoutAreaDefinition>? layoutDefinitionMap;
    private Dictionary<string, string>? schemaMap;
    /// <inheritdoc cref="IAgentDefinition"/>
    public string Name => "NorthwindAgent";

    /// <inheritdoc />
    public string? GroupName => "Northwind";
    /// <inheritdoc />
    public int DisplayOrder => 0;
    /// <inheritdoc />
    public string? IconName => "Database";

    /// <inheritdoc cref="IAgentDefinition"/>
    public string Description => "Any question around the Northwind domain should direct here. Provides access to Northwind domain data including customers, orders, products, and other business entities. Can query and analyze Northwind business data.";

    /// <inheritdoc cref="IAgentDefinition"/>
    public string Instructions => BuildInstructions();

    private string BuildInstructions()
    {
        var schemaSection = schemaMap != null && schemaMap.Count > 0
            ? $"""

              ## Available Data Schemas

              {string.Join("\n\n", schemaMap.Select(kvp => $"### {kvp.Key}\n```json\n{kvp.Value}\n```"))}
              """
            : "";

        return $$"""
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
               - Layout areas (reports, views, charts, dashboards) related to Northwind data

               Use the DataPlugin to access structured domain data and the LayoutAreaPlugin to display visual components.
               Always provide accurate, data-driven responses based on the available Northwind data.

               ## CRITICAL: Updating Data

               When updating entities, you MUST follow this workflow:

               1. **Load the entity first**: If you don't know the exact ID, use GetData to list all entities of that type. If you know the ID, use GetData with the entityId parameter to load the specific entity.

               2. **Modify minimally**: Only change the properties that need to be updated. Preserve ALL other properties exactly as they were in the loaded entity. Do NOT remove or omit any properties.

               3. **Always include $type**: When calling UpdateData, the JSON MUST include the "$type" property. This is REQUIRED for the system to know which type is being updated. Example:
                  ```json
                  {
                    "$type": "Customer",
                    "customerId": "ALFKI",
                    "companyName": "Updated Company Name",
                    ... all other existing properties ...
                  }
                  ```

               4. **Call UpdateData**: Pass the complete JSON with all properties (modified and unmodified) to UpdateData.

               NEVER omit the $type property - updates will fail without it.
               {{schemaSection}}
               """;
    }

    IEnumerable<AITool> IAgentWithTools.GetTools(IAgentChat chat)
    {
        var data = new DataPlugin(hub, chat, typeDefinitionMap);
        foreach (var tool in data.CreateTools())
            yield return tool;

        var layout = new LayoutAreaPlugin(hub, chat, layoutDefinitionMap);
        foreach (var tool in layout.CreateTools())
            yield return tool;
    }

    private static readonly Address NorthwindAddress = AddressExtensions.CreateAppAddress("Northwind");

    async Task IInitializableAgent.InitializeAsync()
    {
        var typeResponse = await hub.AwaitResponse(new GetDomainTypesRequest(), o => o.WithTarget(NorthwindAddress));
        typeDefinitionMap = typeResponse?.Message?.Types?.ToDictionary(x => x.Name!);
        var layoutResponse = await hub.AwaitResponse(new GetLayoutAreasRequest(), o => o.WithTarget(NorthwindAddress));
        layoutDefinitionMap = layoutResponse?.Message?.Areas?.ToDictionary(x => x.Area);

        // Fetch schemas for all types
        schemaMap = new Dictionary<string, string>();
        if (typeDefinitionMap != null)
        {
            foreach (var typeName in typeDefinitionMap.Keys)
            {
                try
                {
                    var schemaResponse = await hub.AwaitResponse(
                        new GetDataRequest(new SchemaReference(typeName)),
                        o => o.WithTarget(NorthwindAddress));
                    if (schemaResponse?.Message?.Data is SchemaInfo schemaInfo)
                    {
                        schemaMap[typeName] = schemaInfo.Schema;
                    }
                }
                catch
                {
                    // Schema not available for this type, skip
                }
            }
        }
    }

    /// <summary>
    /// Matches addresses with Northwind in.
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public bool Matches(AgentContext? context)
    {
        if (context?.Address == null)
            return false;

        // Match if the address contains "Northwind" or starts with the Northwind address
        var contextAddress = context.Address.ToString();
        return contextAddress.Contains("Northwind", StringComparison.OrdinalIgnoreCase) ||
               contextAddress.StartsWith(NorthwindAddress.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
