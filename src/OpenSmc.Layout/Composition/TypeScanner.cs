using System.Collections;

namespace OpenSmc.Layout.Composition;

public static class TypeScanner
{
    public static IEnumerable<T> ScanFor<T>(object instance)
    {
        return ScanForInner<T>(instance, new HashSet<object>());
    }

    private static IEnumerable<T> ScanForInner<T>(object instance, HashSet<object> scanned)
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