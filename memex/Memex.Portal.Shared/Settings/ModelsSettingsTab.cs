using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Memex.Portal.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Settings tab for managing AI <c>ModelProvider</c> credentials.
/// Mirrors <see cref="ApiTokensSettingsTab"/>'s reactive shape — entries are
/// stored as MeshNodes under the owner's namespace (the User's partition
/// when viewed from the user settings page, any node's namespace when
/// viewed from that node's settings).
/// </summary>
public static class ModelsSettingsTab
{
    public const string TabId = "Models";

    public static MessageHubConfiguration AddModelsSettingsTab(
        this MessageHubConfiguration config)
    {
        return config.AddSettingsMenuItems(
            new SettingsMenuItemDefinition(
                Id: TabId,
                Label: "Models",
                ContentBuilder: BuildModelsContent,
                Group: "AI",
                Icon: FluentIcons.Sparkle(),
                Order: 220,
                RequiredPermission: Permission.Api));
    }

    internal static UiControl BuildModelsContent(
        LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var providerService = host.Hub.ServiceProvider.GetRequiredService<ModelProviderService>();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId ?? "";

        // Owner path: the MeshNode whose settings we're viewing, falling back
        // to the user's partition when this tab is rendered from the user's
        // own settings page (node==null). This matches the user's intent of
        // "under user namespace OR any other node's namespace".
        var ownerPath = !string.IsNullOrEmpty(node?.Path) ? node!.Path : userId;

        stack = stack.WithView(Controls.H2("Models").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;\">" +
            "Enter your own AI provider credentials. Each provider auto-creates the standard model nodes you can pick in chat. " +
            "Keys never leave your namespace.</p>"));

