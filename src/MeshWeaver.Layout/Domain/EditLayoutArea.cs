using System.ComponentModel;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Reflection;

namespace MeshWeaver.Layout.Domain;

public static class EditLayoutArea
{
    public static UiControl Edit(LayoutAreaHost host, ITypeDefinition typeDefinition, object id, RenderingContext ctx)
    {
        // Title with right-aligned navigation icons
        var navigationIcons = $"<a href=\"/{host.Hub.Address}/DataModel/{typeDefinition.Type.Name}\" title=\"Data Model\" style=\"text-decoration: none; font-size: 2em; line-height: 1;\">⧉</a>";
        if (!string.IsNullOrWhiteSpace(typeDefinition.CollectionName))
            navigationIcons += $" <a href=\"/{host.Hub.Address}/Catalog/{typeDefinition.CollectionName}\" title=\"View Catalog\" style=\"text-decoration: none; font-size: 2em; line-height: 1;\">🗃️</a>";

        var titleWithNav = $"<div style=\"display: flex !important; justify-content: space-between !important; align-items: center !important; margin-bottom: 1rem; width: 100%;\"><h1 style=\"margin: 0; flex-grow: 1;\">{typeDefinition.DisplayName}</h1><div style=\"flex-shrink: 0;\">{navigationIcons}</div></div>";
        var ret = new StackControl()
            .WithView(Controls.Html(titleWithNav));

        var description = typeDefinition.Type.GetXmlDocsSummary();
        if (!string.IsNullOrWhiteSpace(description))
            ret = ret.WithView(Controls.Html($"<p>{description}</p>"));

        return ret.WithView((areaHost, _) => EditLayout(areaHost, typeDefinition, id));
    }

    private static UiControl EditLayout(LayoutAreaHost host, ITypeDefinition typeDefinition, object id)
    {
        var dataId = Guid.NewGuid().AsString();
        var stream = host.Workspace
            .GetStream(new EntityReference(typeDefinition.CollectionName, id));

        host.RegisterForDisposal(stream!
            .Select(e => typeDefinition.SerializeEntityAndId(e?.Value ?? throw new InvalidOperationException("Entity value is null"), host.Hub.JsonSerializerOptions))
            .Subscribe(e => host.UpdateData(dataId, e))
        );

        return typeDefinition.Type.GetProperties()
            .Aggregate(new EditFormControl { DataContext = LayoutAreaReference.GetDataPointer(dataId) }, (form, property) =>
                MapToEditFormControl(form, property, host));
    }

    private static EditFormControl MapToEditFormControl(EditFormControl form, PropertyInfo property, LayoutAreaHost host)
    {
        return host.Hub.ServiceProvider.MapToControl<EditFormControl, EditFormSkin>(form, property);
    }

    /// <summary>
    /// Builds the property form with grid for regular properties and separate sections for markdown.
    /// Uses MapToToggleableControl for readonly/edit toggle functionality.
    /// </summary>
    public static UiControl Overview(
        LayoutAreaHost host,
        Type contentType,
        string dataId,
        bool canEdit)
    {
        // Get browsable properties (skip Title - shown in header)
        var properties = contentType.GetProperties()
            .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .Where(p => !IsTitleProperty(p.Name))
            .ToList();

        // Separate properties into regular vs markdown (SeparateEditView)
        var regularProps = properties
            .Where(p => p.GetCustomAttribute<UiControlAttribute>()?.SeparateEditView != true)
            .ToList();

        var markdownProps = properties
            .Where(p => p.GetCustomAttribute<UiControlAttribute>()?.SeparateEditView == true)
            .ToList();

        var stack = Controls.Stack.WithWidth("100%");

        // Build grid for regular properties using MapToToggleableControl
        if (regularProps.Count > 0)
        {
            var propsGrid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

            foreach (var prop in regularProps)
            {
                var control = host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit, host);
                propsGrid = propsGrid.WithView(control, s => s.WithXs(12).WithMd(6).WithLg(4));
            }

            stack = stack.WithView(propsGrid);
        }

        // Build markdown sections using MapToToggleableControl
        foreach (var prop in markdownProps)
        {
            var control = host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit, host);
            stack = stack.WithView(control);
        }

        return stack;
    }

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
