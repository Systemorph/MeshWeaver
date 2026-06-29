#nullable enable
namespace MeshWeaver.ServiceProvider;

/// <summary>
/// Marks a field or property to be populated via dependency injection when an instance is
/// processed by <c>Buildup</c>. The decorated member's type is resolved from the service provider.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class InjectAttribute : Attribute
{
}