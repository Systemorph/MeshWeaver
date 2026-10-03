using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
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
        {
            // A JSON `null`/absent element carries no properties to render. Treat it exactly like a
            // null Content — it is the SAME state, spelled in JSON — rather than announcing an
            // unresolvable type for content that has none.
            if (je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return Controls.Stack;

            // 🚨 Ask the MESH-WIDE registry FIRST, keyed on the node's OWN NodeType — the EXACT
            // route, and the one this seam is uniquely placed to take because it holds the node.
            // JsonSerializer.Deserialize<object> alone is NOT enough: ObjectPolymorphicConverter
            // asks this hub's frozen ITypeRegistry and then only the NAME route
            // (IMeshContentTypeRegistry.TryRecover), which must REFUSE a discriminator two packages
            // both claim (#1299). TryRecoverForNodeType answers the question that always has one
            // right answer, and falls back to the name route itself — so this is strictly more
            // recovery, never less.
            var contentTypeRegistry = host.Hub.ServiceProvider.GetService<IMeshContentTypeRegistry>();
            instance = contentTypeRegistry?.TryRecoverForNodeType(node.NodeType, je, host.Hub.JsonSerializerOptions)
                       ?? JsonSerializer.Deserialize<object>(je.GetRawText(), host.Hub.JsonSerializerOptions);

            // 🚨 Still JSON ⇒ the discriminator resolves NOWHERE in this process, and the old code
            // silently fell through to `typeof(JsonElement)` and built the property form over
            // JsonElement's OWN members (ValueKind, …) — a form the viewer cannot tell apart from
            // the real one, with no exception and nothing to grep (#3558). Say what happened
            // instead: that is the whole difference between a wrong page and a diagnosable one.
            if (instance is null or JsonElement)
                return UnresolvableContentType(host, node, je);
        }

        return PropertyOverview(host, node.Path, instance.GetType(), canEdit);
    }

    /// <summary>
    /// The property overview as a TEMPLATE for the hub's own node: the form of
    /// <paramref name="contentType"/> — STRUCTURE, which the caller reads from the hub's
    /// configuration — with every field bound to the node. Nothing here waits on the node.
    /// </summary>
    /// <param name="host">The node hub's layout host.</param>
    /// <param name="contentType">The content type the hub is configured with.</param>
    /// <param name="canEdit">Whether the fields are click-to-edit.</param>
    public static UiControl BuildPropertyOverviewTemplate(LayoutAreaHost host, Type contentType, bool canEdit)
        => PropertyOverview(host, host.Hub.Address.ToString(), contentType, canEdit);

    private static UiControl PropertyOverview(LayoutAreaHost host, string nodePath, Type contentType, bool canEdit)
    {
        // The property form is bound DIRECTLY to the node's Content (node-bound DataContext): every
        // field reads from and writes straight back to the node stream (IMeshNodeStreamCache). ONE
        // source of truth — no /data replica of the node content, no SetupAutoSave save subscription.
        // See Doc/GUI/DataBinding "edit node content by binding to the node stream".
        var dataId = EditLayoutArea.GetDataId(nodePath);
        var boundContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: true);

        // Build using unified content view - Overview mode: toggleable=true, no footer actions.
        // A few read-only display controls (dimension / options / formatted-date labels) derive their
        // text from the LAYOUT-AREA /data stream rather than a value pointer, so /data/{dataId} is
        // kept a ONE-WAY live mirror of the node's Content for as long as this control's area lives
        // (NodePageProjections.MirrorContent — registered in the buildup, disposed with the area,
        // so it opens once per rendered area and never accumulates per render, #606). All WRITES
        // still go straight to the node via the node-bound DataContext.
        //
        // The markdown body (from index.md / a Markdown node's content) is intentionally
        // NOT rendered here — the node page hoists it to a direct child of the outer stack
        // (BuildMarkdownBody / BuildMarkdownBodyTemplate) so callers (and tests) can locate it
        // without walking through nested property-overview stacks.
        return Controls.Stack.WithWidth("100%")
            .WithView(EditLayoutArea.BuildContentView(host, new ContentViewOptions
            {
                DataId = dataId,
                ContentType = contentType,
                CanEdit = canEdit,
                IsToggleable = true,  // Overview: click-to-edit, blur back to read-only
                BoundDataContext = canEdit ? boundContext : null
            }))
            .MirrorContent(nodePath, dataId);
    }

    /// <summary>
    /// The property form's fallback slot: the area a node page renders its form in when the hub's
    /// configuration names no content type (<see cref="MeshNodeLayoutAreas.ConfiguredContentType(LayoutAreaHost)"/>
    /// is null). Its id is the mode — <see cref="ContentFormOverview"/> or <see cref="ContentFormEdit"/>.
    /// </summary>
    public const string ContentFormArea = "NodeContentForm";

    /// <summary><see cref="ContentFormArea"/> mode: the node page's click-to-edit overview.</summary>
    public const string ContentFormOverview = "overview";

    /// <summary><see cref="ContentFormArea"/> mode: the Edit page's form in pure edit mode.</summary>
    public const string ContentFormEdit = "edit";

    /// <summary>
    /// The <see cref="ContentFormArea"/> slot as a nested area with the skeleton loading shape —
    /// the page around it renders at once; only the slot waits.
    /// </summary>
    internal static UiControl ContentFormSlot(LayoutAreaHost host, string mode)
        => Controls.LayoutArea(host.Hub.Address, ContentFormArea, mode)
            .WithSpinnerType(SpinnerType.Skeleton);

    /// <summary>
    /// Renders the <see cref="ContentFormArea"/>: the property form of a hub whose configuration
    /// names no content type, so the form's SHAPE can only come from the node's own content
    /// (its <c>$type</c>). This is a STRUCTURE read and the one place the default node page still
    /// reads the node on the hub; the fields themselves stay bound to the node. Re-renders only when
    /// the shape can change — the content type, or the viewer's read or edit right.
    ///
    /// <para>🚨 READ-GATED in the view, like <see cref="MeshNodeLayoutAreas.Overview"/> and
    /// <see cref="MeshNodeLayoutAreas.ContentData"/>: this is a registered top-level area, so it is
    /// addressable by reference on its own and must not lean on the page that normally embeds it.
    /// The delivery pipeline already refuses a <c>SubscribeRequest</c> from a viewer without Read
    /// (<c>[RequiresPermission(Permission.Read)]</c>); this gate is the second layer for the
    /// configurations where that check is decided by a hub-level or node-type rule rather than by
    /// the viewer's effective permissions on the node.</para>
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    public static IObservable<UiControl?> ContentForm(LayoutAreaHost host, RenderingContext ctx)
        => ContentForm(host, host.Hub.GetEffectivePermissions(host.Hub.Address.ToString()));

    /// <summary>
    /// <see cref="ContentForm(LayoutAreaHost, RenderingContext)"/> over an explicit permission
    /// stream — the seam the read gate is measured through (<c>ContentFormIsReadGatedTest</c>).
    /// </summary>
    /// <param name="host">The layout area host of the hub's own node.</param>
    /// <param name="permissions">The viewer's effective permissions on that node.</param>
    /// <returns>The access-denied view without Read; otherwise the form in the requested mode.</returns>
    internal static IObservable<UiControl?> ContentForm(LayoutAreaHost host, IObservable<Permission> permissions)
    {
        var hubPath = host.Hub.Address.ToString();
        var edit = string.Equals(host.Reference.Id?.ToString(), ContentFormEdit, StringComparison.Ordinal);
        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(permissions,
                (node, granted) => (Node: node,
                    CanRead: granted.HasFlag(Permission.Read), CanEdit: granted.HasFlag(Permission.Update)))
            .DistinctUntilChanged(t => (ShapeOf(t.Node), t.CanRead, t.CanEdit))
            .Select(t => !t.CanRead
                ? MeshNodeLayoutAreas.BuildAccessDenied(hubPath, locale: host.ViewerLocale())
                : t.Node is null
                    ? (UiControl?)Controls.Markdown(host.Localize("ui.mdNodeNotFound"))
                    : edit
                        ? (t.CanEdit ? EditForm(host, t.Node) : null)
                        : BuildPropertyOverview(host, t.Node, t.CanEdit));
    }

    /// <summary>What decides the form's shape: the content's CLR type, or its JSON discriminator.</summary>
    private static string ShapeOf(MeshNode? node) => node?.Content switch
    {
        null => "",
        JsonElement { ValueKind: JsonValueKind.Object } je when je.TryGetProperty("$type", out var t)
            => "json:" + t.ToString(),
        JsonElement je => "json-" + je.ValueKind,
        { } content => content.GetType().AssemblyQualifiedName ?? content.GetType().Name,
    };

    /// <summary>The Edit page's form for a node whose content type comes from its content.</summary>
    private static UiControl EditForm(LayoutAreaHost host, MeshNode node)
    {
        var instance = node.Content;
        if (instance is JsonElement je && je.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            instance = host.Hub.ServiceProvider.GetService<IMeshContentTypeRegistry>()
                           ?.TryRecoverForNodeType(node.NodeType, je, host.Hub.JsonSerializerOptions)
                       ?? JsonSerializer.Deserialize<object>(je.GetRawText(), host.Hub.JsonSerializerOptions);
        if (instance is null or JsonElement)
            return Controls.Markdown(host.Localize("ui.mdNoContentType"))
                .WithStyle("color: var(--neutral-foreground-hint);");

        var dataId = EditLayoutArea.GetDataId(node.Path);
        var boundContext = LayoutAreaReference.GetMeshNodeDataContext(node.Path, bindContent: true);
        return EditLayoutArea.BuildPropertyForm(
                host, instance.GetType(), dataId, canEdit: true, isToggleable: false, boundDataContext: boundContext)
            .MirrorContent(node.Path, dataId);
    }

    /// <summary>
    /// The honest state for content whose <c>$type</c> resolves nowhere in this process: name the
    /// discriminator and say which of the two indistinguishable causes the viewer is looking at.
    ///
    /// <para>🚨 The wording must NOT assert corruption. For an in-mesh NodeType — <c>Source/*.cs</c>
    /// compiled by Roslyn at RUNTIME — the same state is printed while the compile is still in
    /// flight, and it ENDS: <c>MeshNodeStreamHandle</c>'s typed read boundary waits on
    /// <see cref="IMeshContentTypeRegistry.Registrations"/> and re-emits the node typed the moment a
    /// registration makes it resolvable, which re-renders this area. Reading the line as "this row is
    /// broken" is what sent the #2952 investigation after the data for weeks, so it names both causes
    /// and says which observation separates them.</para>
    /// </summary>
    private static UiControl UnresolvableContentType(LayoutAreaHost host, MeshNode node, JsonElement content)
    {
        var discriminator = content.ValueKind == JsonValueKind.Object
                            && content.TryGetProperty("$type", out var typeProp)
                            && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString()
            : null;

        // Two genuinely different states, and conflating them misleads — in the rendered notice AND
        // in the log. NO discriminator means the content is free-form JSON: legal by design
        // (ContentDiscriminatorValidator: "content WITHOUT a $type stays legal"), permanent, and
        // nothing to wait for. There is no name that "resolves nowhere", so there is nothing to
        // report — a warning here would accuse a legal shape, once per render, for ever (Copilot
        // review). The notice on screen is the whole story.
        if (string.IsNullOrEmpty(discriminator))
            return Controls.Markdown(
                $"**{host.Localize("overview.untypedContent")}**\n\n"
                + host.Localize("overview.untypedContentHint"));

        // A discriminator WAS named and resolves on neither route. Not a per-emission flood: this
        // branch is reached only on the already-degraded path, and a node whose type registers late
        // leaves it for good on the very next emission.
        host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>()?.LogWarning(
            "OverviewLayoutArea: content discriminator '$type': '{TypeName}' on {Path} (NodeType "
            + "'{NodeType}') resolves on neither the NodeType route nor the name route — the property "
            + "overview renders the unresolved-type notice instead of a form. Either the NodeType's "
            + "runtime compile has not registered it YET (transient — the view re-renders when it "
            + "does), or no declaration will ever claim this discriminator.",
            discriminator, node.Path, node.NodeType ?? "(none)");

        return Controls.Markdown(
            $"⚠️ **{host.Localize("overview.unresolvedType", discriminator)}**\n\n"
            + host.Localize("overview.unresolvedTypeHint"));
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
        => node is not null && MarkdownBodyText(node) is { } body
            ? new MarkdownControl(body.Markdown ?? "") { Html = body.Html, NodePath = node.Path }
                .WithStyle(MarkdownBodyStyle)
            : null;

    private const string MarkdownBodyStyle = "padding: 0 0 48px 0;";

    /// <summary>The <c>/data</c> id the node page's markdown body projection is published under.</summary>
    internal const string BodyDataId = "nodeBody";

    /// <summary>
    /// The node page's markdown body as a TEMPLATE for the hub's own node: a
    /// <see cref="MarkdownControl"/> whose markdown and pre-rendered HTML are BOUND to a projection
    /// of the node (<see cref="BuildMarkdownBody"/>'s text, computed on the hub), hidden while the
    /// node has none. It follows every later edit.
    /// </summary>
    /// <param name="host">The node hub's layout host.</param>
    public static UiControl BuildMarkdownBodyTemplate(LayoutAreaHost host)
    {
        var nodePath = host.Hub.Address.ToString();
        return NodePageProjections.Body(host, MarkdownBodyStyle)
            .Bind(p => BodyTemplate(p.Markdown, p.Html, p.Style, nodePath), BodyDataId);
    }

    /// <summary>The markdown body with its bound values as POINTERS.</summary>
    internal static MarkdownControl BodyTemplate(object markdown, object? html, object style, string nodePath)
        => new(markdown) { Html = html, NodePath = nodePath, Style = style };

    /// <summary>
    /// The text <see cref="BuildMarkdownBody"/> shows — the raw markdown and the pre-rendered HTML,
    /// a leading heading that repeats the node's name stripped from both — or null when the node
    /// carries no markdown body. Pure.
    /// </summary>
    internal static (string? Markdown, string? Html)? MarkdownBodyText(MeshNode? node)
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
        // Preserve the generic view's markdown surface: an unrelated typed Content/Body property
        // alone does not make a data record a document. Once admitted, current source wins over HTML.
        if (rawMarkdown is null && string.IsNullOrWhiteSpace(node.PreRenderedHtml))
            return null;
        // The page header already renders node.Name as the H1. If the body ALSO opens with a top-level
        // heading that repeats the name (a common authoring habit — e.g. a course/markdown page starting
        // `# Agentic Engineering`), it shows the title TWICE. Strip that leading duplicate from both the
        // raw markdown and the pre-rendered HTML — only when it MATCHES the name, so a different first
        // heading is left untouched.
        var name = node.Name;
        var html = MarkdownBody.Render(node);
        if (!string.IsNullOrEmpty(name))
        {
            rawMarkdown = StripLeadingTitleMarkdown(rawMarkdown, name);
            html = StripLeadingTitleHtml(html, name);
        }

        var hasHtml = !string.IsNullOrWhiteSpace(html);
        var hasRaw = !string.IsNullOrWhiteSpace(rawMarkdown);
        if (!hasHtml && !hasRaw)
            return null;
        return (rawMarkdown, html);
    }

    /// <summary>Removes a leading <c># {name}</c> ATX heading from markdown when it duplicates the node
    /// name (the header already shows it). Only a MATCHING leading H1 is stripped.</summary>
    public static string? StripLeadingTitleMarkdown(string? markdown, string name)
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
    public static string? StripLeadingTitleHtml(string? html, string name)
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

    /// <summary>
    /// <see cref="BuildTitle"/> as a TEMPLATE of the node at <paramref name="nodePath"/>: the read
    /// view's name and icon are BOUND to the node's own fields, and the edit view writes the content's
    /// <c>title</c> straight back to it. Only the click-to-edit toggle lives in <c>/data</c>.
    /// </summary>
    public static UiControl BuildTitleTemplate(LayoutAreaHost host, string nodePath, string dataId, bool canEdit)
    {
        var editStateId = $"editState_{dataId}_title";
        var editStateStream = host.Stream.GetDataStream<bool>(editStateId);
        var fields = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: false);

        var heading = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 12px;")
            .WithView(new IconControl(new JsonPointerReference(nameof(MeshNode.Icon))) { Width = "32px", DataContext = fields })
            .WithView(Controls.H1(new JsonPointerReference(nameof(MeshNode.Name))) with { DataContext = fields });

        var readView = Controls.Stack
            .WithStyle($"cursor: {(canEdit ? "pointer" : "default")};")
            .WithView(heading);
        if (canEdit)
            readView = readView.WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(editStateId, true);
                return Task.CompletedTask;
            });

        return Controls.Stack
            .WithView((h, ctx) =>
                editStateStream
                    .StartWith(false)
                    .DistinctUntilChanged()
                    .Select(isEditing => isEditing && canEdit
                        ? BuildTitleEditView(h, nodePath, editStateId)
                        : readView));
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
    internal static string RenderNodeIconHtml(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
            return "";
        var trimmed = icon.Trim();
        if (trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return $"<span style=\"display:inline-flex; width:32px; height:32px; overflow:hidden;\">{SizeInlineSvg(trimmed)}</span>";
        // A `content:` UCR needs node-context resolution to a served URL — render nothing rather than a
        // broken <img src="content:…"> (proper resolution is a follow-up).
        if (trimmed.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            return "";
        var looksLikeUrl = trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("/") || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains('.');
        if (looksLikeUrl)
            return $"<img src=\"{System.Web.HttpUtility.HtmlAttributeEncode(trimmed)}\" alt=\"\" style=\"width:32px; height:32px; object-fit:contain;\" />";
        // A bare token: an emoji (non-ASCII glyph) renders; a legacy Fluent icon NAME (an ASCII word like
        // "Building") is not an image and renders nothing rather than literal text.
        return trimmed.All(char.IsAscii)
            ? ""
            : $"<span style=\"font-size:28px; line-height:1;\">{System.Web.HttpUtility.HtmlEncode(trimmed)}</span>";
    }

    /// <summary>Puts an inline <c>&lt;svg&gt;</c> icon through the backplate policy
    /// (<see cref="IconBackplate.Ensure"/>) and then forces it to fill its container, by injecting a
    /// <c>width/height:100%</c> style onto the root element — so an icon with a <c>viewBox</c> but no
    /// explicit <c>width</c>/<c>height</c> (which a browser would render at the ~300×150 default and
    /// overflow the tile) scales to its box instead.
    ///
    /// <para>🚨 The plate is not the caller's to remember, for the reason
    /// <see cref="MeshNodeImageHelper.SizeInlineSvg"/> carries in full (#4350): this sizer is the
    /// last thing that touches the markup before it is injected into a raw-HTML title row, so a
    /// <c>currentColor</c> outline that skipped the policy inherited the heading's color and
    /// vanished on one of the two themes. For an icon that already paints its own full-bleed plate
    /// the policy is a no-op — its plate, hue and glyph are untouched — and the fill-to-box style
    /// below remains the only markup this method adds.</para></summary>
    internal static string SizeInlineSvg(string svg) =>
        System.Text.RegularExpressions.Regex.Replace(IconBackplate.Ensure(svg).Trim(), "^<svg\\b",
            "<svg style=\"width:100%;height:100%\" preserveAspectRatio=\"xMidYMid meet\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
