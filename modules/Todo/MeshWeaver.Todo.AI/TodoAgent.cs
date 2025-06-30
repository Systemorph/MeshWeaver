using System.ComponentModel;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Messaging;
using Microsoft.SemanticKernel;
using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Todo.AI;

/// <summary>
/// Todo data agent that provides access to Todo domain data and collections
/// </summary>
[ExposedInNavigator]
public class TodoAgent(IMessageHub hub) : IInitializableAgent, IAgentWithPlugins
{
    private static readonly ApplicationAddress TodoApplicationAddress = new("Todo");
    private Dictionary<string, TypeDescription>? typeDefinitionMap;
    public string Name => "TodoAgent";
    public string Description => "Handles all questions and actions related to todo items, categories, and task management. Provides access to todo data, allows creation, categorization, and management of todo items.";
    public string Instructions =>
        $@"You are the TodoAgent, specialized in managing todo items. You can:
- List, create, and update todo items
- Assign todo items to categories
- Retrieve and match categories using the DataPlugin

To create a new todo item:
1. Ask the user for the title, description, due date, and category.
2. Use the DataPlugin to get available categories (function: {nameof(DataPlugin.GetData)}, type: 'TodoCategory').
3. Match the user's category input to an existing category from the DataPlugin.
4. Use the {nameof(CreateTodo)} function to create a new 'TodoItem' with the provided details and matched category.
Always use the DataPlugin for data access and category matching.

Furthermore, you can get a list of TodoItem from the {nameof(DataPlugin.GetData)} function with the type 'TodoItem' or retrieve a specific TodoItem by its ID using the same function with the entityId parameter.
";

    [KernelFunction]
    [Description("Creates a new todo item with the specified title, description, due date, and category. The category must be an existing category from the DataPlugin.")]
    public void CreateTodo(string title, string description, 
                           DateTime dueDate, 
                           [Description("The category of the todo item. Must be an existing category from the DataPlugin.")] string category)
    {
        var json = $$$"""
                      {
                        "$type": "TodoItem",
                        "title": "{{{title}}}",
                        "description": "{{{description}}}",
                        "dueDate": "{{{dueDate:yyyy-MM-ddTHH:mm:ssZ}}}",
                        "category": "{{{category}}}"
                      }
                      """; 

        // Use the DataPlugin to create the todo item
        hub.Post(new DataChangeRequest(){Creations = [JsonDocument.Parse(json).RootElement] }, o => o.WithTarget(TodoApplicationAddress));
    }
    IEnumerable<KernelPlugin> IAgentWithPlugins.GetPlugins(IAgentChat chat)
    {
        var data = new DataPlugin(hub, chat, typeDefinitionMap, _ => TodoApplicationAddress);
        yield return data.CreateKernelPlugin();
    }

    async Task IInitializableAgent.InitializeAsync()
    {
        var response = await hub.AwaitResponse(new GetDomainTypesRequest(), o => o.WithTarget(TodoApplicationAddress));
        typeDefinitionMap = response.Message.Types.ToDictionary(x => x.Name);
    }
}
