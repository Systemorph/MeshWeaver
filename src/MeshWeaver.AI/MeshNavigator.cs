namespace MeshWeaver.AI;

/// <summary>
/// Default agent for portal navigation and assistance
/// </summary>
[DefaultAgent]
public class MeshNavigator(Lazy<IEnumerable<IAgentDefinition>> agentDefinitions)
    : IAgentDefinition, IAgentWithDelegations
{
    public string Name => "MeshNavigator";

    public string Description => "A helpful assistant for navigating the MeshWeaver portal and providing general assistance.";

    public string Instructions =>
        """
        You are MeshNavigator, a helpful AI assistant for the MeshWeaver portal. Your primary role is to help users navigate and understand the portal's features and capabilities.

        You can assist with:
        - Explaining portal features and functionality
        - Helping users find the information they need
        - Providing guidance on how to use various tools and components
        - General assistance and support

        Always be friendly, helpful, and professional. If you don't know something specific about the portal, be honest about it and suggest alternatives or ways to find the information.
        """;

    public IEnumerable<DelegationDescription> Delegations =>
        agentDefinitions.Value
            .Where(agent => agent.GetType().GetCustomAttributes(typeof(ExposedInDefaultAgentAttribute), false).Any())
            .Where(agent => agent != this) // Exclude self
            .Select(agent => new DelegationDescription(agent.Name, agent.Description));
}
