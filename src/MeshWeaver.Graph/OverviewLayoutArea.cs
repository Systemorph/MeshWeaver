using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Builds an overview layout for MeshNode content with read-only display and click-to-edit.
/// Shows read-only views by default, click switches to edit mode, blur auto-switches back.
/// Markdown properties are handled separately with full width and Done button.
/// Content is accessed via MeshNode.Content with auto-save to persist changes.
/// </summary>
public static class OverviewLayoutArea
{
    /// <summary>
    /// Builds the property overview for a MeshNode, showing read-only views with click-to-edit.
    /// Uses the unified ContentViewOptions for consistent layout across Overview, Edit, and Create.
    /// </summary>
    public static UiControl BuildPropertyOverview(LayoutAreaHost host, MeshNode node, bool canEdit = true)
    {
        var nodePath = node.Namespace ?? host.Hub.Address.ToString();

        // Handle Content which could be null, JsonElement, or already deserialized typed object
        var instance = node.Content;
        if (instance == null)
            return Controls.Stack;

        if (instance is JsonElement je)
            instance = JsonSerializer.Deserialize<object>(je.GetRawText(), host.Hub.JsonSerializerOptions)!;

        var contentType = instance.GetType();

        // Set up local data for editing
        var dataId = EditLayoutArea.GetDataId(nodePath);
        host.UpdateData(dataId, instance);

        // Setup auto-save to persist changes via DataChangeRequest
        if (canEdit)
        {
            SetupAutoSave(host, dataId, instance, node);
        }

        var container = Controls.Stack.WithWidth("100%");

        // Build using unified content view - Overview mode: toggleable=true, no footer actions
        container = container.WithView(EditLayoutArea.BuildContentView(host, new ContentViewOptions
        {
            DataId = dataId,
            ContentType = contentType,
            CanEdit = canEdit,
            IsToggleable = true  // Overview: click-to-edit, blur back to read-only
        }));

        // The markdown body (from index.md / a Markdown node's content) is intentionally
        // NOT rendered here — BuildDetailsContent hoists it to a direct child of the
        // outer stack via BuildMarkdownBody so callers (and tests) can locate it without
        // walking through nested property-overview stacks.

        return container;
    }

    /// <summary>
    /// Builds the markdown body control for a node whose content is a parsed
    /// <see cref="MeshWeaver.Markdown.MarkdownContent"/> (or one carrying
    /// <see cref="MeshNode.PreRenderedHtml"/>). Returns <c>null</c> when the
    /// node has no markdown payload.
    ///
    /// <para>Both <c>Markdown</c> (raw source) and <c>Html</c> (pre-rendered)
    /// are populated. The raw source is what tests / agent tools / @@() inline-
    /// reference resolvers consume; the pre-rendered HTML is what the browser
    /// displays. Keeping them in lockstep is what lets the markdown round-trip
    /// through serialization without losing the @@() references.</para>
    /// </summary>
    public static UiControl? BuildMarkdownBody(LayoutAreaHost host, MeshNode? node)
    {
        if (node is null)
            return null;
        var rawMarkdown = node.Content switch
        {
            MeshWeaver.Markdown.MarkdownContent mc => mc.Content,
            JsonElement je when je.ValueKind == JsonValueKind.Object && je.TryGetProperty("Content", out var c)
                => c.GetString(),
            JsonElement je when je.ValueKind == JsonValueKind.Object && je.TryGetProperty("content", out var c2)
                => c2.GetString(),
            _ => null
        };
        var hasHtml = !string.IsNullOrWhiteSpace(node.PreRenderedHtml);
        var hasRaw = !string.IsNullOrWhiteSpace(rawMarkdown);
        if (!hasHtml && !hasRaw)
            return null;
        return new MarkdownControl(rawMarkdown ?? "") { Html = node.PreRenderedHtml }
            .WithStyle("padding: 0 0 48px 0;");
    }

    /// <summary>
    /// Builds a clickable title that switches to edit mode on click.
    /// </summary>
    public static UiControl BuildTitle(LayoutAreaHost host, MeshNode node, string dataId, bool canEdit)
    {
        var editStateId = $"editState_{dataId}_title";
        var editStateStream = host.Stream.GetDataStream<bool>(editStateId);

        return Controls.Stack
            .WithView((h, ctx) =>
                editStateStream
                    .StartWith(false)
                    .DistinctUntilChanged()
                    .Select(isEditing =>
                        isEditing && canEdit
                            ? BuildTitleEditView(h, dataId, editStateId)
                            : BuildTitleReadView(h, node, dataId, editStateId, canEdit)));
    }

    private static UiControl BuildTitleReadView(
        LayoutAreaHost _,
        MeshNode node,
        string _1,
        string editStateId,
        bool canEdit)
    {
        var title = node.Name ?? node.Id ?? "";

        var titleStack = Controls.Stack
            .WithStyle($"cursor: {(canEdit ? "pointer" : "default")};")
            .WithView(Controls.Html($"<h1 style=\"margin: 0;\">{System.Web.HttpUtility.HtmlEncode(title)}</h1>"));

        if (canEdit)
        {
            titleStack = titleStack.WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(editStateId, true);
                return Task.CompletedTask;
            });
        }

        return titleStack;
    }

    private static UiControl BuildTitleEditView(
        LayoutAreaHost _,
        string dataId,
        string editStateId)
    {
        var titleField = new TextFieldControl(new JsonPointerReference("title"))
        {
            Immediate = true,
            AutoFocus = true,
            DataContext = LayoutAreaReference.GetDataPointer(dataId)
        }
        .WithStyle("font-size: 2rem; font-weight: bold; border: none; background: transparent; min-width: 300px;")
        .WithBlurAction(ctx =>
        {
            ctx.Host.UpdateData(editStateId, false);
            return Task.CompletedTask;
        });

        return titleField;
    }

    /// <summary>
    /// Sets up auto-save: watches local data stream for changes and persists via DataChangeRequest.
    /// Follows the exact pattern from InlineEditingTest.cs but for MeshNode content.
    /// </summary>
    internal static void SetupAutoSave(
        LayoutAreaHost host,
        string dataId,
        object instance,
        MeshNode node)
    {
        var current = instance;

        host.RegisterForDisposal($"autosave_{dataId}",
            host.Stream.GetDataStream<object>(dataId)
                .Debounce(TimeSpan.FromMilliseconds(300))
                .Subscribe(updatedContent =>
                {

                    if (object.Equals(current, updatedContent))
                        return;

                    // Update current to prevent re-sending
                    current = updatedContent;

                    // Create updated MeshNode with new content
                    var updatedNode = node with { Content = updatedContent };

                    // Issue DataChangeRequest to persist the change
                    host.Hub.Post(
                        new DataChangeRequest { ChangedBy = host.Stream.ClientId }.WithUpdates(updatedNode),
                        o => o.WithTarget(host.Hub.Address));
                }));
    }

}
