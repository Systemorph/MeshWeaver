using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Northwind.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Northwind.AI;

/// <summary>
/// Northwind data agent that provides access to Northwind domain data and collections
/// </summary>
public class NorthwindDataAgent : IAgentDefinition
{
    private readonly IMessageHub hub;

    public NorthwindDataAgent(IMessageHub hub)
    {
        this.hub = hub;
    }

    public string AgentName => "NorthwindDataAgent";

    public string Description => "Provides access to Northwind domain data including customers, orders, products, and other business entities. Can query and analyze Northwind business data.";

    public string Instructions =>
        """
        You are the NorthwindDataAgent, specialized in working with Northwind business data. You have access to:
        
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
        """;    public IEnumerable<object> GetPlugins()
    {
        var plugins = new List<object>();

        // Get domain types and create DataPlugin
        try
        {
            // Get the TypeRegistry directly from the ServiceProvider
            var typeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
            
            // Get all registered types from the TypeRegistry and filter for Northwind domain types
            var typeDefinitionMap = new Dictionary<string, TypeDefinition>();
            
            foreach (var kvp in typeRegistry.Types)
            {
                var typeName = kvp.Key;
                var domainTypeDefinition = kvp.Value;
                
                // Filter for Northwind domain types
                if (domainTypeDefinition.Type.Namespace?.StartsWith("MeshWeaver.Northwind.Domain") == true &&
                    !typeName.EndsWith("Request", StringComparison.OrdinalIgnoreCase) &&
                    !typeName.EndsWith("Response", StringComparison.OrdinalIgnoreCase) &&
                    !typeName.EndsWith("Command", StringComparison.OrdinalIgnoreCase) &&
                    !typeName.EndsWith("Event", StringComparison.OrdinalIgnoreCase))
                {
                    // Create a new AI TypeDefinition from the domain type definition
                    var addressTypeName = typeName.Split('.').LastOrDefault() ?? typeName;
                    var address = new Address("data", addressTypeName.ToLowerInvariant());
                    
                    // Create our AI TypeDefinition
                    var aiTypeDefinition = new TypeDefinition(
                        domainTypeDefinition.Type,
                        domainTypeDefinition.CollectionName,
                        address,
                        domainTypeDefinition.DisplayName
                    );
                    
                    typeDefinitionMap[typeName] = aiTypeDefinition;
                }
            }

            if (typeDefinitionMap.Any())
            {
                plugins.Add(new DataPlugin(hub, typeDefinitionMap));
            }
        }
        catch (Exception)
        {
            // If domain types retrieval fails, continue without DataPlugin
        }

        // Add CollectionPlugin for file operations
        plugins.Add(new CollectionPlugin(hub));

        return plugins;
    }
}
