using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshWeaver.Layout;

namespace MeshWeaver.Hosting.Monolith.TestBase;

/// <summary>
/// Assertions for "templates first, data later" (Doc/GUI/DataBinding): a layout area's control
/// tree is a TEMPLATE when its first emission waits on nothing. The same check as
/// <c>MarkdownEditIsATemplateTest.EveryViewIsStatic</c> (the reference conversion), shared so
/// every converted area pins it the same way.
/// </summary>
public static class LayoutTemplateAssertions
{
    /// <summary>
    /// Fails when any container in <paramref name="root"/> carries a DEFERRED view —
    /// <c>WithView((h, c) =&gt; observable)</c>, the shape that renders only once data has arrived.
    /// A container records such a view as the delegate or observable that will produce it, so any
    /// non-control entry is a slot the first render leaves empty.
    /// </summary>
    /// <param name="root">The template to check.</param>
    public static void EveryViewIsStatic(UiControl root)
    {
        foreach (var control in Descendants(root))
        {
            if (Member(control.GetType(), "Views")?.GetValue(control) is not IEnumerable views)
                continue;
            var deferred = views.Cast<object?>().Where(v => v is not null and not UiControl)
                .Select(v => v!.GetType().Name).ToList();
            if (deferred.Count > 0)
                throw new InvalidOperationException(
                $"{control.GetType().Name} carries {deferred.Count} deferred view(s) "
                + $"({string.Join(", ", deferred)}) — a view that renders only once data has arrived. "
                + "A template binds its data instead.");
        }
    }

    /// <summary>The control and every control nested in its containers, depth first.</summary>
    /// <param name="root">The template to walk.</param>
    /// <returns>The controls.</returns>
    public static IEnumerable<UiControl> Descendants(UiControl root)
    {
        yield return root;
        if (Member(root.GetType(), "Views")?.GetValue(root) is IEnumerable views)
            foreach (var v in views)
                if (v is UiControl child)
                    foreach (var d in Descendants(child))
                        yield return d;
    }

    private static PropertyInfo? Member(Type? t, string name)
    {
        for (; t is not null; t = t.BaseType)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (p is not null)
                return p;
        }
        return null;
    }
}
