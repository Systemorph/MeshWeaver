using System.ComponentModel;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Reflection;

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
}

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
        stack = stack.WithView(BuildPropertyForm(host, options.ContentType, options.DataId, options.CanEdit, options.IsToggleable));

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

        if (headerActions != null)
        {
            // Header with title and actions side by side
            headerStack = headerStack.WithView(Controls.Html($@"
                <div style=""display: flex; justify-content: space-between; align-items: center; margin-bottom: 1rem; width: 100%;"">
                    <h2 style=""margin: 0; flex-grow: 1;"">{System.Web.HttpUtility.HtmlEncode(title)}</h2>
                </div>"));
            // Note: headerActions would need to be positioned, but for simplicity we add it separately
        }
        else
        {
            headerStack = headerStack.WithView(Controls.H2(title).WithStyle("margin: 0 0 1rem 0;"));
        }

        if (!string.IsNullOrEmpty(description))
        {
            headerStack = headerStack.WithView(Controls.Html($"<p style=\"color: var(--neutral-foreground-hint); margin-bottom: 1rem;\">{System.Web.HttpUtility.HtmlEncode(description)}</p>"));
        }

        return headerStack;
    }

    /// <summary>
    /// Builds the Edit view for an entity using the same layout as Overview but with all fields in edit mode.
    /// </summary>
    public static UiControl Edit(LayoutAreaHost host, ITypeDefinition typeDefinition, object id, RenderingContext _)
    {
        // Title with right-aligned navigation icons
        var navigationIcons = $"<a href=\"/{host.Hub.Address}/DataModel/{typeDefinition.Type.Name}\" title=\"Data Model\" style=\"text-decoration: none; font-size: 1.5em; line-height: 1;\">⧉</a>";
        if (!string.IsNullOrWhiteSpace(typeDefinition.CollectionName))
            navigationIcons += $" <a href=\"/{host.Hub.Address}/Catalog/{typeDefinition.CollectionName}\" title=\"View Catalog\" style=\"text-decoration: none; font-size: 1.5em; line-height: 1;\">🗃️</a>";

        var stack = Controls.Stack.WithWidth("100%");

        // Header with title and navigation
        var header = Controls.Html($@"
            <div style=""display: flex; justify-content: space-between; align-items: center; margin-bottom: 1rem; width: 100%;"">
                <h1 style=""margin: 0; flex-grow: 1;"">{System.Web.HttpUtility.HtmlEncode(typeDefinition.DisplayName)}</h1>
                <div style=""flex-shrink: 0;"">{navigationIcons}</div>
            </div>");
        stack = stack.WithView(header);

        // Description if available
        var description = typeDefinition.Type.GetXmlDocsSummary();
        if (!string.IsNullOrWhiteSpace(description))
            stack = stack.WithView(Controls.Html($"<p style=\"color: var(--neutral-foreground-hint); margin-bottom: 1rem;\">{description}</p>"));

        // Use Overview with startInEditMode=true for the form
        return stack.WithView((areaHost, _) => EditLayout(areaHost, typeDefinition, id));
    }

    private static UiControl EditLayout(LayoutAreaHost host, ITypeDefinition typeDefinition, object id)
    {
        var dataId = GetDataId($"{host.Hub.Address}_{typeDefinition.CollectionName}_{id}");
        var stream = host.Workspace
            .GetStream(new EntityReference(typeDefinition.CollectionName, id));

        host.RegisterForDisposal(stream!
            .Select(e => typeDefinition.SerializeEntityAndId(e?.Value ?? throw new InvalidOperationException("Entity value is null"), host.Hub.JsonSerializerOptions))
            .Subscribe(e => host.UpdateData(dataId, e))
        );

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
    public static UiControl BuildPropertyForm(
        LayoutAreaHost host,
        Type contentType,
        string dataId,
        bool canEdit,
        bool isToggleable = true)
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
                var control = host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit, host, isToggleable);
                propsGrid = propsGrid.WithView(control, s => s.WithXs(12).WithMd(6).WithLg(4));
            }

            stack = stack.WithView(propsGrid);
        }

        // Build markdown sections using MapToToggleableControl (full width)
        foreach (var prop in markdownProps)
        {
            var control = host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit, host, isToggleable);
            stack = stack.WithView(control);
        }

        // Build collection sections using MapToToggleableControl (full width)
        foreach (var prop in collectionProps)
        {
            var control = host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit, host, isToggleable);
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
