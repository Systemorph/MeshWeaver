using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph;

/// <summary>
/// Custom views for Space nodes.
/// </summary>
public static class SpaceLayoutAreas
{
    private const string ThinScrollbar = "scrollbar-width: thin; scrollbar-color: rgba(128,128,128,0.3) transparent;";
    // Full-bleed: the space page uses the whole viewport width (just a comfortable side
    // inset), not a centered reading column — so the markdown body and the bottom navigation
    // catalog fill the screen instead of a ~1/3 stripe on wide displays.
    private const string ContentInset = "max-width: 100%; padding: 0 32px;";

    /// <summary>
    /// GitHub-style space header view with live dashboard below.
    /// Shows logo, name, description, stats, then a set of MeshSearch sections scoped
    /// to the space's own partition, and a chat input inviting content creation.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var options = host.Hub.JsonSerializerOptions;

        var nodeStream = host.Workspace.GetStream<MeshNode>()
            ?.Select(nodes => nodes?.FirstOrDefault(n => n.Path == hubPath))
            ?? Observable.Return<MeshNode?>(null);

        // 🚨 The Space lives on MeshNode.Content — NOT in its own stream.
        // WithContentType<Space> registers Space in the TypeRegistry (serialization) but
        // NOT as a workspace TypeSource, so host.Workspace.GetStream<Space>() returns null
        // (Workspace.GetStream<T> bails when DataContext.GetTypeSource(T) is null). The old
        // spaceStream was therefore ALWAYS null → space.Logo / space.Body were never read →
        // every Space showed the node icon + the welcome placeholder instead of its own
        // logo/body. Read the Space off the node's Content via ContentAs (handles the
        // typed-instance, JsonElement, and null cases).
        // Compose with the effective-permission stream so the space's title is click-to-edit
        // ONLY for a user who can Update the node (mirrors MeshNodeLayoutAreas.Overview) — pure
        // observable composition, no await.
        return nodeStream.CombineLatest(
            host.Hub.GetEffectivePermissions(hubPath),
            (node, permissions) =>
            {
                // Gate on Read first (mirrors MeshNodeLayoutAreas.Overview): a user without Read
                // must get Access Denied, not sit forever on "Loading..." should the node stream
                // never emit for them.
                if (!permissions.HasFlag(Permission.Read))
                    return (UiControl?)MeshNodeLayoutAreas.BuildAccessDenied(hubPath);

                if (node == null)
                    return Controls.Markdown("*Loading...*") as UiControl;

                var canEdit = permissions.HasFlag(Permission.Update);
                var space = ResolveSpace(node, options);
                return BuildSpaceView(host, space, node, canEdit);
            });
    }

    /// <summary>
    /// The Space's Edit area: a full-page markdown editor on the Space's <b>main markdown body</b>
    /// (<see cref="Space.Body"/>) — NOT the generic property form over every Space field. It uses the
    /// SAME <see cref="MarkdownEditorControl"/> the Markdown node's editor uses
    /// (<c>MarkdownEditLayoutArea</c>), but bound to the <c>body</c> CONTENT field via a node-bound
    /// <c>DataContext</c> (<see cref="LayoutAreaReference.GetMeshNodeDataContext"/>, <c>bindContent:true</c>).
    /// Reads come straight off the node stream and edits write back per-field — editing the CONTENT
    /// OBJECT (the Space), never replacing the node's content. The Markdown node's
    /// <c>MarkdownEditorControl.WithAutoSave</c> path replaces the whole node content with a
    /// <c>MarkdownContent</c>, which would destroy the structured Space; the node-bound field pointer
    /// is the content-object-preserving variant of the same mechanism. See Doc/GUI/DataBinding.
    /// </summary>
    public static IObservable<UiControl?> Edit(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        // Compose with the permission stream — pure observable, no await (mirrors MeshNodeLayoutAreas.EditNode).
        return host.Workspace.GetMeshNodeStream().CombineLatest(
            host.Hub.GetEffectivePermissions(hubPath),
            (node, permissions) => !permissions.HasFlag(Permission.Update)
                ? (UiControl?)MeshNodeLayoutAreas.BuildAccessDenied(hubPath)
                : (UiControl?)BuildBodyEditor(node, hubPath));
    }

    /// <summary>
    /// Builds the body editor: a back link, an editable name, and the markdown editor — all bound to
    /// the Space CONTENT object via a node-bound <c>DataContext</c> so each field reads/writes straight
    /// to the node stream per-field (one source of truth, no <c>/data</c> replica, no whole-content clobber).
    /// </summary>
    private static UiControl BuildBodyEditor(MeshNode? node, string hubPath)
    {
        if (node is null)
            return Controls.Markdown("*Space not found.*");

        var spacePath = node.Path ?? hubPath;
        // Field pointers resolve against the node's Content JSON (the Space). Every control's edit
        // is a per-field read-modify-write straight to the node — see Doc/GUI/DataBinding "Node-bound DataContext".
        var contentCtx = LayoutAreaReference.GetMeshNodeDataContext(spacePath, bindContent: true);

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("height: calc(100vh - 100px); display: flex; flex-direction: column;");

        // Header: back to the space's default page + editable name + autosave hint.
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithVerticalAlignment(VerticalAlignment.Center)
            .WithHorizontalGap(12)
            .WithStyle("padding: 8px 0; border-bottom: 1px solid var(--neutral-stroke-rest); flex-shrink: 0;");

        headerRow = headerRow.WithView(Controls.Button("")
            .WithIconStart(FluentIcons.ArrowLeft())
            .WithAppearance(Appearance.Stealth)
            .WithNavigateToHref($"/{spacePath}"));

        headerRow = headerRow.WithView(new TextFieldControl(new JsonPointerReference("name"))
        {
            Immediate = true,
            Placeholder = "Space name",
            DataContext = contentCtx
        }.WithStyle("flex: 1; font-size: 1.25rem; font-weight: 600;"));

        headerRow = headerRow.WithView(Controls.Html(
            "<span style=\"color: var(--neutral-foreground-hint); font-size: 0.85rem;\">Changes are saved automatically</span>"));

        container = container.WithView(headerRow);

        // Main markdown body — the SAME MarkdownEditorControl the Markdown node uses, but its Value
        // pointer + node-bound DataContext make it read/write the Space's `body` content field per-field
        // (no WithAutoSave whole-content replace). The editor view routes node-bound reads/writes through
        // MeshNodeBindingExtensions automatically (see Doc/GUI/DataBinding).
        var editor = new MarkdownEditorControl
        {
            Value = new JsonPointerReference("body"),
            DataContext = contentCtx,
            Height = "100%",
            MaxHeight = "none",
            Placeholder = "Write your space's overview in markdown…"
        };

        container = container.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; width: 100%; min-height: 0; overflow: hidden; margin-top: 8px;")
            .WithView(editor));

        return container;
    }

    /// <summary>
    /// Resolves the <see cref="Space"/> off a node's <see cref="MeshNode.Content"/>, robust to
    /// every form Content can take: an already-typed <see cref="Space"/>, a degraded
    /// <see cref="JsonElement"/>, or — when the content lost its typing entirely — a raw string.
    /// A JSON-object string is deserialised back into the Space; any other non-empty string is
    /// taken as the body so the page still shows the author's text instead of the welcome
    /// placeholder. Returns null only when there is genuinely nothing to render.
    /// </summary>
    internal static Space? ResolveSpace(MeshNode? node, JsonSerializerOptions options)
    {
        if (node is null)
            return null;

        var space = node.ContentAs<Space>(options);
        if (space != null)
            return space;

        // Content degraded to a bare string (or a JSON-string JsonElement) — recover it
        // rather than falling back to the welcome placeholder.
        var raw = node.Content switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (raw.TrimStart().StartsWith('{'))
        {
            try { return JsonSerializer.Deserialize<Space>(raw, options); }
            catch (JsonException) { /* not Space JSON — fall through and treat it as the body */ }
        }
        return new Space { Name = node.Name ?? node.Path, Body = raw };
    }

    private static UiControl BuildSpaceView(
        LayoutAreaHost host,
        Space? space,
        MeshNode? node,
        bool canEdit)
    {
        var spacePath = node?.Path ?? host.Hub.Address.ToString();
        var spaceName = space?.Name ?? node?.Name ?? spacePath;

        var shell = Controls.Stack
            .WithWidth("100%")
            .WithStyle($"height: 100%; overflow-y: auto; {ThinScrollbar}");

        shell = shell.WithView(BuildHeader(host, space, node, spaceName, spacePath, canEdit));
        shell = shell.WithView(BuildBodyContent(space, node, spacePath));

        if (IsSystemorph(spacePath))
            shell = shell.WithView(BuildSystemorphHighlights(spacePath));

        // No hardcoded navigation/catalog section. The space body (BuildBodyContent above) owns the
        // catalog: the standard WelcomeMarkdown template embeds it INLINE via @@("area/Search"), and
        // an author can move/tune/remove it in the editable Body like any other content. A fixed
        // BuildNavigation section here double-rendered the catalog for any body that embedded it.
        return shell;
    }

    /// <summary>
    /// Logo + name + description + stats row. GitHub-style header, fixed at the top.
    /// The name renders as a click-to-edit title (see <see cref="BuildEditableTitle"/>) when
    /// <paramref name="canEdit"/> — an editor renames the space in place; everyone else sees a
    /// plain heading.
    /// </summary>
    private static UiControl BuildHeader(
        LayoutAreaHost host, Space? space, MeshNode? node, string spaceName, string spacePath, bool canEdit)
    {
        var description = space?.Description;
        var logo = space?.Logo ?? GetNodeLogo(node);
        var website = space?.Website;
        var location = space?.Location;
        var email = space?.Email;
        var isVerified = space?.IsVerified ?? false;

        var container = Controls.Stack
            .WithStyle("flex-shrink: 0; padding: 24px 0 16px 0; width: 100%;");

        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle($"gap: 24px; align-items: flex-start; width: 100%; {ContentInset}");

        // Logo (large, rounded square like GitHub)
        UiControl logoControl;
        if (!string.IsNullOrEmpty(logo))
        {
            // Natural-aspect, never cropped: a wide banner logo (e.g. ATIOZ) and a square
            // avatar both render whole. `object-fit: cover` in a fixed 100×100 box cropped wide
            // logos to their middle strip — use max-box + auto sizing so the image scales to fit
            // within the bounds at its own aspect ratio (contain just backs that up for any
            // intrinsic-size oddities).
            logoControl = Controls.Html(
                $"<img src=\"{System.Web.HttpUtility.HtmlAttributeEncode(logo)}\" alt=\"\" style=\"max-height: 96px; max-width: 340px; width: auto; height: auto; border-radius: 12px; object-fit: contain; background: var(--neutral-layer-2); padding: 6px; box-sizing: border-box;\" />");
        }
        else
        {
            var initials = GetInitials(spaceName);
            logoControl = Controls.Html(
                $"<div style=\"width: 100px; height: 100px; border-radius: 12px; background: var(--accent-fill-rest); display: flex; align-items: center; justify-content: center; color: white; font-size: 2.5rem; font-weight: 600;\">" +
                $"{System.Web.HttpUtility.HtmlEncode(initials)}</div>");
        }

        headerRow = headerRow.WithView(logoControl);

        var infoColumn = Controls.Stack.WithStyle("gap: 8px; flex: 1;");

        infoColumn = infoColumn.WithView(BuildEditableTitle(host, spacePath, spaceName, canEdit));

        if (!string.IsNullOrEmpty(description))
        {
            infoColumn = infoColumn.WithView(
                Controls.Markdown(description).WithStyle("color: var(--neutral-foreground-hint); font-size: 1rem;"));
        }

        if (isVerified)
        {
            infoColumn = infoColumn.WithView(Controls.Html(
                "<span style=\"display: inline-flex; align-items: center; gap: 4px; padding: 2px 8px; border-radius: 12px; border: 1px solid #3fb950; color: #3fb950; font-size: 0.75rem; font-weight: 500; width: fit-content;\">" +
                "<svg width=\"12\" height=\"12\" viewBox=\"0 0 16 16\" fill=\"currentColor\"><path d=\"M13.78 4.22a.75.75 0 010 1.06l-7.25 7.25a.75.75 0 01-1.06 0L2.22 9.28a.75.75 0 111.06-1.06L6 10.94l6.72-6.72a.75.75 0 011.06 0z\"/></svg>" +
                "Verified</span>"));
        }

        var statsRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 24px; margin-top: 12px; flex-wrap: wrap;");

        if (!string.IsNullOrEmpty(location))
        {
            statsRow = statsRow.WithView(Controls.Html(
                $"<span style=\"display: inline-flex; align-items: center; gap: 6px; color: var(--neutral-foreground-hint);\">" +
                $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\" fill=\"currentColor\"><path d=\"M8 0a5 5 0 0 0-5 5c0 4.17 4.42 10.22 4.62 10.48a.5.5 0 0 0 .76 0C8.58 15.22 13 9.17 13 5a5 5 0 0 0-5-5Zm0 7.5a2.5 2.5 0 1 1 0-5 2.5 2.5 0 0 1 0 5Z\"/></svg>" +
                $"{System.Web.HttpUtility.HtmlEncode(location)}</span>"));
        }

        if (!string.IsNullOrEmpty(website))
        {
            var displayUrl = website.Replace("https://", "").Replace("http://", "").TrimEnd('/');
            statsRow = statsRow.WithView(Controls.Html(
                $"<a href=\"{System.Web.HttpUtility.HtmlAttributeEncode(website)}\" target=\"_blank\" style=\"display: inline-flex; align-items: center; gap: 6px; color: var(--accent-foreground-rest); text-decoration: none;\">" +
                $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\" fill=\"currentColor\"><path d=\"M7.775 3.275a.75.75 0 0 0 1.06 1.06l1.25-1.25a2 2 0 1 1 2.83 2.83l-2.5 2.5a2 2 0 0 1-2.83 0 .75.75 0 0 0-1.06 1.06 3.5 3.5 0 0 0 4.95 0l2.5-2.5a3.5 3.5 0 0 0-4.95-4.95l-1.25 1.25Zm-.025 5.368-1.25 1.25a2 2 0 0 1-2.83-2.83l2.5-2.5a2 2 0 0 1 2.83 0 .75.75 0 0 0 1.06-1.06 3.5 3.5 0 0 0-4.95 0l-2.5 2.5a3.5 3.5 0 1 0 4.95 4.95l1.25-1.25a.75.75 0 0 0-1.06-1.06Z\"/></svg>" +
                $"{System.Web.HttpUtility.HtmlEncode(displayUrl)}</a>"));
        }

        if (!string.IsNullOrEmpty(email))
        {
            statsRow = statsRow.WithView(Controls.Html(
                $"<a href=\"mailto:{System.Web.HttpUtility.HtmlAttributeEncode(email)}\" style=\"display: inline-flex; align-items: center; gap: 6px; color: var(--neutral-foreground-hint); text-decoration: none;\">" +
                $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\" fill=\"currentColor\"><path d=\"M1.75 2h12.5c.966 0 1.75.784 1.75 1.75v8.5A1.75 1.75 0 0 1 14.25 14H1.75A1.75 1.75 0 0 1 0 12.25v-8.5C0 2.784.784 2 1.75 2ZM1.5 12.25c0 .138.112.25.25.25h12.5a.25.25 0 0 0 .25-.25V5.809L8.38 9.397a.75.75 0 0 1-.76 0L1.5 5.809v6.441Zm13-8.181v-.319a.25.25 0 0 0-.25-.25H1.75a.25.25 0 0 0-.25.25v.319l6.5 3.98 6.5-3.98Z\"/></svg>" +
                $"{System.Web.HttpUtility.HtmlEncode(email)}</a>"));
        }

        infoColumn = infoColumn.WithView(statsRow);
        headerRow = headerRow.WithView(infoColumn);

        container = container.WithView(headerRow);

        // Divider
        container = container.WithView(Controls.Html(
            $"<div style=\"{ContentInset}\"><hr style=\"border: none; border-top: 1px solid var(--neutral-stroke-rest); margin: 16px 0 0 0;\" /></div>"));

        return container;
    }

    /// <summary>
    /// The space's H1 title as a click-to-edit heading. Read view is a plain heading; when
    /// <paramref name="canEdit"/> and the user clicks it, an inline <see cref="TextFieldControl"/>
    /// bound to the Space content <c>name</c> field (node-bound DataContext) takes over, so the edit
    /// writes straight back to the node stream — and to <see cref="MeshNode.Name"/> via the Space's
    /// <c>[MeshNodeProperty(nameof(MeshNode.Name))]</c> mapping. Same mechanism the Space Edit area
    /// (<see cref="BuildBodyEditor"/>) uses for the name field — the click-to-edit toggle is the only
    /// thing living in <c>/data</c>. See Doc/GUI/DataBinding "Node-bound DataContext".
    /// </summary>
    private static UiControl BuildEditableTitle(
        LayoutAreaHost host, string spacePath, string spaceName, bool canEdit)
    {
        var editStateId = $"editState_{EditLayoutArea.GetDataId(spacePath)}_spaceTitle";
        var editStateStream = host.Stream.GetDataStream<bool>(editStateId);

        return Controls.Stack
            .WithView((_, _) =>
                editStateStream
                    .StartWith(false)
                    .DistinctUntilChanged()
                    .Select(isEditing =>
                        isEditing && canEdit
                            ? BuildTitleEditView(spacePath, editStateId)
                            : BuildTitleReadView(spaceName, editStateId, canEdit)));
    }

    private static UiControl BuildTitleReadView(string spaceName, string editStateId, bool canEdit)
    {
        var titleStack = Controls.Stack
            .WithStyle($"cursor: {(canEdit ? "pointer" : "default")};")
            .WithView(Controls.Html(
                $"<h1 style=\"margin: 0; font-size: 2rem; font-weight: 600;\">{System.Web.HttpUtility.HtmlEncode(spaceName)}</h1>"));

        if (canEdit)
            titleStack = titleStack.WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(editStateId, true);
                return Task.CompletedTask;
            });

        return titleStack;
    }

    private static UiControl BuildTitleEditView(string spacePath, string editStateId)
        => new TextFieldControl(new JsonPointerReference("name"))
        {
            Immediate = true,
            AutoFocus = true,
            Placeholder = "Space name",
            DataContext = LayoutAreaReference.GetMeshNodeDataContext(spacePath, bindContent: true)
        }
        .WithStyle("font-size: 2rem; font-weight: 600; border: none; background: transparent; min-width: 300px; width: 100%;")
        .WithBlurAction(ctx =>
        {
            ctx.Host.UpdateData(editStateId, false);
            return Task.CompletedTask;
        });

    /// <summary>
    /// Body content — priority: node.PreRenderedHtml → space.Body → default welcome markdown.
    /// </summary>
    internal static UiControl BuildBodyContent(Space? space, MeshNode? node, string spacePath)
    {
        // Generous bottom padding so the in-body catalog @@-embed has vertical breathing
        // room below it (the catalog is no longer a fixed LayoutArea — see BuildSpaceView).
        var bodyStyle = $"{ContentInset} padding-top: 24px; padding-bottom: 48px;";

        if (!string.IsNullOrWhiteSpace(node?.PreRenderedHtml))
            return new MarkdownControl("") { Html = node.PreRenderedHtml }.WithStyle(bodyStyle);

        // 🚨 NodePath is what makes the body's RELATIVE @@-embeds resolve. The default
        // welcome ships @@("area/Search"); the body MarkdownControl is a CHILD of the Overview
        // area, whose stream owner is not a reliable node-path source — so the embed would
        // render an unaddressed (dead) layout-area div without this. Setting NodePath to the
        // Space path makes @@("area/Search") resolve to {spacePath}/area/Search. Authored
        // bodies may also use the absolute @@/{space}/area/Search, which resolves either way.
        var body = !string.IsNullOrWhiteSpace(space?.Body) ? space!.Body! : SpaceNodeType.WelcomeMarkdown;
        return (Controls.Markdown(body) with { NodePath = spacePath }).WithStyle(bodyStyle);
    }

    /// <summary>
    /// Dashboard grid mirroring the UserActivity layout but scoped to this space's partition:
    /// Latest Threads, Activity Feed, Recent Updates. The content catalog is NOT
    /// hard-wired here anymore — it ships as a deletable <c>@@("area/Search")</c>
    /// section inside the space's markdown body (see
    /// <see cref="SpaceNodeType.WelcomeMarkdown"/>), so each space owner controls
    /// whether and where the catalog appears.
    /// </summary>
    private static UiControl BuildDashboardGrid(string spacePath)
    {
        var grid = Controls.LayoutGrid
            .WithStyle($"{ContentInset} padding-top: 24px; padding-bottom: 24px; gap: 24px; width: 100%;");

        // Latest Threads — full width
        grid = grid.WithView(BuildLatestThreads(spacePath), skin => skin.WithXs(12));

        // Activity feed — 2/3 width on desktop
        grid = grid.WithView(BuildActivityFeed(spacePath), skin => skin.WithXs(12).WithSm(8));

        // Recently updated main content — 1/3 width on desktop
        grid = grid.WithView(BuildRecentUpdates(spacePath), skin => skin.WithXs(12).WithSm(4));

        return grid;
    }

    /// <summary>
    /// Systemorph-specific highlight strip — Featured Stories grid, embedded Event Calendar,
    /// and a Post Pipeline of Social Media posts. Each section uses rich Thumbnail layout areas
    /// where available so cards have visual punch instead of plain icon+name rows.
    /// </summary>
    private static UiControl BuildSystemorphHighlights(string spacePath)
    {
        var stack = Controls.Stack
            .WithStyle($"{ContentInset} padding-top: 24px; padding-bottom: 24px; gap: 32px; width: 100%;");

        stack = stack.WithView(BuildFeaturedStories(spacePath));
        stack = stack.WithView(BuildEventCalendar(spacePath));
        stack = stack.WithView(BuildPostShowcase(spacePath));

        return stack;
    }

    /// <summary>
    /// Featured Marketing Stories — Markdown children of the Story series hub at {spacePath}/Story.
    /// </summary>
    private static UiControl BuildFeaturedStories(string spacePath)
    {
        var heading = Controls.Html(
            $"<div style=\"display:flex;align-items:baseline;justify-content:space-between;gap:12px;padding-bottom:8px;border-bottom:1px solid var(--neutral-stroke-rest);\">" +
            $"<h2 style=\"margin:0;font-size:1.35rem;\">✦ Featured Stories</h2>" +
            $"<a href=\"/{spacePath}/Story\" style=\"color:var(--accent-foreground-rest);text-decoration:none;font-size:0.9rem;\">See all →</a>" +
            $"</div>");

        var grid = Controls.MeshSearch
            .WithHiddenQuery($"namespace:{spacePath}/Story scope:children nodeType:Markdown sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(3)
            .WithItemLimit(6)
            .WithMaxRows(2)
            .WithReactiveMode(true);

        return Controls.Stack
            .WithStyle("gap: 12px; width: 100%;")
            .WithView(heading)
            .WithView(grid);
    }

    /// <summary>
    /// Embed the existing EventCalendar Overview from {spacePath}/Events so the month grid
    /// shows inline on the space page. Single source of truth — same widget the
    /// dedicated calendar page uses.
    /// </summary>
    private static UiControl BuildEventCalendar(string spacePath)
    {
        var eventsPath = $"{spacePath}/Events";

        var heading = Controls.Html(
            $"<div style=\"display:flex;align-items:baseline;justify-content:space-between;gap:12px;padding-bottom:8px;border-bottom:1px solid var(--neutral-stroke-rest);\">" +
            $"<h2 style=\"margin:0;font-size:1.35rem;\">📅 Upcoming Events</h2>" +
            $"<a href=\"/{eventsPath}\" style=\"color:var(--accent-foreground-rest);text-decoration:none;font-size:0.9rem;\">Open calendar →</a>" +
            $"</div>");

        var calendar = new LayoutAreaControl(eventsPath, new LayoutAreaReference("Overview"))
            .WithShowProgress(false)
            .WithStyle("width: 100%;");

        return Controls.Stack
            .WithStyle("gap: 12px; width: 100%;")
            .WithView(heading)
            .WithView(calendar);
    }

    /// <summary>
    /// Social Media post pipeline — all Posts under {spacePath}/SocialMedia rendered as
    /// LinkedIn-style preview cards (PostThumbnail layout area). Status pills on each card
    /// distinguish Draft / Scheduled / Published at a glance.
    /// </summary>
    private static UiControl BuildPostShowcase(string spacePath)
    {
        var socialPath = $"{spacePath}/SocialMedia";

        var heading = Controls.Html(
            $"<div style=\"display:flex;align-items:baseline;justify-content:space-between;gap:12px;padding-bottom:8px;border-bottom:1px solid var(--neutral-stroke-rest);\">" +
            $"<h2 style=\"margin:0;font-size:1.35rem;\">📱 Social Media</h2>" +
            $"<a href=\"/{socialPath}\" style=\"color:var(--accent-foreground-rest);text-decoration:none;font-size:0.9rem;\">See all →</a>" +
            $"</div>");

        var posts = Controls.MeshSearch
            .WithHiddenQuery($"namespace:{socialPath} nodeType:{spacePath}/Post scope:subtree sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemArea("Thumbnail")
            .WithMaxColumns(2)
            .WithItemLimit(12)
            .WithMaxRows(6)
            .WithReactiveMode(true)
            .WithCreateNodeType($"{spacePath}/Post")
            .WithCreateNamespace(socialPath);

        return Controls.Stack
            .WithStyle("gap: 12px; width: 100%;")
            .WithView(heading)
            .WithView(posts);
    }

    /// <summary>
    /// Threads created against this space or its descendants.
    /// </summary>
    private static UiControl BuildLatestThreads(string spacePath)
    {
        return Controls.MeshSearch
            .WithTitle("Latest Threads")
            .WithHiddenQuery($"nodeType:Thread namespace:{spacePath}/*/_Thread sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemLimit(40)
            .WithMaxRows(2)
            .WithMaxColumns(4)
            .WithReactiveMode(true)
            .WithCreateNodeType("Thread")
            .WithCreateNamespace(spacePath);
    }

    /// <summary>
    /// Activity timeline scoped to this space — recent edits, comments, threads.
    /// </summary>
    private static UiControl BuildActivityFeed(string spacePath)
    {
        return Controls.MeshSearch
            .WithTitle("Activity Feed")
            .WithHiddenQuery($"source:activity namespace:{spacePath} scope:subtree is:main sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(2)
            .WithItemLimit(40)
            .WithMaxRows(4)
            .WithReactiveMode(true);
    }

    /// <summary>
    /// Recently updated main content in the space — compact sidebar column.
    /// </summary>
    private static UiControl BuildRecentUpdates(string spacePath)
    {
        return Controls.MeshSearch
            .WithTitle("Recently Updated")
            .WithHiddenQuery($"namespace:{spacePath} is:main scope:subtree sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(1)
            .WithItemLimit(20)
            .WithMaxRows(4)
            .WithReactiveMode(true);
    }

    private static bool IsSystemorph(string spacePath) =>
        string.Equals(spacePath, "Systemorph", StringComparison.OrdinalIgnoreCase);

    private static string? GetNodeLogo(MeshNode? node)
    {
        return MeshNodeThumbnailControl.GetImageUrlForNode(node);
    }

    private static string GetInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}";
        return name.Length >= 2 ? $"{char.ToUpper(name[0])}{char.ToUpper(name[1])}" : char.ToUpper(name[0]).ToString();
    }
}
