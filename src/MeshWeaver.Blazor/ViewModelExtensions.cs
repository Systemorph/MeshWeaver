#nullable enable
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Application.Styles;
using Microsoft.FluentUI.AspNetCore.Components;
using Icon = MeshWeaver.Domain.Icon;
using MeshWeaver.Data;

namespace MeshWeaver.Blazor;

/// <summary>Extension methods for converting MeshWeaver view-model types to their Fluent UI equivalents.</summary>
public static class ViewModelExtensions
{
    /// <summary>Converts a MeshWeaver <c>Icon</c> to its Fluent UI <c>Icon</c> equivalent, or null when the provider is unsupported.</summary>
    /// <param name="icon">The domain icon to convert.</param>
    /// <returns>A Fluent UI icon instance, or null when the icon provider is not recognized.</returns>
    public static Microsoft.FluentUI.AspNetCore.Components.Icon? ToFluentIcon(this Icon icon) =>
        icon.Provider switch
        {
            FluentIcons.Provider => CreateFluentIcon(icon),
            CustomIcons.Provider => CreateCustomIcon(icon),
            _ => null
        };

    private static Microsoft.FluentUI.AspNetCore.Components.Icon? CreateFluentIcon(Icon icon)
    {
        try
        {
            return new IconInfo { Name = icon.Id, Size = (IconSize)icon.Size, Variant = (IconVariant)icon.Variant }
                .GetInstance();
        }
        catch (ArgumentException)
        {
            // A PascalCase word that is not an actual Fluent icon name (Icon.Parse classifies any
            // such free-text node icon as FluentProvider) must degrade to "no icon" — GetInstance's
            // only signal for an unknown name is this throw, and a per-render throw storms the log.
            return null;
        }
    }

    private static Microsoft.FluentUI.AspNetCore.Components.Icon? CreateCustomIcon(Icon icon)
    {
        var svgContent = CustomIcons.GetSvgContent(icon.Id);
        if (string.IsNullOrEmpty(svgContent))
            return null;

        // Extract the path/content from SVG for FluentUI Icon
        // FluentUI Icon expects SVG inner content (path, rect, etc.)
        return new Microsoft.FluentUI.AspNetCore.Components.Icon(
            icon.Id,
            IconVariant.Regular,
            IconSize.Size20,
            svgContent);
    }


    internal static UiControl? GetControl(this ISynchronizationStream<JsonElement> stream, ChangeItem<JsonElement> item, string area)
    {
        return item.Value.TryGetProperty(LayoutAreaReference.Areas, out var controls)
               && controls.TryGetProperty(JsonSerializer.Serialize(area), out var node)
            ? node.Deserialize<UiControl>(stream.Hub.JsonSerializerOptions)
            : null;
    }


    internal static string GetArea(this NamedAreaControl control)
        => control.Area?.ToString() ?? string.Empty;

    /// <summary>
    /// A container child resolved for rendering: the child's <see cref="NamedAreaControl"/>, the
    /// absolute area path it renders, and the renderer key that gives it a stable IDENTITY.
    /// </summary>
    /// <param name="Area">The child's named-area control.</param>
    /// <param name="ResolvedArea">The child's absolute area path.</param>
    /// <param name="Key">The renderer key — the area path, disambiguated when the container
    /// declares the same area id twice.</param>
    public sealed record ContainerChild(NamedAreaControl Area, string ResolvedArea, string Key);

    /// <summary>
    /// Resolves a container's children to their absolute area paths together with the renderer key
    /// that identifies each one.
    /// <para>🚨 The key is the child's AREA NAME, never its position. A renderer that diffs container
    /// children positionally retains the component instance at each index and hands it a DIFFERENT
    /// logical child when the child SET changes (an inserted Back button, a replaced named child).
    /// That retained subtree owns a live control-stream subscription and JS-managed DOM, so mutating
    /// it in place produced the duplicated/stale children of issue #732. Keyed by area name an
    /// insert/remove/reorder unmounts and mounts by identity, and an unchanged child keeps its
    /// instance even when its index moved.</para>
    /// <para>A duplicate area id inside one container is a layout-authoring bug — the server's store
    /// keys collide too, so the last writer already wins there. It must not become a duplicate key,
    /// though: the Blazor renderer THROWS on sibling key collisions, which would blank the whole page
    /// instead of rendering the (degenerate) container. Repeats are therefore disambiguated
    /// deterministically, so every key stays stable across renders.</para>
    /// </summary>
    /// <param name="container">The container whose children are resolved.</param>
    /// <param name="parentArea">The container's own area path, used when a child carries no absolute area.</param>
    /// <returns>One <see cref="ContainerChild"/> per child, in declaration order.</returns>
    public static IEnumerable<ContainerChild> ResolveChildren(this IContainerControl container, string? parentArea)
    {
        var occurrences = new Dictionary<string, int>();
        foreach (var area in container.Areas)
        {
            var resolved = string.IsNullOrEmpty(area.GetArea()) ? $"{parentArea}/{area.Id}" : area.GetArea();
            var occurrence = occurrences.TryGetValue(resolved, out var seen) ? seen + 1 : 0;
            occurrences[resolved] = occurrence;
            yield return new ContainerChild(area, resolved, occurrence == 0 ? resolved : $"{resolved}#{occurrence}");
        }
    }
}
