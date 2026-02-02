using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
    /// Builds the property overview for a MeshNode, showing read-only views with click-to-edit.
    /// </summary>
    public static UiControl BuildPropertyOverview(LayoutAreaHost host, MeshNode node)
    {
        var nodePath = node.Namespace ?? host.Hub.Address.ToString();

        // Handle Content which could be null, JsonElement, or already deserialized typed object
        var instance = node.Content;
        if (instance == null)
            return Controls.Stack;

        if (instance is JsonElement je)
            instance = JsonSerializer.Deserialize<object>(je.GetRawText(), host.Hub.JsonSerializerOptions)!;

        var contentType = instance.GetType();

        // 2. Check access permissions
        var canEdit = CheckEditAccess(host, node);

        // 3. Set up workspace stream for bidirectional data binding (DomainDetails pattern)
        var dataId = EditLayoutArea.GetDataId(nodePath);
        var typeDefinition = host.Workspace.DataContext.TypeRegistry.GetTypeDefinition(contentType);
        if (typeDefinition is null)
            throw new InvalidOperationException($"Type definition not found for content type {contentType.FullName}");

        var entityId = node.Id;
        var stream = host.Workspace.GetStream(new EntityReference(typeDefinition.CollectionName, entityId));
        if (stream != null)
        {
            // Subscribe to workspace stream - this handles bidirectional sync automatically
            host.RegisterForDisposal(stream
                .Where(e => e?.Value != null)
                .Subscribe(e => host.UpdateData(dataId, e!.Value!))
            );
        }
        else
        {
            // Fallback: store JsonElement directly
            host.UpdateData(dataId, instance);
        }

        // 4. Setup auto-save to persist changes via DataChangeRequest
        if (canEdit)
        {
            SetupAutoSave(host, dataId, instance, node);
        }

        // 5. Build property form with readonly/edit toggle
        return EditLayoutArea.Overview(host, contentType, dataId, canEdit);
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
}