        if (string.IsNullOrEmpty(ownerPath))
        {
            stack = stack.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint);\">No owner identity available.</p>"));
            return stack;
        }

        const string createDataId = "modelProviderCreate";
        const string resultDataId = "modelProviderResult";

        // Build dropdown options from the live LanguageModelCatalogOptions —
        // each provider package self-registers via its AddXxxCatalog
        // extension (no central registry). Keyless providers (Copilot /
        // ClaudeCode use other auth) are filtered out for the BYO-key UX.
        var catalogOptions = host.Hub.ServiceProvider.GetService<LanguageModelCatalogOptions>();
        var providerOptions = catalogOptions?.Sources
            .Where(s => s.RequiresApiKey)
            .OrderBy(s => s.Order)
            .ToList()
            ?? new List<LanguageModelCatalogSource>();

        host.UpdateData(createDataId, new Dictionary<string, object?>
        {
            ["provider"] = providerOptions.FirstOrDefault()?.ProviderName ?? "",
            ["label"] = "",
            ["apiKey"] = "",
            ["endpoint"] = ""
        });

        var createSection = Controls.Stack.WithWidth("100%")
            .WithStyle("padding: 16px; background: var(--neutral-layer-2); border-radius: 8px; gap: 12px; margin-bottom: 24px;");
        createSection = createSection.WithView(
            Controls.Html("<h3 style=\"margin: 0 0 8px 0; font-size: 1rem;\">Add Provider</h3>"));

        var formRow1 = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: flex-end; flex-wrap: wrap;");

        var providerOptionsArray = providerOptions
            .Select(p => new Option<string>(p.ProviderName, p.EffectiveLabel))
            .Cast<Option>()
            .ToArray();
        var providerSelect = new SelectControl(new JsonPointerReference("provider"), providerOptionsArray)
        {
            Label = "Provider",
            DataContext = LayoutAreaReference.GetDataPointer(createDataId),
        }.WithWidth("200px");
        formRow1 = formRow1.WithView(providerSelect);

        formRow1 = formRow1.WithView(new TextFieldControl(new JsonPointerReference("label"))
        {
            Label = "Label (optional)",
            Placeholder = "e.g. Roland's personal key",
            DataContext = LayoutAreaReference.GetDataPointer(createDataId)
        }.WithWidth("260px"));

        var formRow2 = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: flex-end; flex-wrap: wrap;");

        formRow2 = formRow2.WithView(new TextFieldControl(new JsonPointerReference("apiKey"))
        {
            Label = "API Key",
            Placeholder = "sk-ant-… / sk-… (paste here)",
            DataContext = LayoutAreaReference.GetDataPointer(createDataId)
        }.WithWidth("420px"));

        formRow2 = formRow2.WithView(new TextFieldControl(new JsonPointerReference("endpoint"))
        {
            Label = "Endpoint (optional)",
            Placeholder = "leave blank for provider default",
            DataContext = LayoutAreaReference.GetDataPointer(createDataId)
        }.WithWidth("280px"));

        formRow2 = formRow2.WithView(Controls.Button("Save Provider")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(resultDataId,
                    "<p style=\"padding: 8px 12px; color: var(--neutral-foreground-hint);\">Saving…</p>");

                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(createDataId)
                    .Take(1)
                    .Subscribe(data =>
                    {
                        var providerName = data?.GetValueOrDefault("provider")?.ToString()?.Trim() ?? "";
                        var apiKey = data?.GetValueOrDefault("apiKey")?.ToString()?.Trim() ?? "";
                        var label = data?.GetValueOrDefault("label")?.ToString()?.Trim();
                        var endpoint = data?.GetValueOrDefault("endpoint")?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(label)) label = null;
                        if (string.IsNullOrEmpty(endpoint)) endpoint = null;

                        if (string.IsNullOrEmpty(providerName))
                        {
                            ctx.Host.UpdateData(resultDataId, ErrorHtml("Please choose a provider."));
                            return;
                        }
                        if (string.IsNullOrEmpty(apiKey))
                        {
                            ctx.Host.UpdateData(resultDataId, ErrorHtml("API key is required."));
                            return;
                        }

                        providerService.CreateProvider(ownerPath, providerName, apiKey, label, endpoint)
                            .Subscribe(
                                result => ctx.Host.UpdateData(resultDataId, SuccessHtml(
                                    $"Saved {Esc(providerName)} — {result.ModelNodes.Count} model(s) ready.")),
                                ex => ctx.Host.UpdateData(resultDataId, ErrorHtml(ex.Message)));
                    });
                return Task.CompletedTask;
            }));

        createSection = createSection.WithView(formRow1);
        createSection = createSection.WithView(formRow2);
        stack = stack.WithView(createSection);

        // Result area (live HTML for save outcomes).
        stack = stack.WithView((h, _) =>
            h.Stream.GetDataStream<string>(resultDataId)
                .Select(html => string.IsNullOrEmpty(html)
                    ? (UiControl?)Controls.Stack.WithWidth("100%")
                    : (UiControl?)Controls.Stack.WithWidth("100%").WithView(Controls.Html(html)))
                .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        stack = stack.WithView(
            Controls.Html("<h3 style=\"margin: 0 0 12px 0; font-size: 1rem;\">Configured Providers</h3>"));

        stack = stack.WithView((h, _) =>
            providerService.GetProvidersForOwner(ownerPath)
                .Select(providers => providers.Count == 0
                    ? (UiControl?)Controls.Html(
                        "<p style=\"color: var(--neutral-foreground-hint);\">No providers configured yet. Add one above.</p>")
                    : BuildProviderList(providers, providerService, resultDataId)));

        // ── Active models (provider selection) ────────────────────────────
        // Fan out ALL models the user can see via workspace.GetQuery — public
        // LanguageModel nodes surface regardless of provider Api-gating — group
        // by provider, and let the user toggle which providers feed their chat.
        // Toggling persists ModelProviderSelection; AgentChatClient + the
        // ChatClientCredentialResolver react (use-without-see handles org keys).
        stack = stack.WithView(Controls.Html(
            "<h3 style=\"margin: 24px 0 8px 0; font-size: 1rem;\">Active models</h3>" +
            "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 12px;\">" +
            "Choose which providers' models appear in your chat. Models an organisation shared with you " +
            "work even though their key stays hidden.</p>"));

        stack = stack.WithView((h, _) =>
        {
            var ws = h.Hub.GetWorkspace();
            var models = ws.GetQuery($"model-fanout:{ownerPath}", $"nodeType:{LanguageModelNodeType.NodeType}")
                .Select(nodes => nodes
                    .Where(n => string.Equals(n.NodeType, LanguageModelNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                    .ToList());
            var selection = providerService.GetSelection(ownerPath)
                .StartWith(ImmutableArray<string>.Empty);
            return models.CombineLatest(selection, (modelNodes, selected) =>
                (UiControl?)BuildModelSelectionList(modelNodes, selected, providerService, ownerPath, resultDataId));
        });

        return stack;
    }

    /// <summary>
    /// Renders the fanned-out models grouped by provider, each row toggling
    /// that provider's membership in the owner's <c>ModelProviderSelection</c>.
    /// </summary>
    private static UiControl BuildModelSelectionList(
        IReadOnlyList<MeshNode> modelNodes,
        ImmutableArray<string> selected,
        ModelProviderService service,
        string ownerPath,
        string resultDataId)
    {
        var byProvider = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in modelNodes)
        {
            var providerPath = ProviderPathOf(n);
            if (string.IsNullOrEmpty(providerPath)) continue;
            if (!byProvider.TryGetValue(providerPath, out var list))
                byProvider[providerPath] = list = new List<string>();
            list.Add(n.Name ?? n.Id);
        }

        if (byProvider.Count == 0)
            return Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No models discovered.</p>");

        var selectedSet = selected.IsDefault
            ? new HashSet<string>(StringComparer.Ordinal)
            : selected.ToHashSet(StringComparer.Ordinal);
        var container = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");

        foreach (var kvp in byProvider.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var providerPath = kvp.Key;
            var isActive = selectedSet.Contains(providerPath);
            var preview = string.Join(", ", kvp.Value.Take(6)) + (kvp.Value.Count > 6 ? "…" : "");

            var row = Controls.Stack.WithOrientation(Orientation.Horizontal)
                .WithStyle("padding: 10px 12px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; align-items: center; gap: 16px;");
            row = row.WithView(Controls.Html(
                $"<div style=\"flex: 1;\"><strong>{Esc(providerPath)}</strong>" +
                $"<div style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint);\">{Esc(preview)}</div></div>"));

            var capturedPath = providerPath;
            var capturedActive = isActive;
            row = row.WithView(Controls.Button(isActive ? "Remove" : "Add")
                .WithAppearance(isActive ? Appearance.Outline : Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    service.GetSelection(ownerPath).Take(1).Subscribe(cur =>
                    {
                        var set = cur.IsDefault ? new List<string>() : cur.ToList();
                        if (capturedActive) set.Remove(capturedPath);
                        else if (!set.Contains(capturedPath)) set.Add(capturedPath);
                        service.SetSelection(ownerPath, set.ToImmutableArray()).Subscribe(
                            ok => ctx.Host.UpdateData(resultDataId, ok
                                ? SuccessHtml(capturedActive ? $"Removed {capturedPath}." : $"Added {capturedPath}.")
                                : ErrorHtml("Failed to update selection.")),
                            ex => ctx.Host.UpdateData(resultDataId, ErrorHtml(ex.Message)));
                    });
                    return Task.CompletedTask;
                }));
            container = container.WithView(row);
        }
        return container;
    }

    /// <summary>Provider path for a LanguageModel node — its ProviderRef, else its parent path.</summary>
    private static string? ProviderPathOf(MeshNode n)
    {
        if (n.Content is ModelDefinition md && !string.IsNullOrEmpty(md.ProviderRef))
            return md.ProviderRef;
        var path = n.Path;
        if (string.IsNullOrEmpty(path)) return null;
        var idx = path.LastIndexOf('/');
        return idx > 0 ? path[..idx] : null;
    }

    private static UiControl BuildProviderList(
        IReadOnlyList<ProviderInfo> providers,
        ModelProviderService service,
        string resultDataId)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");

        foreach (var p in providers)
        {
            var row = Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("padding: 12px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; align-items: center; gap: 16px;");

            var endpointLabel = string.IsNullOrEmpty(p.Endpoint) ? "(default)" : Esc(p.Endpoint!);
            var modelsLabel = p.ModelIds.Count == 0
                ? "no models"
                : $"{p.ModelIds.Count} model(s)";

            row = row.WithView(Controls.Html(
                $"<div style=\"flex: 1;\">" +
                $"<strong>{Esc(p.Label ?? p.Provider)}</strong>" +
                $" <span style=\"color: var(--neutral-foreground-hint);\">({Esc(p.Provider)})</span>" +
                $"<div style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint);\">" +
                $"Endpoint: {endpointLabel} | " +
                $"Key fingerprint: {Esc(p.ApiKeyFingerprint)} | " +
                $"{modelsLabel} | " +
                $"Created: {p.CreatedAt:yyyy-MM-dd}" +
                "</div></div>"));

            var captured = p;
            row = row.WithView(Controls.Button("Delete")
                .WithAppearance(Appearance.Outline)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(resultDataId, PendingHtml($"Deleting {captured.Label ?? captured.Provider}…"));
                    service.DeleteProvider(captured.NodePath).Subscribe(
                        ok => ctx.Host.UpdateData(resultDataId, ok
                            ? SuccessHtml($"Deleted {Esc(captured.Label ?? captured.Provider)}.")
                            : ErrorHtml($"Failed to delete {Esc(captured.NodePath)}.")),
                        ex => ctx.Host.UpdateData(resultDataId, ErrorHtml(ex.Message)));
                    return Task.CompletedTask;
                }));

            container = container.WithView(row);
        }

        return container;
    }

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);

    private static string SuccessHtml(string msg) =>
        "<p style=\"padding: 8px 12px; color: #4ade80; background: var(--neutral-layer-2); " +
        $"border-radius: 6px;\">{Esc(msg)}</p>";

    private static string ErrorHtml(string msg) =>
        "<p style=\"padding: 8px 12px; color: #f87171; background: var(--neutral-layer-2); " +
        $"border-radius: 6px;\">{Esc(msg)}</p>";

    private static string PendingHtml(string msg) =>
        "<p style=\"padding: 8px 12px; color: var(--neutral-foreground-hint); " +
        $"background: var(--neutral-layer-2); border-radius: 6px;\">{Esc(msg)}</p>";
}
