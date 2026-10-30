using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Layout.Domain;

/// <summary>
/// Options for building a unified content view (Overview, Edit, or Create).
/// </summary>
public record ContentViewOptions
{
    /// <summary>
    /// The data ID used for data binding.
    /// </summary>
    public required string DataId { get; init; }

    /// <summary>
    /// The type of the content being displayed/edited.
    /// </summary>
    public required Type ContentType { get; init; }

    /// <summary>
    /// Whether editing is allowed based on permissions.
    /// </summary>
    public bool CanEdit { get; init; } = true;

    /// <summary>
    /// If true (default), starts read-only and can toggle to edit. If false, stays in edit mode.
    /// </summary>
    public bool IsToggleable { get; init; } = true;

    /// <summary>
    /// Static title text. Ignored if TitleDataId is set.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// If set, title is data-bound to the "name" field of this data ID.
    /// </summary>
    public string? TitleDataId { get; init; }

    /// <summary>
    /// Title prefix shown before the data-bound name (e.g., "Create ").
    /// </summary>
    public string? TitlePrefix { get; init; }

    /// <summary>
    /// Optional description shown below the title.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional header actions (navigation icons, etc.) shown to the right of the title.
    /// </summary>
    public UiControl? HeaderActions { get; init; }

    /// <summary>
    /// Optional footer actions (buttons) shown below the property form.
    /// </summary>
    public UiControl? FooterActions { get; init; }

    /// <summary>
    /// Optional padding for the entire view. Default is no padding.
    /// </summary>
    public string? Padding { get; init; }

    /// <summary>
    /// When set, the generated form controls bind their field VALUES against this node-bound
    /// DataContext (see <see cref="LayoutAreaReference.MeshNodePrefix"/>) instead of the layout-area
    /// <c>/data/{DataId}</c> replica — reads come straight off the node stream and edits write
    /// straight back to it (ONE source of truth, no replicate-then-save). Edit-state (the
    /// click-to-edit toggle) still lives in <c>/data</c> keyed by <see cref="DataId"/>. Leave null
    /// for the create form (no node exists yet) and other transient editors.
    /// </summary>
    public string? BoundDataContext { get; init; }
}

/// <summary>
/// Static helper that builds unified content view layouts (overview, edit, create) for domain types.
/// Composes a header, a property form grid, and optional footer actions using the standard framework controls.
/// </summary>
public static class EditLayoutArea
{
    /// <summary>
    /// Builds a unified content view that works for Overview, Edit, and Create scenarios.
    /// The structure is: Header (title + actions) → Property form → Footer actions
    /// </summary>
    public static UiControl BuildContentView(LayoutAreaHost host, ContentViewOptions options)
    {
        var style = "width: 100%;";
        if (!string.IsNullOrEmpty(options.Padding))
            style += $" padding: {options.Padding};";

        var stack = Controls.Stack.WithStyle(style);

        // Header with title (static or data-bound)
        if (options.TitleDataId != null)
        {
            // Data-bound title
            stack = stack.WithView((h, _) =>
                h.Stream.GetDataStream<Dictionary<string, object?>>(options.TitleDataId)
                    .Select(metadata =>
                    {
                        var name = metadata?.GetValueOrDefault("name")?.ToString() ?? "Untitled";
                        var titleText = string.IsNullOrEmpty(options.TitlePrefix) ? name : $"{options.TitlePrefix}{name}";
                        return BuildHeader(titleText, options.HeaderActions, options.Description);
                    }));
        }
        else if (!string.IsNullOrEmpty(options.Title))
        {
            stack = stack.WithView(BuildHeader(options.Title, options.HeaderActions, options.Description));
        }

        // Property form
        stack = stack.WithView(BuildPropertyForm(host, options.ContentType, options.DataId, options.CanEdit, options.IsToggleable, options.BoundDataContext));

        // Footer actions (buttons)
        if (options.FooterActions != null)
        {
            stack = stack.WithView(options.FooterActions);
        }

        return stack;
    }

