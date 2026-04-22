using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Shared dialog for picking or uploading an icon for a <see cref="MeshNode"/>.
/// Factored out of the Settings &gt; Display icon picker so it can be opened from
/// the Overview header by clicking the icon tile, matching the UX used on
/// Organization NodeType nodes.
/// <para>
/// On "Use as Icon" the dialog posts an <see cref="UpdateNodeRequest"/> that sets
/// <see cref="MeshNode.Icon"/> to <c>content:&lt;fileName&gt;</c>. The resolver
/// rewrites that to <c>/static/storage/content/{nodePath}/{fileName}</c> at
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
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<Dictionary<string, object?>>(formDataId)
            .Select(data =>
            {
                var raw = data?.GetValueOrDefault("icon")?.ToString() ?? "";
                var resolved = MeshNodeImageHelper.ResolveContentPath(raw, nodePath) ?? "";
                return (UiControl)BuildPreviewTile(resolved, raw);
            }));

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("icon"))
        {
            Label = "Icon reference",
            Placeholder = "content:logo.png, /static/…, <svg>…</svg>, or absolute URL",
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
                .WithView(Controls.Button("Use as Icon")
                    .WithAppearance(Appearance.Neutral)
                    .WithClickAction(ctx => ApplyFileAsIcon(ctx, formDataId))));
        }

        // Bottom button row — Save posts UpdateNodeRequest, Cancel just dismisses the dialog.
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; justify-content: flex-end; margin-top: 8px;")
            .WithView(Controls.Button("Cancel")
                .WithAppearance(Appearance.Neutral)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateArea(DialogControl.DialogArea, null);
                    return Task.CompletedTask;
                }))
            .WithView(Controls.Button("Save")
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.Save())
                .WithClickAction(ctx => SaveIcon(ctx, formDataId, node))));

        return Controls.Dialog(stack, $"Icon — {node.Name ?? node.Id}")
            .WithSize("M")
            .WithClosable(true);
    }

    private static UiControl BuildPreviewTile(string resolved, string raw)
    {
        const string tile = "width: 72px; height: 72px; display: flex; align-items: center; justify-content: center; border-radius: 10px; background: var(--neutral-layer-2);";
        if (string.IsNullOrEmpty(raw))
            return Controls.Html(
                $"<div style=\"{tile} border: 2px dashed var(--neutral-stroke-rest); background: transparent; color: var(--neutral-foreground-hint);\">No icon</div>");

        if (resolved.StartsWith("data:") || resolved.StartsWith("http") || resolved.StartsWith("/"))
        {
            var fit = resolved.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "contain" : "cover";
            return Controls.Html(
                $"<div style=\"{tile}\"><img src=\"{resolved}\" alt=\"\" style=\"width: 64px; height: 64px; border-radius: 8px; object-fit: {fit};\" /></div>");
        }

        if (raw.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return Controls.Html($"<div style=\"{tile}\">{raw}</div>");

        return Controls.Html($"<div style=\"{tile} font-size: 36px;\">{System.Web.HttpUtility.HtmlEncode(raw)}</div>");
    }

    private static Task ApplyFileAsIcon(UiActionContext ctx, string formDataId)
    {
        ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formDataId)
            .Take(1)
            .Subscribe(data =>
            {
                var fileName = data?.GetValueOrDefault("fileName")?.ToString()?.Trim()?.TrimStart('/') ?? "";
                if (string.IsNullOrEmpty(fileName)) return;
                var next = new Dictionary<string, object?>(data ?? new Dictionary<string, object?>())
                {
                    ["icon"] = $"content:{fileName}"
                };
                ctx.Host.UpdateData(formDataId, next);
            });
        return Task.CompletedTask;
    }

    private static Task SaveIcon(UiActionContext ctx, string formDataId, MeshNode node)
    {
        ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formDataId)
            .Take(1)
            .Subscribe(data =>
            {
                var newIcon = data?.GetValueOrDefault("icon")?.ToString() ?? "";
                var updatedNode = node with { Icon = string.IsNullOrWhiteSpace(newIcon) ? null : newIcon };
                ctx.Host.Hub.Post(new UpdateNodeRequest(updatedNode));
                ctx.Host.UpdateArea(DialogControl.DialogArea, null);
            });
        return Task.CompletedTask;
    }
}
