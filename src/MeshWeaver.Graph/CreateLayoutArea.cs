using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
// IIconGenerator is consumed via DI; interface lives in MeshWeaver.Mesh.Services.
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for creating new mesh nodes.
/// Shows a unified "Create New" form with type autocomplete, namespace, name, and id fields.
/// On submit it persists ONE <see cref="MeshNodeState.Active"/> node via a single
/// <c>CreateNode</c> (through the access-control pipeline) and navigates to the new node's Edit
/// area — no transient placeholder is ever written to storage.
/// </summary>
public static class CreateLayoutArea
{
    /// <summary>
    /// Returns the Create menu item if the user has Create permission.
    /// When on a NodeType definition page, passes the type as query parameter.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, MeshNode? node, Permission perms)
    {
        if (!perms.HasFlag(Permission.Create))
            return null;

        var createQs = node?.NodeType == MeshNode.NodeTypePath
            ? $"type={Uri.EscapeDataString(hubPath)}"
            : null;
        return new("Create", MeshNodeLayoutAreas.CreateNodeArea,
            RequiredPermission: Permission.Create, Order: 0,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.CreateNodeArea, createQs));
    }
    /// <summary>
    /// Main entry point for the Create layout area. Shows the unified "Create New" form with the
    /// type picker (or a NodeType-specific <see cref="NodeTypeDefinition.BuildCreate"/> override,
    /// e.g. Thread's chat composer). Submitting the form creates the node and navigates to its
    /// Edit area; nothing is persisted until that single <c>CreateNode</c>.
    /// </summary>
    public static IObservable<UiControl?> Create(LayoutAreaHost host, RenderingContext _)
    {
        var currentPath = host.Hub.Address.ToString();

        // Per-type Create override. A NodeType can inject its own create control via
        // NodeTypeDefinition.BuildCreate — e.g. Thread opens the new-chat composer instead
        // of the generic form (the instance is created on submit), and a type could equally
        // return a control that refuses the create. The requested type is resolved from BOTH
        // the ?type= (singular) and ?types= (plural, the MeshSearch create button's
        // restriction param) shapes, so the override fires regardless of which "+" was used.
        var requestedType = ResolveRequestedType(host);
        if (!string.IsNullOrEmpty(requestedType)
            && host.Hub.ServiceProvider.FindStaticNode(requestedType)?.Content is NodeTypeDefinition typeDef
            && typeDef.BuildCreate is { } buildCreate)
        {
            var ns = host.GetQueryStringParamValue("namespace") ?? currentPath;
            // A null control from the override means "no opinion" → default form.
            return buildCreate(host, ns).Take(1).SelectMany(control =>
                control is not null
                    ? Observable.Return<UiControl?>(control)
                    : BuildDefaultCreate(host, currentPath));
        }

        return BuildDefaultCreate(host, currentPath);
    }

    /// <summary>
    /// Resolves the NodeType the "+"/Create action targets, honouring both URL shapes the
    /// create entry points use: <c>?type=Thread</c> (singular — the catalog button and
    /// NodeType create links) and <c>?types=Thread</c> (plural — the MeshSearch create
    /// button's restriction param). The plural form resolves to an override only when it
    /// names a single type. Returns <c>null</c> when no specific type was requested.
    /// </summary>
    private static string? ResolveRequestedType(LayoutAreaHost host)
    {
        var type = host.GetQueryStringParamValue("type");
        if (!string.IsNullOrEmpty(type))
            return type;
        var types = host.GetQueryStringParamValue("types")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return types is { Length: 1 } ? types[0] : null;
    }

    /// <summary>
    /// The default Create UI: the generic "Create New" form. Used when no NodeType-specific
    /// <see cref="NodeTypeDefinition.BuildCreate"/> applies (or one yielded <c>null</c>).
    /// <para>The form runs on the PARENT node's hub and never writes a placeholder/transient
    /// node. On submit it builds one <see cref="MeshNodeState.Active"/> node and persists it with
    /// a single <c>CreateNode</c> (flowing through the CreateNodeRequest access-control pipeline),
    /// then navigates to the new node's Edit area — the owning hub knows the node's content type
    /// and materialises the type-specific content editor there.</para>
    /// </summary>
    private static IObservable<UiControl?> BuildDefaultCreate(LayoutAreaHost host, string currentPath)
    {
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Take(1).Select(nodes =>
            (UiControl?)BuildCreateNewForm(host, nodes, currentPath));
    }

    private static void ShowErrorDialog(UiActionContext ctx, string title, string message)
    {
        var errorDialog = Controls.Dialog(
            Controls.Markdown($"**{title}:**\n\n{message}"),
            title
        ).WithSize("M").WithClosable(true);
        ctx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
    }

    /// <summary>
    /// Renders an icon value as a 48x48 preview. Supports three forms:
    /// inline SVG markup, an http(s) or /static URL, and a FluentIcon name.
    /// </summary>
    internal static UiControl BuildIconPreview(string icon)
    {
        const string boxStyle = "width:48px;height:48px;display:flex;align-items:center;justify-content:center;border:1px solid var(--neutral-stroke-rest);border-radius:6px;color:var(--neutral-foreground-rest);";
        if (icon.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return Controls.Html($"<div style=\"{boxStyle}\">{icon}</div>");
        if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase) || icon.StartsWith("/")
            || icon.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            return Controls.Html($"<div style=\"{boxStyle}\"><img src=\"{System.Web.HttpUtility.HtmlAttributeEncode(icon)}\" style=\"max-width:32px;max-height:32px;\" /></div>");
        return Controls.Html($"<div style=\"{boxStyle}\"><span style=\"font-size:12px;\">{System.Web.HttpUtility.HtmlEncode(icon)}</span></div>");
    }

    /// <summary>
    /// Handles the Create form's "Regenerate" icon click: reads Name+Description from the
    /// form dictionary, invokes IIconGenerator, and writes the resulting SVG back into the
    /// form's "icon" slot so the preview refreshes live.
    /// </summary>
    private static Task RegenerateFormIcon(UiActionContext actx, string formId)
    {
        var generator = actx.Host.Hub.ServiceProvider.GetService<IIconGenerator>();
        if (generator == null)
        {
            ShowErrorDialog(actx, "Regenerate Icon",
                "Icon generator service is not registered. Call AddAgentChatServices().");
            return Task.CompletedTask;
        }

        // Patch specific keys onto the LATEST form snapshot (re-read each time) so a field the
        // user edits while the (multi-second) icon round runs is not clobbered by a stale copy.
        void PatchForm(Action<Dictionary<string, object?>> mutate) =>
            actx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId).Take(1).Subscribe(form =>
            {
                var next = form is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(form);
                mutate(next);
                actx.Host.UpdateData(formId, next);
            });

        // Single reactive chain (no nested Subscribe): read the form → generate → write back.
        actx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
            .Take(1)
            .SelectMany(form =>
            {
                var currentName = form?.GetValueOrDefault("name")?.ToString() ?? "";
                // Prefer the dedicated icon description; fall back to the node description.
                var iconDesc = form?.GetValueOrDefault("iconDescription")?.ToString();
                if (string.IsNullOrWhiteSpace(iconDesc))
                    iconDesc = form?.GetValueOrDefault("description")?.ToString();
                if (string.IsNullOrWhiteSpace(currentName) && string.IsNullOrWhiteSpace(iconDesc))
                {
                    ShowErrorDialog(actx, "Regenerate Icon",
                        "Enter a Name or Description first — the agent uses those to craft the icon.");
                    return Observable.Empty<string>();
                }
                // Spinner up immediately so the button gives feedback during the agent turn.
                PatchForm(f => f["iconGenerating"] = true);
                // Bound the round: a missing / non-responding utility-tier model must surface as a
                // VISIBLE error, never an indefinite silent hang (ErrorPropagationAndWedges) — the
                // "pressing the button does nothing" report.
                return generator.GenerateSvgAsync(currentName, iconDesc)
                    .Timeout(TimeSpan.FromSeconds(90));
            })
            .Subscribe(
                svg => PatchForm(f => { f["icon"] = svg; f["iconGenerating"] = false; }),
                ex =>
                {
                    PatchForm(f => f["iconGenerating"] = false);
                    ShowErrorDialog(actx, "Icon Generation Failed",
                        ex is TimeoutException
                            ? "The icon agent didn't respond in time. Make sure a utility-tier model is configured for icon generation."
                            : ex.Message);
                });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the Create form's "Generate image" click: builds a prompt from Name + Icon description,
    /// invokes <see cref="IImageGenerator"/> (a configured image model — Azure OpenAI Images, OpenAI, or a
    /// local Stable-Diffusion endpoint), and writes the resulting PNG back into the form's "icon" slot as a
    /// <c>data:</c> URI. Inline storage is deliberate here: the node does not exist yet, so a data URI is
    /// self-contained and renders directly (a content-collection URL is a follow-up for large images).
    /// </summary>
    private static Task RegenerateFormImage(UiActionContext actx, string formId)
    {
        var generator = actx.Host.Hub.ServiceProvider.GetService<IImageGenerator>();
        if (generator == null)
        {
            ShowErrorDialog(actx, "Generate Image",
                "Image generator service is not registered. Call AddAgentChatServices().");
            return Task.CompletedTask;
        }

        void PatchForm(Action<Dictionary<string, object?>> mutate) =>
            actx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId).Take(1).Subscribe(form =>
            {
                var next = form is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(form);
                mutate(next);
                actx.Host.UpdateData(formId, next);
            });

        actx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
            .Take(1)
            .SelectMany(form =>
            {
                var name = form?.GetValueOrDefault("name")?.ToString() ?? "";
                // Prefer the dedicated icon description; fall back to the node description.
                var iconDesc = form?.GetValueOrDefault("iconDescription")?.ToString();
                if (string.IsNullOrWhiteSpace(iconDesc))
                    iconDesc = form?.GetValueOrDefault("description")?.ToString();
                var prompt = string.Join(". ", new[] { name, iconDesc }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    ShowErrorDialog(actx, "Generate Image",
                        "Enter a Name or an Icon description first — the image model uses those as the prompt.");
                    return Observable.Empty<GeneratedImage>();
                }
                PatchForm(f => f["iconGenerating"] = true);
                // Bound the round: a missing / non-responding image endpoint must surface as a VISIBLE
                // error, never an indefinite silent hang (ErrorPropagationAndWedges).
                return generator.GenerateImageAsync(prompt)
                    .Timeout(TimeSpan.FromSeconds(120));
            })
            .Subscribe(
                image =>
                {
                    var dataUrl = $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Data)}";
                    PatchForm(f => { f["icon"] = dataUrl; f["iconGenerating"] = false; });
                },
                ex =>
                {
                    PatchForm(f => f["iconGenerating"] = false);
                    ShowErrorDialog(actx, "Image Generation Failed",
                        ex is TimeoutException
                            ? "The image model didn't respond in time. Check the image model / endpoint configuration."
                            : ex.Message);
                });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the unified "Create New" form:
    /// Namespace (MeshNodePicker), Type (MeshNodePicker with Items), Name, Id, Create/Cancel.
    /// Synchronous — defaults are resolved from the already-available nodes array.
    /// </summary>
    private static UiControl BuildCreateNewForm(
        LayoutAreaHost host,
        MeshNode[] nodes,
        string parentPath)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILogger<LayoutAreaHost>>();
        var meshConfiguration = host.Hub.ServiceProvider.GetRequiredService<MeshConfiguration>();
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 24px;");
        stack = stack.WithView(Controls.H2("Create New").WithStyle("margin: 0 0 24px 0;"));

        // 1. Resolve defaults from the current node
        var currentNode = nodes.FirstOrDefault(n => n.Path == parentPath);

        // Non-NodeType: pre-select self as Location (matches Associated-catalog behavior).
        // NodeType: start from the current path; the NodeTypeDefinition's DefaultNamespace
        // (resolved below) will override if configured.
        var defaultNamespace = parentPath;

        var defaultType = currentNode?.NodeType == MeshNode.NodeTypePath
            ? parentPath
            : "Markdown";

        // Override from query string (e.g. Create?type=Organization)
        var typeOverride = host.GetQueryStringParamValue("type");
        if (!string.IsNullOrEmpty(typeOverride))
            defaultType = typeOverride;

        // Override namespace from query string (e.g. Create?namespace=ACME/Marketing)
        var namespaceOverride = host.GetQueryStringParamValue("namespace");
        if (namespaceOverride != null)
            defaultNamespace = namespaceOverride;

        // Parse restriction query params (e.g. ?types=X,Y&namespaces=A,B)
        // Note: namespaces param uses != null to distinguish "absent" from "empty value" (root)
        var typesParam = host.GetQueryStringParamValue("types");
        var namespacesParam = host.GetQueryStringParamValue("namespaces");
        var restrictedTypes = !string.IsNullOrEmpty(typesParam)
            ? typesParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;
        string[]? restrictedNamespaces = namespacesParam != null
            ? namespacesParam.Split(',', StringSplitOptions.TrimEntries)
            : null;
        // URL-param namespace restriction (if any), captured BEFORE the default type's
        // RestrictedToNamespaces is folded in below. The reactive namespace field uses this as
        // the fixed restriction and otherwise derives the restriction from the *selected* type,
        // so partition objects lock to root regardless of how the type was chosen.
        var urlRestrictedNamespaces = restrictedNamespaces;

        // If types restricted to single entry, use it as default
        if (restrictedTypes is { Length: 1 })
            defaultType = restrictedTypes[0];

        // When type is known, look up its NodeTypeDefinition for namespace restrictions
        var knownType = restrictedTypes is { Length: 1 } ? restrictedTypes[0] : defaultType;
        if (!string.IsNullOrEmpty(knownType))
        {
            var typeNode = host.Hub.ServiceProvider.FindStaticNode(knownType);
            var typeDef = typeNode.ContentAs<NodeTypeDefinition>(host.Hub.JsonSerializerOptions);

            // (A type's own create flow — e.g. Thread's chat composer — is handled earlier
            // in Create() via NodeTypeDefinition.BuildCreate; such a type never reaches
            // this generic form.)

            // Apply DefaultNamespace from NodeTypeDefinition (pre-selects but doesn't restrict)
            if (typeDef?.DefaultNamespace != null)
                defaultNamespace = typeDef.DefaultNamespace;

            // Apply RestrictedToNamespaces if not already overridden by URL param
            if (restrictedNamespaces == null && typeDef?.RestrictedToNamespaces is { Count: > 0 })
                restrictedNamespaces = typeDef.RestrictedToNamespaces.ToArray();
        }

        // If namespaces restricted to single entry, use it as default
        if (restrictedNamespaces is { Length: 1 })
            defaultNamespace = restrictedNamespaces[0];

        // 2. Build fixed creatable type Items (alphabetical)
        var creatableTypeNodes = host.Hub.ServiceProvider.EnumerateStaticNodes()
            .Where(n => n.ExcludeFromContext?.Contains("create") != true)
            .OrderBy(n => n.Name ?? n.Path)
            .ToArray();

        // Resolve the default icon from the selected type's registration so the preview
        // can show something meaningful before the user clicks "Regenerate".
        var defaultTypeIcon = creatableTypeNodes.FirstOrDefault(n => n.Path == defaultType)?.Icon;

        // 3. Form data
        var formId = $"create_form_{Guid.NewGuid().AsString()}";
        host.UpdateData(formId, new Dictionary<string, object?>
        {
            ["namespace"] = defaultNamespace,
            ["type"] = defaultType,
            ["name"] = "",
            ["id"] = "",
            ["description"] = "",
            ["iconDescription"] = "",
            ["icon"] = defaultTypeIcon ?? ""
        });
        var dataContext = LayoutAreaReference.GetDataPointer(formId);

        // 4. Name field (required) — primary input, Id auto-derives from it.
        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("name"))
        {
            Label = "Name *",
            Placeholder = "Enter a name...",
            Required = true,
            Immediate = true,
            DataContext = dataContext
        }.WithStyle("width: 100%; margin-bottom: 16px;"));

        // 5. Id field (optional — auto-generated from Name at submit time if left empty)
        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("id"))
        {
            Label = "Id (optional)",
            Placeholder = "Leave empty to auto-generate from name",
            Immediate = true,
            DataContext = dataContext
        }.WithStyle("width: 100%; margin-bottom: 4px;"));
        stack = stack.WithView(Controls.Body("Leave empty to auto-generate from the name (e.g. \"My Article\" → \"MyArticle\")")
            .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; margin-bottom: 16px;"));

        // 6. Type picker (or readonly label if restricted to single value)
        if (restrictedTypes is { Length: 1 })
        {
            // Single type restriction — show readonly info
            var typeNode = creatableTypeNodes.FirstOrDefault(n => n.Path == restrictedTypes[0]);
            var typeLabel = typeNode?.Name ?? restrictedTypes[0];
            stack = stack.WithView(Controls.Stack
                .WithWidth("100%")
                .WithStyle("margin-bottom: 16px;")
                .WithView(Controls.Body("Type").WithStyle("font-weight: 600; margin-bottom: 4px;"))
                .WithView(Controls.Body(typeLabel).WithStyle("color: var(--neutral-foreground-rest);")));
        }
        else if (restrictedTypes is { Length: > 1 })
        {
            // Multiple type restriction — filtered picker
            var filteredTypeNodes = creatableTypeNodes
                .Where(n => restrictedTypes.Contains(n.Path))
                .ToArray();
            stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("type"))
            {
                Label = "Type *",
                Required = true,
                Placeholder = "Select a type...",
                DataContext = dataContext
            }.WithItems(filteredTypeNodes)
             .WithMaxResults(15)
             .WithStyle("width: 100%; margin-bottom: 16px;"));
        }
        else
        {
            // No restriction — full picker. Queries cover (a) root-level registered NodeTypes
            // and (b) any NodeType defined within the current namespace or its ancestors.
            // context:create excludes NodeTypes that opt out of creation (e.g. Release,
            // Notification) — the WHERE on Items already filters those, but the QUERY path
            // must too or the excluded types leak back in via query results.
            var ancestorQuery = string.IsNullOrEmpty(parentPath)
                ? "namespace: nodeType:NodeType context:create"
                : $"namespace:{parentPath} nodeType:NodeType scope:selfAndAncestors context:create";
            stack = stack.WithView(new MeshNodePickerControl(new JsonPointerReference("type"))
            {
                Label = "Type *",
                Required = true,
                Placeholder = "Select a type...",
                DataContext = dataContext
            }.WithItems(creatableTypeNodes)
             .WithQueries("namespace: nodeType:NodeType context:create", ancestorQuery)
             .WithMaxResults(15)
             .WithStyle("width: 100%; margin-bottom: 16px;"));
        }

        // 7. Namespace — REACTIVE on the selected type. Partition objects (Space, User;
        //    NodeTypeDefinition.RestrictedToNamespaces = [""]) live ONLY at root, so the field
        //    locks to a read-only "Root (top-level)" label and can't be retargeted to a
        //    user/space namespace — whether the type was preset or picked from the dropdown.
        //    Keyed on the TYPE value only (DistinctUntilChanged) so it does NOT re-render while
        //    the user types Name/Description.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<Dictionary<string, object?>>(formId)
            .Select(form => form?.GetValueOrDefault("type")?.ToString() ?? "")
            .DistinctUntilChanged()
            .Select(selectedType =>
            {
                // URL-param restriction wins; otherwise derive it from the SELECTED type.
                var effective = urlRestrictedNamespaces;
                if (effective == null && !string.IsNullOrEmpty(selectedType)
                    && host.Hub.ServiceProvider.FindStaticNode(selectedType)?.Content is NodeTypeDefinition selDef
                    && selDef.RestrictedToNamespaces is { Count: > 0 } r)
                    effective = r.ToArray();
                return (UiControl)BuildNamespaceControl(effective, dataContext);
            }));

        // 8. Description — free-text context. Also used as the seed when regenerating an icon.
        stack = stack.WithView(new TextAreaControl(new JsonPointerReference("description"))
        {
            Label = "Description",
            Placeholder = "Briefly describe what you're creating. Used to seed icon generation.",
            Immediate = true,
            DataContext = dataContext
        }.WithRows(3).WithStyle("width: 100%; margin-bottom: 16px;"));

        // 8b. Icon description — an OPTIONAL, dedicated seed for the avatar/icon generator,
        //     separate from the node Description so the user can steer the icon's imagery
        //     ("a friendly robot holding a compass") without changing what the node is about.
        //     Falls back to the Description above when left empty.
        stack = stack.WithView(new TextAreaControl(new JsonPointerReference("iconDescription"))
        {
            Label = "Icon description (optional)",
            Placeholder = "Describe the icon/avatar to generate — e.g. \"a friendly robot holding a compass\". Falls back to the description above.",
            Immediate = true,
            DataContext = dataContext
        }.WithRows(2).WithStyle("width: 100%; margin-bottom: 16px;"));

        // 9. Icon: "Icon" label, live preview, Regenerate button.
        // Preview is data-bound so it reflects live updates (default from the chosen type,
        // a regenerated SVG from the Node Initializer agent, or a spinner while generating).
        stack = stack.WithView(Controls.Body("Icon")
            .WithStyle("font-weight: 600; display: block; margin-bottom: 6px;"));
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("align-items: center; margin-bottom: 24px;")
            .WithView((h, _) => h.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                .Select(form =>
                {
                    // "iconGenerating" flips true while the agent round runs so the preview shows
                    // activity — the round is a full agent turn (seconds), so without this the
                    // Regenerate button looks dead. Value survives a JSON round-trip as either a
                    // CLR bool ("True") or a JsonElement ("true"), so compare case-insensitively.
                    if (string.Equals(form?.GetValueOrDefault("iconGenerating")?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                        return Controls.Progress("Generating…", 0);
                    var icon = form?.GetValueOrDefault("icon")?.ToString() ?? "";
                    return string.IsNullOrEmpty(icon)
                        ? Controls.Html("<div style=\"width:48px;height:48px;border:1px dashed var(--neutral-stroke-rest);border-radius:6px;\"></div>")
                        : BuildIconPreview(icon);
                }))
            .WithView(Controls.Button("Regenerate")
                .WithAppearance(Appearance.Neutral)
                .WithIconStart(FluentIcons.Sparkle())
                .WithClickAction(actx => RegenerateFormIcon(actx, formId)))
            // "Generate image" — a real raster avatar via a configured image model (IImageGenerator),
            // as opposed to "Regenerate" which draws a vector SVG through the NodeInitializer agent.
            .WithView(Controls.Button("Generate image")
                .WithAppearance(Appearance.Neutral)
                .WithIconStart(FluentIcons.Image())
                .WithClickAction(actx => RegenerateFormImage(actx, formId))));

        // 10. Button row: Cancel on left, Create on right
        var cancelUrl = MeshNodeLayoutAreas.BuildUrl(parentPath, MeshNodeLayoutAreas.OverviewArea);
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("margin-top: 24px; justify-content: flex-start;");

        buttonRow = buttonRow.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Neutral)
            .WithNavigateToHref(cancelUrl));

        buttonRow = buttonRow.WithView(Controls.Button("Create")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(actx =>
            {
                // Reactive click — read form, CreateNode (completes after the create response),
                // then navigate to the new node's Edit area. No await on the click path
                // (AsynchronousCalls.md).
                actx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                    .Take(1)
                    .Subscribe(formValues =>
                    {
                        var ns = formValues.GetValueOrDefault("namespace")?.ToString()?.Trim() ?? "";
                        var selectedType = formValues.GetValueOrDefault("type")?.ToString()?.Trim();
                        // Partition objects (Space, User) live ONLY at root — force "" no matter
                        // what the namespace field held. Mirrors OwnsPartitionProvisioningValidator,
                        // which rejects a partition-owning create with a non-empty namespace.
                        if (!string.IsNullOrEmpty(selectedType)
                            && host.Hub.ServiceProvider.FindStaticNode(selectedType)?.Content is NodeTypeDefinition stDef
                            && stDef.OwnsPartition)
                            ns = "";
                        var name = formValues.GetValueOrDefault("name")?.ToString()?.Trim();
                        var id = formValues.GetValueOrDefault("id")?.ToString()?.Trim();
                        var description = formValues.GetValueOrDefault("description")?.ToString()?.Trim();
                        var icon = formValues.GetValueOrDefault("icon")?.ToString()?.Trim();

                        if (string.IsNullOrWhiteSpace(selectedType))
                        {
                            ShowErrorDialog(actx, "Validation Error", "Type is required.");
                            return;
                        }
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            ShowErrorDialog(actx, "Validation Error", "Name is required.");
                            return;
                        }

                        if (string.IsNullOrWhiteSpace(id))
                            id = GenerateIdFromName(name);

                        string nodePath;
                        if (meshConfiguration.IsSatelliteNodeType(selectedType))
                        {
                            var typeSegment = $"_{selectedType}";
                            nodePath = string.IsNullOrEmpty(ns) ? $"{typeSegment}/{id}" : $"{ns}/{typeSegment}/{id}";
                        }
                        else
                        {
                            nodePath = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
                        }

                        var nodeFactory = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();

                        var typeRegistration = host.Hub.ServiceProvider.FindStaticNode(selectedType);

                        var newNode = MeshNode.FromPath(nodePath) with
                        {
                            Name = name!.Trim(),
                            Description = string.IsNullOrEmpty(description) ? null : description,
                            NodeType = selectedType,
                            Icon = string.IsNullOrEmpty(icon) ? typeRegistration?.Icon : icon,
                            Category = typeRegistration?.Category,
                            State = MeshNodeState.Active
                        };

                        logger?.LogInformation("Creating node at {NodePath} with type {NodeType}", nodePath, selectedType);

                        // ONE authoritative create through the CreateNodeRequest pipeline (identity +
                        // access-control validators). No transient placeholder is written — the owning hub
                        // materialises the node's default content on create, and we land on its Edit area so
                        // the type-specific content editor renders on the hub that knows the content type.
                        // "Node already exists" / "Access denied" surface via OnError — no pre-flight
                        // existence query (those read the lagged index — AsynchronousCalls.md).
                        nodeFactory.CreateNode(newNode)
                        .Subscribe(
                            _ =>
                            {
                                logger?.LogInformation("Successfully created node at {NodePath}", nodePath);
                                var editUrl = MeshNodeLayoutAreas.BuildUrl(nodePath, MeshNodeLayoutAreas.EditArea);
                                actx.NavigateTo(editUrl);
                            },
                            ex =>
                            {
                                var errorMsg = ex.Message.Contains("Access denied") || ex.Message.Contains("Unauthorized")
                                    ? "You do not have permission to create nodes in this namespace."
                                    : $"Failed to create node: {ex.Message}";
                                ShowErrorDialog(actx, "Creation Failed", errorMsg);
                            });
                    });
                return Task.CompletedTask;
            }));

        stack = stack.WithView(buttonRow);
        return stack;
    }

    /// <summary>
    /// Builds the namespace input for the create form given an optional restriction:
    /// a single restricted value (incl. "") renders a read-only label; multiple values render a
    /// filtered picker; no restriction autocompletes existing Spaces (and never offers "").
    /// </summary>
    private static UiControl BuildNamespaceControl(string[]? restricted, string dataContext)
    {
        if (restricted is { Length: 1 })
        {
            var nsLabel = string.IsNullOrEmpty(restricted[0]) ? "Root (top-level)" : restricted[0];
            return Controls.Stack
                .WithWidth("100%")
                .WithStyle("margin-bottom: 16px;")
                .WithView(Controls.Body("Namespace").WithStyle("font-weight: 600; margin-bottom: 4px;"))
                .WithView(Controls.Body(nsLabel).WithStyle("color: var(--neutral-foreground-rest);"));
        }
        if (restricted is { Length: > 1 })
        {
            var nsItems = restricted.Select(ns =>
                string.IsNullOrEmpty(ns)
                    ? new MeshNode("") { Name = "Root (top-level)", NodeType = "Namespace" }
                    : new MeshNode(ns) { Name = ns, NodeType = "Namespace" }
            ).ToArray();
            return new MeshNodePickerControl(new JsonPointerReference("namespace"))
            {
                Label = "Namespace",
                Placeholder = "Select namespace...",
                DataContext = dataContext
            }.WithItems(nsItems)
             .WithMaxResults(15)
             .WithStyle("width: 100%; margin-bottom: 16px;");
        }
        // No restriction — autocomplete existing Spaces (real namespaces). "" is intentionally
        // NOT offered: only partition objects may live at root, and those restrict to [""].
        return new MeshNodePickerControl(new JsonPointerReference("namespace"))
        {
            Label = "Namespace",
            Placeholder = "Search a space to nest under...",
            DataContext = dataContext
        }.WithQueries("nodeType:Space")
         .WithMaxResults(15)
         .WithStyle("width: 100%; margin-bottom: 16px;");
    }

    /// <summary>
    /// Generates an Id from a Name by converting to PascalCase and removing special characters.
    /// E.g., "Build sales presentation deck" -> "BuildSalesPresentationDeck"
    /// </summary>
    private static string GenerateIdFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        // Split by spaces and other separators; preserve case within each word,
        // only ensure the first character is uppercase.
        var words = Regex.Split(name, @"[\s\-_]+")
            .Where(w => !string.IsNullOrEmpty(w))
            .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1));

        var pascalCase = string.Join("", words);

        // Remove any remaining non-alphanumeric characters
        pascalCase = Regex.Replace(pascalCase, @"[^a-zA-Z0-9]", "");

        return pascalCase ?? "";
    }
}
