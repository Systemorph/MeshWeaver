using MeshWeaver.Reflection;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Utility class for converting layout control options into a strongly-typed
/// <c>Option</c> representation used by list-based form components.
/// </summary>
public static class OptionsExtension
{
    /// <summary>
    /// Represents a single selectable option in a list-based form control.
    /// </summary>
    /// <param name="Item">The underlying data item that this option wraps.</param>
    /// <param name="Text">The display label shown to the user.</param>
    /// <param name="ItemString">String representation of <paramref name="Item"/> used for equality comparisons.</param>
    /// <param name="ItemType">The <c>System.Type</c> of <paramref name="Item"/>.</param>
    /// <param name="Icon">Optional icon identifier shown alongside the label.</param>
    public record Option(object? Item, string? Text, string? ItemString, Type ItemType, string? Icon = null);

    internal static string? MapToString(object? instance, Type itemType) =>
        instance == null || IsDefault((dynamic)instance)
            ? GetDefault(itemType)
            : instance!.ToString();

    private static string? GetDefault(Type itemType)
    {
        if (itemType == typeof(string) || itemType.IsNullableGeneric())
            return null;
        return Activator.CreateInstance(itemType)!.ToString();
    }

    private static bool IsDefault<T>(T instance) => instance is null || instance.Equals(default(T));

}