    private static UiControl BuildHeader(string title, UiControl? headerActions, string? description)
    {
        var headerStack = Controls.Stack.WithWidth("100%");

        // headerActions is accepted for the callers' sake but has never been drawn: both branches of
        // the former HTML header rendered the title alone.
        _ = headerActions;
        headerStack = headerStack.WithView(Controls.H2(title).WithStyle("margin: 0 0 1rem 0;"));

        if (DomainViews.Description(description) is { } descriptionLine)
            headerStack = headerStack.WithView(descriptionLine);

        return headerStack;
    }

    /// <summary>
    /// Builds the Edit view for an entity using the same layout as Overview but with all fields in edit mode.
    /// </summary>
    public static UiControl Edit(LayoutAreaHost host, ITypeDefinition typeDefinition, object id, RenderingContext _)
    {
        // Title with right-aligned navigation glyphs
        var links = ImmutableList.Create(
            DomainViews.NavGlyph("⧉", host.Localize("ui.dataModel"), $"/{host.Hub.Address}/DataModel/{typeDefinition.Type.Name}"));
        if (!string.IsNullOrWhiteSpace(typeDefinition.CollectionName))
            links = links.Add(DomainViews.NavGlyph("🗃️", host.Localize("ui.viewCatalog"), $"/{host.Hub.Address}/Catalog/{typeDefinition.CollectionName}"));

        var stack = Controls.Stack.WithWidth("100%")
            .WithView(DomainViews.TitleRow(Controls.H1(typeDefinition.DisplayName).WithStyle("margin: 0;"), links.ToArray()));

        // Description if available — data-bound: the documentation file is read on the I/O pool, not
        // while this view is built.
        stack = stack.WithView(DomainViews.DescriptionOf(host.Hub, typeDefinition.Type));

        // Use Overview with startInEditMode=true for the form
        return stack.WithView((areaHost, ctx) => EditLayout(areaHost, ctx, typeDefinition, id));
    }

    private static UiControl EditLayout(LayoutAreaHost host, RenderingContext context, ITypeDefinition typeDefinition, object id)
    {
        var dataId = GetDataId($"{host.Hub.Address}_{typeDefinition.CollectionName}_{id}");
        var stream = host.Workspace
            .GetStream(new EntityReference(typeDefinition.CollectionName, id));

        // FeedData, never a bare Subscribe: the projection below THROWS on a missing entity, and
        // without an error arm that rethrew on the pool (Template.BindFeed).
        host.RegisterForDisposal(context.Area, host.FeedData(context.Area, dataId, stream!
            .Select(e => typeDefinition.SerializeEntityAndId(e?.Value ?? throw new InvalidOperationException("Entity value is null"), host.Hub.JsonSerializerOptions))));

        // Reuse BuildPropertyForm with isToggleable=false for pure edit mode (no toggle back to read-only)
        return BuildPropertyForm(host, typeDefinition.Type, dataId, canEdit: true, isToggleable: false);
    }

    /// <summary>
    /// Builds the property form with grid for regular properties and separate sections for markdown.
    /// Uses MapToToggleableControl for readonly/edit toggle functionality.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="contentType">The type of the content being edited.</param>
    /// <param name="dataId">The data ID used for data binding.</param>
    /// <param name="canEdit">Whether editing is allowed based on permissions.</param>
    /// <param name="isToggleable">If true (default), starts read-only and can toggle. If false, stays in edit mode.</param>
    /// <param name="boundDataContext">
    /// Optional node-bound data context path. When set, form controls bind their values directly to the mesh node stream
    /// instead of the layout-area /data replica, providing a single source of truth. Leave null for create forms.
    /// </param>
    public static UiControl BuildPropertyForm(
        LayoutAreaHost host,
        Type contentType,
        string dataId,
        bool canEdit,
        bool isToggleable = true,
        string? boundDataContext = null)
    {
        // Get browsable properties (skip Title - shown in header)
        var properties = contentType.GetProperties()
            .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .Where(p => !IsTitleProperty(p.Name))
            .ToList();

        // Separate properties into regular vs markdown vs collection
        var regularProps = properties
            .Where(p => !EditorExtensions.IsMarkdownProperty(p)
                        && p.GetCustomAttribute<MeshNodeCollectionAttribute>() == null)
            .ToList();

        var markdownProps = properties
            .Where(p => EditorExtensions.IsMarkdownProperty(p))
            .ToList();

        var collectionProps = properties
            .Where(p => p.GetCustomAttribute<MeshNodeCollectionAttribute>() != null)
            .ToList();

        var stack = Controls.Stack.WithWidth("100%");

        // Build grid for regular properties using MapToToggleableControl
        if (regularProps.Count > 0)
        {
            var propsGrid = Controls.LayoutGrid.WithStyle(s => s.WithWidth("100%")).WithSkin(s => s.WithSpacing(2));

            foreach (var prop in regularProps)
            {
                var control = host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit, host, isToggleable, boundDataContext);
                propsGrid = propsGrid.WithView(control, s => s.WithPropertyCellSizing(prop));
            }

            stack = stack.WithView(propsGrid);
        }

