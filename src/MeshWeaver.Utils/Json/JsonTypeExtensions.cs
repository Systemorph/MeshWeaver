namespace MeshWeaver.Json;

/// <summary>
/// CLR-type predicates for "does this map to a JSON number" — used to pick a numeric editor
/// or a right-aligned grid column.
/// </summary>
public static class JsonTypeExtensions
{
    /// <summary>Whether <paramref name="type"/> is one of the CLR integer types.</summary>
    public static bool IsInteger(this Type type) =>
        type == typeof(byte) || type == typeof(sbyte)
        || type == typeof(short) || type == typeof(ushort)
        || type == typeof(int) || type == typeof(uint)
        || type == typeof(long) || type == typeof(ulong);

    /// <summary>Whether <paramref name="type"/> is one of the CLR floating-point types.</summary>
    public static bool IsFloatingPoint(this Type type) =>
        type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    /// <summary>
    /// Whether <paramref name="type"/> maps to a JSON number.
    /// </summary>
    /// <remarks>
    /// 🚨 Exact-type comparison, so a NULLABLE numeric (<c>int?</c>) is NOT a number here. That
    /// has always been the behaviour — <c>EditorExtensions</c> gives <c>int?</c> a plain field,
    /// not a <c>NumberFieldControl</c> — and changing it would silently re-render existing forms.
    /// Treat it as contract; if nullable numerics should get numeric editors, that is a separate,
    /// deliberate UI change.
    /// </remarks>
    public static bool IsNumber(this Type type) => type.IsInteger() || type.IsFloatingPoint();
}
