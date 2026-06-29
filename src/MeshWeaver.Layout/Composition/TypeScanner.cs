using System.Collections;

namespace MeshWeaver.Layout.Composition;

/// <summary>
/// Recursively walks an object graph and yields every value that is assignable to a given type,
/// avoiding infinite loops by tracking already-visited instances.
/// </summary>
public static class TypeScanner
{
    /// <summary>
    /// Walks <paramref name="instance"/> and all reachable property values (and enumerable elements)
    /// and yields each value that is of type <typeparamref name="T"/>.
    /// Cycles are broken by reference equality; primitives, strings, Type objects, and exceptions are not descended into.
    /// </summary>
    /// <typeparam name="T">The type to scan for.</typeparam>
    /// <param name="instance">The root object to start scanning from; null returns an empty sequence.</param>
    /// <returns>An enumerable of all instances of <typeparamref name="T"/> found in the object graph.</returns>
    public static IEnumerable<T> ScanFor<T>(object? instance)
    {
        return ScanForInner<T>(instance, new HashSet<object>());
    }

    private static IEnumerable<T> ScanForInner<T>(object? instance, HashSet<object> scanned)
    {
        if (instance == null || scanned.Contains(instance))
            yield break;

        scanned.Add(instance);

        if (instance is T t)
            yield return t;

        // TODO V10: what to put here? (2023/06/25, Roland Buergi)
        if (instance.GetType().IsPrimitive || instance is string || instance is Type || instance is Exception)
            yield break;


        if (instance is IEnumerable enumerable)
            foreach (var e in enumerable)
            foreach (var ret in ScanFor<T>(e))
                yield return ret;

        else
        {
            foreach (var property in instance.GetType().GetProperties())
            foreach (var ret in ScanFor<T>(property.GetValue(instance)))
                yield return ret;
        }
    }
}