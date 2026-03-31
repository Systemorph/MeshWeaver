namespace MeshWeaver.Messaging;

/// <summary>
/// Marks a property, field, or class to be excluded from serialization when logging.
/// This is useful for properties with large payloads that would clutter logs.
///
/// When applied to a class: The entire message is excluded from logging (checked before serialization).
/// When applied to a property/field: The property is excluded during log serialization (via LoggingTypeInfoResolver).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class, Inherited = true)]
public class PreventLoggingAttribute : Attribute
{
}
