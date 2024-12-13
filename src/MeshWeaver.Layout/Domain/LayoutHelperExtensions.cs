using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.Domain;

public static class LayoutHelperExtensions
{
    public static EntityStoreAndUpdates ConfigBasedRenderer<TControl>(this LayoutAreaHost host,
        RenderingContext context,
        EntityStore store,
        string area,
        Func<TControl> factory,
        Func<TControl, LayoutAreaHost, RenderingContext, TControl> config)
        where TControl : UiControl
    {
        var menu = store.GetLayoutArea<TControl>(area) ?? factory();
        menu = config(menu, host, context);
        return host.RenderArea(
            context with { Area = area }, menu, store)
;
    }

    internal static bool DataEquality(object data, object otherData)
    {
        if (data is null)
            return otherData is null;

        if (data is IEnumerable<object> e)
            return otherData is IEnumerable<object> e2 && e.SequenceEqual(e2, JsonObjectEqualityComparer.Instance);
        return JsonObjectEqualityComparer.Instance.Equals(data, otherData);
    }

    public static int DataHashCode(object data)
    {
        if (data is null)
            return 0;
        if (data is IEnumerable<object> e)
            return e.Aggregate(0, (i, y) => i ^ y.GetHashCode());
        return data.GetHashCode();
    }
}