        // Build markdown sections using MapToToggleableControl (full width)
        foreach (var prop in markdownProps)
        {
            var control = host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit, host, isToggleable, boundDataContext);
            stack = stack.WithView(control);
        }

        // Build collection sections using MapToToggleableControl (full width)
        foreach (var prop in collectionProps)
        {
            var control = host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit, host, isToggleable, boundDataContext);
            stack = stack.WithView(control);
        }

        return stack;
    }

    /// <summary>
    /// Alias for BuildPropertyForm for backward compatibility.
    /// </summary>
    public static UiControl Overview(
        LayoutAreaHost host,
        Type contentType,
        string dataId,
        bool canEdit,
        bool isToggleable = true) => BuildPropertyForm(host, contentType, dataId, canEdit, isToggleable);

    /// <summary>
    /// Applies the responsive grid-cell sizing for a property rendered in the auto-generated property form.
    /// Plain free-text string properties span the full row (<c>Xs(12)</c> at every breakpoint) so long values
    /// stay readable without entering edit mode; structured values (numbers, dates, booleans, enums,
    /// dimension / mesh-node references, fixed option lists — all short labels) keep the compact responsive
    /// cells (<c>Xs(12).Md(6).Lg(4)</c>, up to three per row on large screens).
    /// </summary>
    /// <param name="skin">The grid item skin to configure.</param>
    /// <param name="property">The property rendered in this grid cell.</param>
    /// <returns>A new <see cref="LayoutGridItemSkin"/> with the sizing appropriate for the property.</returns>
    public static LayoutGridItemSkin WithPropertyCellSizing(this LayoutGridItemSkin skin, PropertyInfo property)
        => IsFreeTextProperty(property)
            ? skin.WithXs(12)
            : skin.WithXs(12).WithMd(6).WithLg(4);

    /// <summary>
    /// Determines whether a property renders as plain free text: a <see cref="string"/> property without a
    /// <see cref="DimensionAttribute"/>, without a <see cref="MeshNodeAttribute"/> reference, and without fixed
    /// <see cref="UiControlAttribute.Options"/> — i.e. its value length is unbounded, so it needs the full row
    /// to be readable. Structured references and option-backed strings render as short labels and stay compact.
    /// </summary>
    /// <param name="property">The property to classify.</param>
    /// <returns><c>true</c> when the property is a plain free-text string; otherwise <c>false</c>.</returns>
    public static bool IsFreeTextProperty(PropertyInfo property)
        => property.PropertyType == typeof(string)
           && property.GetCustomAttribute<DimensionAttribute>() == null
           && property.GetCustomAttribute<MeshNodeAttribute>() == null
           && property.GetCustomAttribute<UiControlAttribute>()?.Options == null;

    /// <summary>
    /// Gets the consistent data ID for a node path. Used by both header and property overview.
    /// </summary>
    public static string GetDataId(string path) => $"content_{path.Replace("/", "_")}";

    /// <summary>
    /// Determines if a property name is a title property (displayed in header, not in form).
    /// </summary>
    public static bool IsTitleProperty(string name) =>
        name.Equals("Title", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DisplayName", StringComparison.OrdinalIgnoreCase);
}
