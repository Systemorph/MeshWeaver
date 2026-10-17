using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Shared dialog for picking or uploading an icon for a <see cref="MeshNode"/>.
/// Factored out of the Settings &gt; Display icon picker so it can be opened from
/// the Overview header by clicking the icon tile, matching the UX used on
/// Organization NodeType nodes.
/// <para>
/// On "Use as Icon" the dialog writes via <c>stream.Update</c> (the mesh-node
/// stream cache) to set
/// <see cref="MeshNode.Icon"/> to <c>content:&lt;fileName&gt;</c>. The resolver
/// rewrites that to <c>/api/content/{nodePath}/{fileName}</c> at
/// render time, so the file the user just uploaded via the embedded
/// <see cref="FileBrowserControl"/> is immediately reachable.
/// </para>
/// </summary>
public static class NodeIconPickerDialog
{
    /// <summary>
    /// Builds a dialog control with the icon picker for <paramref name="node"/>.
    /// Caller supplies a <see cref="LayoutAreaHost"/> so the dialog can register
    /// data streams and a subscription for dismissing itself on success.
    /// </summary>
    public static UiControl Build(LayoutAreaHost host, MeshNode node)
    {
        var nodePath = node.Path;
        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var collections = contentService?.GetAllCollectionConfigs()?.ToList() ?? [];
        var editableCollection = collections.FirstOrDefault(c => c.IsEditable);

        var formDataId = $"iconPickerForm_{nodePath.Replace('/', '_')}";
        host.UpdateData(formDataId, new Dictionary<string, object?>
        {
            ["icon"] = node.Icon ?? "",
            ["fileName"] = ""
        });

        var formPointer = LayoutAreaReference.GetDataPointer(formDataId);
        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap: 16px; padding: 8px;");

        // Live preview of whatever is currently in the Icon field.
        var noIcon = host.Localize(NoIconKey);
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<Dictionary<string, object?>>(formDataId)
            .Select(data =>
            {
                var raw = data?.GetValueOrDefault("icon")?.ToString() ?? "";
                var resolved = MeshNodeImageHelper.ResolveContentPath(raw, nodePath) ?? "";
                return (UiControl)BuildPreviewTile(resolved, raw, noIcon);
            }));

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("icon"))
        {
            Label = "Icon reference",
            Placeholder = "content:logo.png, <svg>…</svg>, or absolute URL",
            Immediate = true,
            DataContext = formPointer
        });

        if (editableCollection != null)
        {
            stack = stack.WithView(Controls.Body(
                    $"Upload or select a file in the '{editableCollection.DisplayName ?? editableCollection.Name}' collection, then type its filename below and click 'Use as Icon'.")
                .WithStyle("color: var(--neutral-foreground-hint); font-size: 0.85rem;"));

            stack = stack.WithView(new FileBrowserControl(editableCollection.Name)
                .WithCollectionConfiguration(editableCollection)
                .WithCollectionInfo(editableCollection.SourceType, editableCollection.BasePath, editableCollection.Settings)
                .CreatePath());

            stack = stack.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("gap: 8px; align-items: flex-end;")
                .WithView(new TextFieldControl(new JsonPointerReference("fileName"))
                {
                    Label = "Filename in collection",
                    Placeholder = "logo.png",
                    Immediate = true,
                    DataContext = formPointer
                }.WithStyle("flex: 1;"))
                .WithView(Controls.Button(host.Localize("ui.useAsIcon"))
                    .WithAppearance(Appearance.Neutral)
                    .WithReactiveClickAction(ctx => ApplyFileAsIcon(ctx, formDataId))));
        }

        // Bottom button row — Save writes via stream.Update, Cancel just dismisses the dialog.
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; justify-content: flex-end; margin-top: 8px;")
            .WithView(Controls.Button(host.Localize("common.cancel"))
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateArea(DialogControl.DialogArea, null);
                    return Task.CompletedTask;
                }))
            .WithView(Controls.Button(host.Localize("common.save"))
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.Save())
                .WithReactiveClickAction(ctx => SaveIcon(ctx, formDataId, node))));

        return Controls.Dialog(stack, $"Icon — {node.Name ?? node.Id}")
            .WithSize("M")
            .WithClosable(true);
    }

    /// <summary>The 72px tile the picker previews an icon in.</summary>
    internal const string PreviewTileStyle =
        "width: 72px; height: 72px; display: flex; align-items: center; justify-content: center; border-radius: 10px; background: var(--neutral-layer-2);";

    /// <summary>Text key of the empty tile's "No icon".</summary>
    internal const string NoIconKey = "node.icon.none";

    /// <summary>
    /// The picker's preview of the icon being chosen: one <see cref="ImageControl"/> in a 72px tile —
    /// the control that draws the icon everywhere else, so the picker shows exactly what the portal
    /// will draw (an inline svg plated, as an inert image; a raster picture cropped to fill; a glyph).
    /// A dashed tile with "No icon" while the field is empty.
    /// </summary>
    /// <param name="resolved">The icon with a <c>content:</c> reference already resolved to its URL.</param>
    /// <param name="raw">The value as typed.</param>
    /// <param name="noIcon">The localized "No icon" line.</param>
    internal static UiControl BuildPreviewTile(string resolved, string raw, string noIcon = "")
    {
        if (string.IsNullOrEmpty(raw))
            return Controls.Stack
                .WithStyle(PreviewTileStyle + " border: 2px dashed var(--neutral-stroke-rest); background: transparent;")
                .WithView(Controls.Label(noIcon).WithColor("var(--neutral-foreground-hint)"));

        var source = string.IsNullOrEmpty(resolved) ? raw : resolved;
        var isRaster = (source.StartsWith("data:") || source.StartsWith("http") || source.StartsWith("/"))
                       && !source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
        return Controls.Stack.WithStyle(PreviewTileStyle)
            .WithView(Controls.Image(source, "")
                .WithSize(64)
                .WithShape(ImageShape.Rounded)
                .WithFit(isRaster ? ImageFit.Cover : ImageFit.Contain));
    }

    private static IObservable<System.Reactive.Unit> ApplyFileAsIcon(UiActionContext ctx, string formDataId)
    {
        return ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formDataId)
            .Take(1)
            .Do(data =>
            {
                var fileName = data?.GetValueOrDefault("fileName")?.ToString()?.Trim()?.TrimStart('/') ?? "";
                if (string.IsNullOrEmpty(fileName)) return;
                var next = new Dictionary<string, object?>(data ?? new Dictionary<string, object?>())
                {
                    ["icon"] = $"content:{fileName}"
                };
                ctx.Host.UpdateData(formDataId, next);
            }).Select(_ => System.Reactive.Unit.Default);
    }

    private static IObservable<System.Reactive.Unit> SaveIcon(UiActionContext ctx, string formDataId, MeshNode node)
    {
        return ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formDataId)
            .Take(1)
            .Do(data =>
            {
                var newIcon = data?.GetValueOrDefault("icon")?.ToString() ?? "";
                if (node.Path is { Length: > 0 } iconPath)
                {
                    var cache = ctx.Host.Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
                    cache.Update(iconPath, n =>
                        n with { Icon = string.IsNullOrWhiteSpace(newIcon) ? null : newIcon },
                        ctx.Host.Hub.JsonSerializerOptions)
                        .Subscribe(
                            _ => { },
                            ex => ctx.Host.Hub.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                                ?.CreateLogger(typeof(NodeIconPickerDialog).FullName!)
                                .LogWarning(ex, "Icon save failed for {Path}", iconPath));
                }
                ctx.Host.UpdateArea(DialogControl.DialogArea, null);
            }).Select(_ => System.Reactive.Unit.Default);
    }
}
