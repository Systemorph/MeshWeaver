using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.AI;

/// <summary>
/// Provides built-in agent nodes shipped from the platform.
/// These agents are available globally regardless of path/scope.
/// </summary>
public class BuiltInAgentProvider : IStaticNodeProvider
{
    /// <summary>
    /// The agent ID for the thread naming utility agent.
    /// </summary>
    public const string ThreadNamerId = "ThreadNamer";

    private static readonly AgentConfiguration ThreadNamerConfig = new()
    {
        Id = ThreadNamerId,
        Instructions = """
                        You are a thread naming assistant. Given the user's first message in a new conversation,
                        generate a concise descriptive name and a PascalCase identifier for the thread.

                        Respond with EXACTLY two lines, nothing else:
                        Name: <short descriptive name, 3-8 words, no quotes>
                        Id: <PascalCase identifier, alphanumeric only, no spaces>

                        Examples:
                        - "How do I set up CI/CD?" -> Name: Setting Up CI CD Pipeline / Id: SettingUpCiCdPipeline
                        - "What's the pricing for enterprise?" -> Name: Enterprise Pricing Inquiry / Id: EnterprisePricingInquiry
                        - "Fix the login bug" -> Name: Fix Login Bug / Id: FixLoginBug
                        """,
        ExposedInNavigator = false,
        DisplayOrder = 999, // Very low priority, utility agent
    };

    private static readonly MeshNode[] Nodes =
    [
        new(ThreadNamerId, "Agent")
        {
            Name = "Thread Namer",
            NodeType = "Agent",
            Content = ThreadNamerConfig
        }
    ];

    public IEnumerable<MeshNode> GetStaticNodes() => Nodes;
}
