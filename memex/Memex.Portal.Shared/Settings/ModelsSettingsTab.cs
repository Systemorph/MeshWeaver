using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.AI.Connect;
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
///
/// <para>Two provider <b>kinds</b> render deliberately different cards
/// (<see cref="ProviderKind"/>):</para>
/// <list type="bullet">
///   <item><b>API</b> (Azure AI Foundry, Azure OpenAI, Anthropic, OpenAI) — an
///         endpoint/key form plus a checkable list of models to enable.</item>
///   <item><b>CLI</b> (Claude Code, GitHub Copilot) — no model list; a login
///         status dot plus a Connect button that delegates to the CLI's native
///         login (paste-code for Claude, device-flow for Copilot). The inline
///         card is a NotConnected → Connecting → Connected/Error state machine
///         driven by <see cref="ConnectSessionManager"/>.</item>
/// </list>
///
/// <para>Entries are stored as MeshNodes under the owner's namespace (the
/// User's partition from the user settings page, any node's namespace from that
/// node's settings).</para>
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
                Label: "Language Models",
                ContentBuilder: BuildModelsContent,
                Group: "AI",
                Icon: FluentIcons.BrainCircuit(),
                // The magic/Sparkle icon belongs to the AI GROUP header (first non-null in a
                // group wins) — otherwise the "AI" group rendered indented with no icon. The
                // item itself gets a distinct model icon.
                GroupIcon: FluentIcons.Sparkle(),
                Order: 220,
                RequiredPermission: Permission.Api));
    }

    internal static UiControl BuildModelsContent(
        LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var providerService = host.Hub.ServiceProvider.GetRequiredService<ModelProviderService>();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId ?? "";

        var ownerPath = !string.IsNullOrEmpty(node?.Path) ? node!.Path : userId;

        stack = stack.WithView(Controls.H2("Language Models").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;\">" +
            "Bring your own AI provider credentials, or connect a co-hosted CLI with your subscription. " +
            "Keys never leave your namespace.</p>"));

        if (string.IsNullOrEmpty(ownerPath))
        {
            stack = stack.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint);\">No owner identity available.</p>"));
            return stack;
        }

        const string resultDataId = "modelProviderResult";

        var catalogOptions = host.Hub.ServiceProvider.GetService<LanguageModelCatalogOptions>();
        var allSources = catalogOptions?.Sources.OrderBy(s => s.Order).ToList()
            ?? new List<LanguageModelCatalogSource>();
        var apiSources = allSources.Where(s => s.Kind == ProviderKind.Api).ToList();
        var cliSources = allSources.Where(s => s.Kind == ProviderKind.Cli).ToList();

        // ── API providers — key/endpoint form + model list ───────────────────
        if (apiSources.Count > 0)
        {
            stack = stack.WithView(Controls.Html(
                "<h3 style=\"margin: 8px 0; font-size: 1rem;\">API providers</h3>" +
                "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 12px;\">" +
                "Add a key (and endpoint, for Azure), then choose which models to enable.</p>"));

            foreach (var src in apiSources)
                stack = stack.WithView(BuildApiCard(host, src, providerService, ownerPath, resultDataId));
        }

        // ── CLI providers — login status + connect (no model list) ────────────
        var sessionManager = host.Hub.ServiceProvider.GetService<ConnectSessionManager>();
        if (cliSources.Count > 0)
        {
            stack = stack.WithView(Controls.Html(
                "<h3 style=\"margin: 24px 0 8px 0; font-size: 1rem;\">CLI providers</h3>" +
                "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 12px;\">" +
                "Log in with your subscription — no key, no model list.</p>"));

            foreach (var src in cliSources)
                stack = stack.WithView(BuildCliCard(host, src, sessionManager, providerService, ownerPath, userId, resultDataId));
        }

        // Result area (live HTML for save / connect outcomes).
        stack = stack.WithView((h, _) =>
            h.Stream.GetDataStream<string>(resultDataId)
                .Select(html => string.IsNullOrEmpty(html)
                    ? (UiControl?)Controls.Stack.WithWidth("100%")
                    : (UiControl?)Controls.Stack.WithWidth("100%").WithView(Controls.Html(html)))
                .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        // ── Configured providers ──────────────────────────────────────────────
        stack = stack.WithView(
            Controls.Html("<h3 style=\"margin: 24px 0 12px 0; font-size: 1rem;\">Configured Providers</h3>"));

        stack = stack.WithView((h, _) =>
            providerService.GetProvidersForOwner(ownerPath)
                .Select(providers => providers.Count == 0
                    ? (UiControl?)Controls.Html(
                        "<p style=\"color: var(--neutral-foreground-hint);\">No providers configured yet.</p>")
                    : BuildProviderList(providers, providerService, resultDataId)));

        // ── Active models (provider selection) ────────────────────────────────
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

    // ════════════════════════════════════════════════════════════════════════
    //  API card — endpoint/key form + checkable model list
    // ════════════════════════════════════════════════════════════════════════

    private static UiControl BuildApiCard(
        LayoutAreaHost host,
        LanguageModelCatalogSource src,
        ModelProviderService providerService,
        string ownerPath,
        string resultDataId)
    {
        var formDataId = $"apiForm:{src.ProviderName}";
        // Azure providers take an endpoint; others just a key.
        var needsEndpoint = src.ProviderName.StartsWith("Azure", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(src.DefaultEndpoint);

        host.UpdateData(formDataId, new Dictionary<string, object?>
        {
            ["apiKey"] = "",
            ["endpoint"] = src.DefaultEndpoint ?? "",
        });

        var card = Controls.Stack.WithWidth("100%")
            .WithStyle("padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; gap: 12px; margin-bottom: 12px;");

        card = card.WithView(Controls.Html(
            $"<div style=\"display:flex; align-items:center; gap:8px;\">" +
            $"<strong style=\"font-size:0.95rem;\">{Esc(src.EffectiveLabel)}</strong>" +
            $"<span style=\"font-size:0.7rem; padding:1px 6px; border-radius:4px; background:var(--neutral-layer-3); color:var(--neutral-foreground-hint);\">API</span>" +
            $"</div>"));

        var formRow = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: flex-end; flex-wrap: wrap;");

        if (needsEndpoint)
        {
            formRow = formRow.WithView(new TextFieldControl(new JsonPointerReference("endpoint"))
            {
                Label = "Endpoint",
                Placeholder = "https://….services.ai.azure.com",
                DataContext = LayoutAreaReference.GetDataPointer(formDataId)
            }.WithWidth("320px"));
        }

        formRow = formRow.WithView(new TextFieldControl(new JsonPointerReference("apiKey"))
        {
            Label = "API key",
            Placeholder = "paste key here",
            DataContext = LayoutAreaReference.GetDataPointer(formDataId)
        }.WithWidth("320px"));

        var providerName = src.ProviderName;
        formRow = formRow.WithView(Controls.Button("Save")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(resultDataId, PendingHtml($"Saving {providerName}…"));
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formDataId)
                    .Take(1)
                    .Subscribe(data =>
                    {
                        var apiKey = data?.GetValueOrDefault("apiKey")?.ToString()?.Trim() ?? "";
                        var endpoint = data?.GetValueOrDefault("endpoint")?.ToString()?.Trim();
                        if (string.IsNullOrEmpty(endpoint)) endpoint = null;
                        if (string.IsNullOrEmpty(apiKey))
                        {
                            ctx.Host.UpdateData(resultDataId, ErrorHtml("API key is required."));
                            return;
                        }
                        providerService.CreateProvider(ownerPath, providerName, apiKey, label: null, endpointOverride: endpoint)
                            .Subscribe(
                                result => ctx.Host.UpdateData(resultDataId, SuccessHtml(
                                    $"Saved {Esc(providerName)} — {result.ModelNodes.Count} model(s) ready.")),
                                ex => ctx.Host.UpdateData(resultDataId, ErrorHtml(ex.Message)));
                    });
                return Task.CompletedTask;
            }));

        card = card.WithView(formRow);

        // Checkable model list — the provider's default model ids (the resolver
        // enables/selects them via the Active-models selection below). Shown so
        // the user sees what models the saved key unlocks.
        var modelIds = src.EffectiveModelIds;
        if (modelIds.Length > 0)
        {
            var modelsHtml = "Models &nbsp;" + string.Join(" &nbsp; ",
                modelIds.Select(m => $"<span style=\"font-size:0.8rem;\">☑ {Esc(m)}</span>"));
            card = card.WithView(Controls.Html(
                $"<div style=\"font-size:0.85rem; color:var(--neutral-foreground-hint);\">{modelsHtml}</div>"));
        }

        return card;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  CLI card — login status + connect (state machine, no model list)
    // ════════════════════════════════════════════════════════════════════════

    private static UiControl BuildCliCard(
        LayoutAreaHost host,
        LanguageModelCatalogSource src,
        ConnectSessionManager? sessionManager,
        ModelProviderService providerService,
        string ownerPath,
        string userId,
        string resultDataId)
    {
        var provider = src.ProviderName.Equals("Copilot", StringComparison.OrdinalIgnoreCase)
            ? ConnectProvider.Copilot
            : ConnectProvider.ClaudeCode;
        var stateDataId = $"cliState:{src.ProviderName}";

        var card = Controls.Stack.WithWidth("100%")
            .WithStyle("padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; gap: 12px; margin-bottom: 12px;");

        card = card.WithView(Controls.Html(
            $"<div style=\"display:flex; align-items:center; gap:8px;\">" +
            $"<strong style=\"font-size:0.95rem;\">{Esc(src.EffectiveLabel)}</strong>" +
            $"<span style=\"font-size:0.7rem; padding:1px 6px; border-radius:4px; background:var(--neutral-layer-3); color:var(--neutral-foreground-hint);\">CLI</span>" +
            $"</div>"));

        if (sessionManager is null || !sessionManager.Supports(provider))
        {
            card = card.WithView(Controls.Html(
                "<p style=\"font-size:0.85rem; color:var(--neutral-foreground-hint);\">" +
                "Connect is not available in this deployment.</p>"));
            return card;
        }

        // Initial state: NotConnected unless the CLI reports an existing login.
        // The card databinds to a per-provider state stream that the Connect /
        // Disconnect buttons drive.
        host.UpdateData(stateDataId, RenderCliBody(provider, new ConnectStatus.NotConnected(), src, stateDataId, sessionManager, ownerPath, userId, resultDataId));

        // Probe IsLoggedIn once on render; flip to a Connected affordance when true.
        var configDir = ResolveConfigDir(host, userId, provider);
        sessionManager.IsLoggedIn(provider, configDir)
            .Take(1)
            .Subscribe(loggedIn =>
            {
                if (loggedIn)
                    host.UpdateData(stateDataId, RenderConnectedBody(src, provider, stateDataId, sessionManager, ownerPath, userId, resultDataId, loginName: null));
            });

        // Render the live state body.
        card = card.WithView((h, _) =>
            h.Stream.GetDataStream<UiControl>(stateDataId)
                .StartWith((UiControl)Controls.Html("<p style=\"color:var(--neutral-foreground-hint);\">…</p>")));

        return card;
    }

    /// <summary>The NotConnected body — status dot + Connect button.</summary>
    private static UiControl RenderCliBody(
        ConnectProvider provider,
        ConnectStatus status,
        LanguageModelCatalogSource src,
        string stateDataId,
        ConnectSessionManager sessionManager,
        string ownerPath,
        string userId,
        string resultDataId)
    {
        var body = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        body = body.WithView(Controls.Html(
            "<div style=\"display:flex; align-items:center; gap:8px; font-size:0.85rem;\">" +
            "<span style=\"color:#9ca3af;\">●</span> Not connected — uses your subscription</div>"));

        body = body.WithView(Controls.Button($"Connect {src.EffectiveLabel}")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                var host = ctx.Host;
                var configDir = ResolveConfigDir(host, userId, provider);
                host.UpdateData(stateDataId, ConnectingPlaceholder());
                sessionManager.StartConnect(ownerPath, provider, configDir)
                    .Subscribe(
                        st => host.UpdateData(stateDataId, RenderConnectingOrTerminal(
                            provider, st, src, stateDataId, sessionManager, ownerPath, userId, resultDataId)),
                        ex => host.UpdateData(stateDataId, RenderError(provider, src, ex.Message, stateDataId, sessionManager, ownerPath, userId, resultDataId)));
                return Task.CompletedTask;
            }));
        return body;
    }

    /// <summary>Branch on the live <see cref="ConnectStatus"/> coming back from StartConnect / SubmitCode.</summary>
    private static UiControl RenderConnectingOrTerminal(
        ConnectProvider provider,
        ConnectStatus status,
        LanguageModelCatalogSource src,
        string stateDataId,
        ConnectSessionManager sessionManager,
        string ownerPath,
        string userId,
        string resultDataId)
    {
        return status switch
        {
            ConnectStatus.Connecting c => RenderConnecting(provider, c.Challenge, src, stateDataId, sessionManager, ownerPath, userId, resultDataId),
            ConnectStatus.Connected ok => RenderConnectedBody(src, provider, stateDataId, sessionManager, ownerPath, userId, resultDataId, loginName: null),
            ConnectStatus.Error err => RenderError(provider, src, err.Reason, stateDataId, sessionManager, ownerPath, userId, resultDataId),
            _ => RenderCliBody(provider, status, src, stateDataId, sessionManager, ownerPath, userId, resultDataId),
        };
    }

    /// <summary>Connecting body — auth URL + (Claude) a paste-code field / (Copilot) the device code.</summary>
    private static UiControl RenderConnecting(
        ConnectProvider provider,
        ConnectChallenge challenge,
        LanguageModelCatalogSource src,
        string stateDataId,
        ConnectSessionManager sessionManager,
        string ownerPath,
        string userId,
        string resultDataId)
    {
        var body = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        body = body.WithView(Controls.Html(
            "<div style=\"display:flex; align-items:center; gap:8px; font-size:0.85rem;\">" +
            "<span style=\"color:#f59e0b;\">●</span> Connecting…</div>"));

        if (challenge.RequiresPastedCode)
        {
            // Claude paste-code flow.
            body = body.WithView(Controls.Html(
                "<div style=\"font-size:0.85rem;\">1&nbsp; Authorize in your browser:<br/>" +
                $"<a href=\"{Esc(challenge.VerificationUrl)}\" target=\"_blank\" rel=\"noopener\" " +
                $"style=\"word-break:break-all;\">{Esc(challenge.VerificationUrl)}</a></div>" +
                "<div style=\"font-size:0.85rem; margin-top:6px;\">2&nbsp; Paste the code Claude shows:</div>"));

            var codeDataId = $"cliCode:{src.ProviderName}";
            body = body.WithView(BuildPasteCodeRow(provider, src, codeDataId, stateDataId, sessionManager, ownerPath, userId, resultDataId));
        }
        else
        {
            // Copilot device-code flow — auto-poll, nothing to paste.
            var codeBlock = string.IsNullOrEmpty(challenge.UserCode)
                ? ""
                : $"<div style=\"font-size:1.2rem; font-family:monospace; padding:6px 12px; border:1px dashed var(--neutral-stroke-rest); border-radius:6px; display:inline-block; margin:6px 0;\">{Esc(challenge.UserCode!)}</div>";
            body = body.WithView(Controls.Html(
                $"<div style=\"font-size:0.85rem;\">Enter this code at " +
                $"<a href=\"{Esc(challenge.VerificationUrl)}\" target=\"_blank\" rel=\"noopener\">{Esc(challenge.VerificationUrl)}</a></div>" +
                codeBlock +
                "<div style=\"font-size:0.8rem; color:var(--neutral-foreground-hint);\">⏳ auto-checking…</div>"));
        }

        body = body.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx =>
            {
                sessionManager.Cancel(ownerPath, provider);
                ctx.Host.UpdateData(stateDataId, RenderCliBody(provider, new ConnectStatus.NotConnected(), src, stateDataId, sessionManager, ownerPath, userId, resultDataId));
                return Task.CompletedTask;
            }));
        return body;
    }

    private static UiControl BuildPasteCodeRow(
        ConnectProvider provider,
        LanguageModelCatalogSource src,
        string codeDataId,
        string stateDataId,
        ConnectSessionManager sessionManager,
        string ownerPath,
        string userId,
        string resultDataId)
    {
        var row = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px; align-items: flex-end;");
        row = row.WithView(new TextFieldControl(new JsonPointerReference("code"))
        {
            Label = "Code",
            Placeholder = "paste here",
            DataContext = LayoutAreaReference.GetDataPointer(codeDataId)
        }.WithWidth("280px"));

        row = row.WithView(Controls.Button("Submit")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                var host = ctx.Host;
                host.Stream.GetDataStream<Dictionary<string, object?>>(codeDataId)
                    .Take(1)
                    .Subscribe(data =>
                    {
                        var code = data?.GetValueOrDefault("code")?.ToString()?.Trim() ?? "";
                        if (string.IsNullOrEmpty(code))
                        {
                            host.UpdateData(resultDataId, ErrorHtml("Please paste the code Claude showed."));
                            return;
                        }
                        sessionManager.SubmitCode(ownerPath, provider, code)
                            .Subscribe(
                                st => host.UpdateData(stateDataId, RenderConnectingOrTerminal(
                                    provider, st, src, stateDataId, sessionManager, ownerPath, userId, resultDataId)),
                                ex => host.UpdateData(stateDataId, RenderError(provider, src, ex.Message, stateDataId, sessionManager, ownerPath, userId, resultDataId)));
                    });
                return Task.CompletedTask;
            }));
        return row;
    }

    private static UiControl RenderConnectedBody(
        LanguageModelCatalogSource src,
        ConnectProvider provider,
        string stateDataId,
        ConnectSessionManager sessionManager,
        string ownerPath,
        string userId,
        string resultDataId,
        string? loginName)
    {
        var body = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 16px; align-items: center;");
        var who = string.IsNullOrEmpty(loginName) ? "" : $" as {Esc(loginName)}";
        body = body.WithView(Controls.Html(
            "<div style=\"flex:1; display:flex; align-items:center; gap:8px; font-size:0.85rem;\">" +
            $"<span style=\"color:#22c55e;\">✓</span> Connected{who}</div>"));

        body = body.WithView(Controls.Button("Disconnect")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx =>
            {
                sessionManager.Cancel(ownerPath, provider);
                // Remove the stored provider node so the next render shows NotConnected.
                var providerService = ctx.Host.Hub.ServiceProvider.GetRequiredService<ModelProviderService>();
                providerService.DeleteProvider($"{ModelProviderNodeType.UserNamespacePath(ownerPath)}/{src.ProviderName}")
                    .Subscribe(
                        _ => ctx.Host.UpdateData(stateDataId, RenderCliBody(provider, new ConnectStatus.NotConnected(), src, stateDataId, sessionManager, ownerPath, userId, resultDataId)),
                        ex => ctx.Host.UpdateData(resultDataId, ErrorHtml(ex.Message)));
                return Task.CompletedTask;
            }));
        return body;
    }

    private static UiControl RenderError(
        ConnectProvider provider,
        LanguageModelCatalogSource src,
        string reason,
        string stateDataId,
        ConnectSessionManager sessionManager,
        string ownerPath,
        string userId,
        string resultDataId)
    {
        var body = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        body = body.WithView(Controls.Html(
            $"<div style=\"font-size:0.85rem; color:#f87171;\">● {Esc(reason)}</div>"));
        body = body.WithView(Controls.Button("Retry")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(stateDataId, RenderCliBody(provider, new ConnectStatus.NotConnected(), src, stateDataId, sessionManager, ownerPath, userId, resultDataId));
                return Task.CompletedTask;
            }));
        return body;
    }

    private static UiControl ConnectingPlaceholder() =>
        Controls.Html("<p style=\"font-size:0.85rem; color:var(--neutral-foreground-hint);\">● Connecting…</p>");

    private static string? ResolveConfigDir(LayoutAreaHost host, string userId, ConnectProvider provider)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        if (provider == ConnectProvider.ClaudeCode)
        {
            var cfg = host.Hub.ServiceProvider
                .GetService<Microsoft.Extensions.Options.IOptions<MeshWeaver.AI.ClaudeCode.ClaudeCodeConfiguration>>()?.Value;
            var root = cfg?.ConfigDirRoot?.TrimEnd('/', '\\');
            return !string.IsNullOrEmpty(root) ? System.IO.Path.Combine(root, userId, ".claude") : null;
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Active-models selection (unchanged behaviour)
    // ════════════════════════════════════════════════════════════════════════

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
