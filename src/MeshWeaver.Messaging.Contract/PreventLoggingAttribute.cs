namespace MeshWeaver.Messaging;

/// <summary>
/// Marks a property, field, or class to be excluded from serialization when logging.
/// This is useful for properties with large payloads that would clutter logs.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class, Inherited = true)]
public class PreventLoggingAttribute : Attribute
{
}
