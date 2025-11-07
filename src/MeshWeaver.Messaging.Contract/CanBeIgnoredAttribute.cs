namespace MeshWeaver.Messaging;

/// <summary>
/// Marks a message type that can be safely ignored without reporting a failure.
/// This is useful for cleanup or shutdown messages where no handler is expected.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public class CanBeIgnoredAttribute : Attribute
{
}
