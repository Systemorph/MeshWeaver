#nullable enable
namespace MeshWeaver.AI;

/// <summary>
/// Attribute to mark an agent as exposed in the navigator for delegation
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class ExposedInNavigatorAttribute : Attribute;
