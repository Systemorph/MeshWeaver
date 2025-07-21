#nullable enable
namespace MeshWeaver.AI;


/// <summary>
/// Attribute to mark an agent as the default/entry point agent
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class DefaultAgentAttribute : Attribute
{
}

