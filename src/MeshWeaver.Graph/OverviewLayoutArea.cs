using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        // Handle Content which could be null, JsonElement, or already deserialized typed object
        var instance = node.Content;
        if (instance == null)
            return Controls.Stack;

        if (instance is JsonElement je)
            instance = JsonSerializer.Deserialize<object>(je.GetRawText(), host.Hub.JsonSerializerOptions)!;

        var contentType = instance.GetType();

        // The property form is bound DIRECTLY to the node's Content (node-bound DataContext): every
        // field reads from and writes straight back to the node stream (IMeshNodeStreamCache). ONE
        // source of truth — no /data replica of the node content, no SetupAutoSave save subscription.
        // See Doc/GUI/DataBinding "edit node content by binding to the node stream".
        var dataId = EditLayoutArea.GetDataId(node.Path);
        var boundContext = LayoutAreaReference.GetMeshNodeDataContext(node.Path, bindContent: true);

        // A few read-only display controls (dimension / options / formatted-date labels) derive their
        // text from the LAYOUT-AREA /data stream rather than a value pointer, so they can't read the
        // node directly from the Layout layer. Keep /data/{dataId} as a ONE-WAY live projection of the
        // node's Content (node → /data, NEVER /data → node) so those labels stay correct. This is a
        // pure read mirror — there is no save loop and no drift: it follows the node, and all WRITES
        // still go straight to the node via the node-bound DataContext above.
        host.RegisterForDisposal($"overview-content-projection_{dataId}",
            host.Workspace.GetMeshNodeStream(node.Path)
                .Select(n => n?.Content)
                .Where(c => c is not null)
                .Subscribe(content => host.UpdateData(dataId, content!)));

        var container = Controls.Stack.WithWidth("100%");

        // Build using unified content view - Overview mode: toggleable=true, no footer actions
        container = container.WithView(EditLayoutArea.BuildContentView(host, new ContentViewOptions
        {
            DataId = dataId,
            ContentType = contentType,
            CanEdit = canEdit,
            IsToggleable = true,  // Overview: click-to-edit, blur back to read-only
            BoundDataContext = canEdit ? boundContext : null
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
        // The page header already renders node.Name as the H1. If the body ALSO opens with a top-level
        // heading that repeats the name (a common authoring habit — e.g. a course/markdown page starting
        // `# Agentic Engineering`), it shows the title TWICE. Strip that leading duplicate from both the
        // raw markdown and the pre-rendered HTML — only when it MATCHES the name, so a different first
        // heading is left untouched.
        var name = node.Name;
        var html = node.PreRenderedHtml;
        if (!string.IsNullOrEmpty(name))
        {
            rawMarkdown = StripLeadingTitleMarkdown(rawMarkdown, name);
            html = StripLeadingTitleHtml(html, name);
        }

        var hasHtml = !string.IsNullOrWhiteSpace(html);
        var hasRaw = !string.IsNullOrWhiteSpace(rawMarkdown);
        if (!hasHtml && !hasRaw)
            return null;
        return new MarkdownControl(rawMarkdown ?? "") { Html = html }
            .WithStyle("padding: 0 0 48px 0;");
    }

    /// <summary>Removes a leading <c># {name}</c> ATX heading from markdown when it duplicates the node
    /// name (the header already shows it). Only a MATCHING leading H1 is stripped.</summary>
    internal static string? StripLeadingTitleMarkdown(string? markdown, string name)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;
        var lines = markdown.Split('\n');
        var i = 0;
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
            i++;
        if (i < lines.Length && lines[i].TrimStart().StartsWith("# ", StringComparison.Ordinal))
        {
            var heading = lines[i].Trim()[2..].Trim();
            if (string.Equals(heading, name, StringComparison.OrdinalIgnoreCase))
                return string.Join('\n', lines.Skip(i + 1)).TrimStart('\n');
        }
        return markdown;
    }

    /// <summary>Removes a leading <c>&lt;h1&gt;{name}&lt;/h1&gt;</c> from pre-rendered HTML when it
    /// duplicates the node name.</summary>
    internal static string? StripLeadingTitleHtml(string? html, string name)
    {
        if (string.IsNullOrEmpty(html))
            return html;
        var trimmed = html.TrimStart();
        var m = System.Text.RegularExpressions.Regex.Match(trimmed, "^<h1\\b[^>]*>(?<t>.*?)</h1>\\s*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (m.Success)
        {
            var text = System.Web.HttpUtility.HtmlDecode(m.Groups["t"].Value).Trim();
            if (string.Equals(text, name, StringComparison.OrdinalIgnoreCase))
                return trimmed[m.Length..];
        }
        return html;
    }

    /// <summary>
    /// Builds a clickable title that switches to edit mode on click. The title edit is bound
    /// DIRECTLY to the node's Content <c>title</c> field (node-bound DataContext) — the edit writes
    /// straight back to the node stream; only the click-to-edit toggle lives in <c>/data</c>.
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
                            ? BuildTitleEditView(h, node.Path, editStateId)
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
        var titleHtml = $"<h1 style=\"margin: 0;\">{System.Web.HttpUtility.HtmlEncode(title)}</h1>";

        // The page title shows the node's OWN icon (MeshNode.Icon) beside its name — so a Space, a doc,
        // any node with an icon carries it on the page. No icon → just the title.
        var iconHtml = RenderNodeIconHtml(node.Icon);
        var heading = iconHtml.Length == 0
            ? titleHtml
            : $"<div style=\"display: flex; align-items: center; gap: 12px;\">{iconHtml}{titleHtml}</div>";

        var titleStack = Controls.Stack
            .WithStyle($"cursor: {(canEdit ? "pointer" : "default")};")
            .WithView(Controls.Html(heading));

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

    /// <summary>
    /// Renders a node's <see cref="MeshNode.Icon"/> as a title-sized (32px) icon: an inline
    /// <c>&lt;svg&gt;</c> is embedded, an image URL (or <c>data:</c>/path) becomes an <c>&lt;img&gt;</c>,
    /// and anything else (an emoji / short glyph) is shown as text. Empty when there is no icon.
    /// </summary>
    private static string RenderNodeIconHtml(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
            return "";
        var trimmed = icon.Trim();
        if (trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return $"<span style=\"display:inline-flex; width:32px; height:32px;\">{trimmed}</span>";
        var looksLikeUrl = trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("/") || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains('.');
        return looksLikeUrl
            ? $"<img src=\"{System.Web.HttpUtility.HtmlAttributeEncode(trimmed)}\" alt=\"\" style=\"width:32px; height:32px; object-fit:contain;\" />"
            : $"<span style=\"font-size:28px; line-height:1;\">{System.Web.HttpUtility.HtmlEncode(trimmed)}</span>";
    }

    private static UiControl BuildTitleEditView(
        LayoutAreaHost _,
        string nodePath,
        string editStateId)
    {
        var titleField = new TextFieldControl(new JsonPointerReference("title"))
        {
            Immediate = true,
            AutoFocus = true,
            DataContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: true)
        }
        .WithStyle("font-size: 2rem; font-weight: bold; border: none; background: transparent; min-width: 300px;")
        .WithBlurAction(ctx =>
        {
            ctx.Host.UpdateData(editStateId, false);
            return Task.CompletedTask;
        });

        return titleField;
    }

}
