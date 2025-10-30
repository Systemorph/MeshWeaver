namespace MeshWeaver.Messaging;

/// <summary>
/// Marks a property or field to be excluded from serialization when logging.
/// This is useful for properties with large payloads that would clutter logs.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public class PreventLoggingAttribute : Attribute
{
}
