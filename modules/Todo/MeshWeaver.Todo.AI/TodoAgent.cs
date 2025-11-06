using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;

namespace MeshWeaver.Todo.AI;

/// <summary>
/// Todo data agent that provides access to Todo domain data and collections
/// </summary>
[ExposedInDefaultAgent]
public class TodoAgent(IMessageHub hub) : IInitializableAgent, IAgentWithTools, IAgentWithContext
{
    private static readonly ApplicationAddress TodoApplicationAddress = new("Todo");
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    private Dictionary<string, LayoutAreaDefinition>? layoutAreaMap;
    public string Name => "TodoAgent";
    public string Description => "Handles all questions and actions related to todo items, categories, and task management. Provides access to todo data, allows creation, categorization, and management of todo items.";
    public string Instructions =>
        $@"""The agent is the TodoAgent, specialized in managing todo items:
        - List, create, and update todo items (using the {nameof(DataPlugin.GetData)} tool with type 'TodoItem')
        - Assign todo items to categories (using the {nameof(DataPlugin.GetData)} tool with type 'TodoCategory')
        - Update existing todo items (using {nameof(DataPlugin.UpdateData)} with the json and type 'TodoItem')
    
        Today's date is {DateTime.UtcNow.ToLongDateString()}.

        To create a new todo item:
        1. Try to find title description and category and due date as best as you can from the user's input.
        2. Use the DataPlugin to get available categories (function: {nameof(DataPlugin.GetData)}, type: 'TodoCategory') and try to match a good category.
        3. When asked to create a new 'TodoItem', use the {nameof(DataPlugin.GetSchema)} method with type 'TodoItem' to get the schema. Fill in the provided details and matched category.

        Always use the DataPlugin for data access and category matching.

        Furthermore, you can get a list of TodoItem from the {nameof(DataPlugin.GetData)} function with the type 'TodoItem' or retrieve a specific TodoItem by its ID using the same function with the entityId parameter.
        """;

    IEnumerable<AITool> IAgentWithTools.GetTools(IAgentChat chat)
    {
        var dataPlugin = new DataPlugin(hub, chat, typeDefinitionMap, _ => TodoApplicationAddress);
        var layoutAreaPlugin = new LayoutAreaPlugin(hub, chat, layoutAreaMap, _ => TodoApplicationAddress);

        foreach (var tool in dataPlugin.CreateTools())
            yield return tool;

        foreach (var tool in layoutAreaPlugin.CreateTools())
            yield return tool;
    }

    async Task IInitializableAgent.InitializeAsync()
    {
        var typesResponse = await hub.AwaitResponse(new GetDomainTypesRequest(), o => o.WithTarget(TodoApplicationAddress));
        typeDefinitionMap = typesResponse.Message.Types.ToDictionary(x => x.Name);
        var layoutAreaResponse = await hub.AwaitResponse(new GetLayoutAreasRequest(), o => o.WithTarget(TodoApplicationAddress));
        layoutAreaMap = layoutAreaResponse.Message.Areas.ToDictionary(x => x.Area);
    }

    public bool Matches(AgentContext? context)
    {
        if (context?.Address == null)
            return false;

        // Match if the address contains "Todo" or starts with the Todo address
        var contextAddress = context.Address.ToString();
        return contextAddress.Contains("Todo", StringComparison.OrdinalIgnoreCase) ||
               contextAddress.StartsWith(TodoApplicationAddress.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
