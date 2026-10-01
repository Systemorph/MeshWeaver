using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// The views a container was DECLARED with — a static control, or the delegate / observable that
/// renders one later. Internal: the public surface is <see cref="LayoutTemplate"/>.
/// </summary>
internal interface IDeclaresViews
{
    /// <summary>The views in declaration order, exactly as the container holds them.</summary>
    IReadOnlyList<object?> DeclaredViews { get; }
}

/// <summary>
/// Reads a control tree as a TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): a
/// layout area emits its whole tree on the first render and BINDS its data, so every view in every
/// container is a control. A deferred view — <c>WithView((h, c) =&gt; observable)</c>, the shape
/// that renders only once data has arrived — is held by its container as the delegate or
/// observable that will produce it, and is a slot the first render leaves empty.
///
/// <para>Public so a NodeType's own <c>Test/</c> cases can assert it on the mesh: in-mesh C# has no
/// test framework and cannot see a container's protected view list.</para>
/// </summary>
public static class LayoutTemplate
{
    /// <summary>
    /// <paramref name="root"/> and every control below it, depth first, following each container's
    /// STATIC views only (a deferred view has no control yet).
    /// </summary>
    /// <param name="root">The top of the tree.</param>
    /// <returns>The controls of the tree.</returns>
    public static IEnumerable<UiControl> Descendants(UiControl root)
    {
        yield return root;
        if (root is not IDeclaresViews container)
            yield break;
        foreach (var view in container.DeclaredViews)
            if (view is UiControl child)
                foreach (var descendant in Descendants(child))
                    yield return descendant;
    }

    /// <summary>
    /// Every deferred view in the tree, named as <c>&lt;container type&gt;[&lt;index&gt;]: &lt;view type&gt;</c>.
    /// Empty means the tree is a template: its first emission is the whole page and waits on nothing.
    /// </summary>
    /// <param name="root">The top of the tree.</param>
    /// <returns>The deferred views, in tree order.</returns>
    public static ImmutableList<string> DeferredViews(UiControl root) =>
        Descendants(root)
            .OfType<IDeclaresViews>()
            .SelectMany(container => container.DeclaredViews
                .Select((view, index) => (Container: container, View: view, Index: index)))
            .Where(x => x.View is not null and not UiControl)
            .Select(x => $"{x.Container.GetType().Name}[{x.Index}]: {x.View!.GetType().Name}")
            .ToImmutableList();
}
