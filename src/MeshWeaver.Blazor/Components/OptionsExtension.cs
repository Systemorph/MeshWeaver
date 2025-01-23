using MeshWeaver.Reflection;

namespace MeshWeaver.Blazor.Components;

public static class OptionsExtension
{
    public record Option(object Item, string Text, string ItemString, Type ItemType);

    internal static string MapToString(object instance, Type itemType) =>
        instance == null || IsDefault((dynamic)instance)
            ? GetDefault(itemType)
            : instance.ToString();

    private static string GetDefault(Type itemType)
    {
        if (itemType == typeof(string) || itemType.IsNullableGeneric())
            return null;
        return Activator.CreateInstance(itemType)!.ToString();
    }

    private static bool IsDefault<T>(T instance) => instance.Equals(default(T));

}
