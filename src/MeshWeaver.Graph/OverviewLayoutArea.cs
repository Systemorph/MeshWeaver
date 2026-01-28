using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Reflection;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Builds an overview layout for MeshNode content with read-only display and click-to-edit.
/// Shows read-only views by default, click switches to edit mode, blur auto-switches back.
/// Markdown properties are handled separately with full width and Done button.
/// Uses workspace stream pattern (like DomainDetails) for automatic bidirectional data binding.
/// </summary>
public static class OverviewLayoutArea
{
    /// <summary>
    /// Gets the consistent data ID for a node path. Used by both header and property overview.
    /// </summary>
    public static string GetDataId(string nodePath) => $"content_{nodePath.Replace("/", "_")}";

    /// <summary>
    /// Builds the property overview for a MeshNode, showing read-only views with click-to-edit.
    /// </summary>
    public static UiControl BuildPropertyOverview(LayoutAreaHost host, MeshNode node)
    {
        var nodePath = node.Namespace ?? host.Hub.Address.ToString();

        if (node.Content is not JsonElement jsonContent)
            return Controls.Stack;

        // 1. Get $type from JsonElement
        if (!jsonContent.TryGetProperty("$type", out var typeProperty))
            return Controls.Stack;

        var typeName = typeProperty.GetString();
        var typeRegistry = host.Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var contentType = typeRegistry.GetType(typeName!);

        if (contentType == null)
            return Controls.Stack;

        // 2. Check access permissions
        var canEdit = CheckEditAccess(host, node);

        // 3. Set up workspace stream for bidirectional data binding (DomainDetails pattern)
        var dataId = GetDataId(nodePath);
        var typeDefinition = host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(contentType);

        if (typeDefinition != null && !string.IsNullOrEmpty(typeDefinition.CollectionName))
        {
            var entityId = GetEntityId(jsonContent, contentType, typeRegistry);
            if (entityId != null)
            {
                var stream = host.Workspace.GetStream(new EntityReference(typeDefinition.CollectionName, entityId));
                if (stream != null)
                {
                    // Subscribe to workspace stream - this handles bidirectional sync automatically
                    host.RegisterForDisposal(stream
                        .Where(e => e?.Value != null)
                        .Select(e => typeDefinition.SerializeEntityAndId(e!.Value!, host.Hub.JsonSerializerOptions))
                        .Where(e => e != null)
                        .Subscribe(e => host.UpdateData(dataId, e!))
                    );
                }
                else
                {
                    // Fallback: store JsonElement directly
                    host.UpdateData(dataId, jsonContent);
                }
            }
            else
            {
                host.UpdateData(dataId, jsonContent);
            }
        }
        else
        {
            host.UpdateData(dataId, jsonContent);
        }

        // 4. Setup auto-save to persist changes via DataChangeRequest
        if (canEdit)
        {
            SetupAutoSave(host, dataId, jsonContent, node);
        }

        // 5. Build property form with readonly/edit toggle
        return BuildPropertyForm(host, contentType, dataId, nodePath, canEdit);
    }

    /// <summary>
    /// Builds the property form with grid for regular properties and separate sections for markdown.
    /// Uses MapToToggleableControl for readonly/edit toggle functionality.
    /// </summary>
    private static UiControl BuildPropertyForm(
        LayoutAreaHost host,
        Type contentType,
        string dataId,
        string nodePath,
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
        LayoutAreaHost host,
        MeshNode node,
        string dataId,
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
        LayoutAreaHost host,
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

    private static object? GetEntityId(JsonElement jsonContent, Type contentType, ITypeRegistry typeRegistry)
    {
        var keyProperty = contentType.GetProperties()
            .FirstOrDefault(p => p.HasAttribute<KeyAttribute>());

        if (keyProperty == null)
        {
            keyProperty = contentType.GetProperty("Id") ?? contentType.GetProperty("ID");
        }

        if (keyProperty == null)
            return null;

        var propName = keyProperty.Name.ToCamelCase();
        if (propName != null && jsonContent.TryGetProperty(propName, out var idElement))
        {
            return idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString(),
                JsonValueKind.Number => idElement.TryGetInt64(out var longVal) ? longVal : idElement.GetDouble(),
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// Sets up auto-save: watches local data stream for changes and persists via DataChangeRequest.
    /// Follows the exact pattern from InlineEditingTest.cs but for MeshNode content.
    /// </summary>
    private static void SetupAutoSave(
        LayoutAreaHost host,
        string dataId,
        JsonElement originalContent,
        MeshNode node)
    {
        var initialJson = originalContent.GetRawText();

        host.RegisterForDisposal($"autosave_{dataId}",
            host.Stream.GetDataStream<JsonElement>(dataId)
                .Debounce(TimeSpan.FromMilliseconds(300))
                .Subscribe(async updatedContent =>
                {
                    if (updatedContent.ValueKind == JsonValueKind.Undefined)
                        return;

                    var currentJson = updatedContent.GetRawText();

                    if (currentJson == initialJson)
                        return;

                    // Update initial to prevent re-sending
                    initialJson = currentJson;

                    // Create updated MeshNode with new content
                    var updatedNode = node with { Content = updatedContent };

                    // Issue DataChangeRequest to persist the change
                    await host.Hub.AwaitResponse<DataChangeResponse>(
                        new DataChangeRequest().WithUpdates(updatedNode),
                        o => o.WithTarget(host.Hub.Address));
                }));
    }

    private static bool CheckEditAccess(LayoutAreaHost host, MeshNode node)
    {
        var baseEditable = true;

        var securityService = host.Hub.ServiceProvider.GetService<ISecurityService>();
        if (securityService != null)
        {
            var nodePath = node.Path;
            try
            {
                var permissions = securityService.GetEffectivePermissionsAsync(nodePath).GetAwaiter().GetResult();
                return permissions.HasFlag(Permission.Update);
            }
            catch
            {
                return baseEditable;
            }
        }

        return baseEditable;
    }

    private static bool IsTitleProperty(string name) =>
        name.Equals("Title", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DisplayName", StringComparison.OrdinalIgnoreCase);
}
