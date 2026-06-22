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
/// <para>The <b>API</b> flow is one "add a provider" card: pick a provider type,
/// enter the base URL + key, <b>Fetch models</b> (live <c>/models</c> via
/// <see cref="ProviderModelLister"/>), tick the ones to bring, Save. The generic
/// <c>OpenAICompatible</c> type covers any OpenAI-wire endpoint (OpenRouter, Groq,
/// Together, a local vLLM, …) — several distinct gateways coexist because each
/// instance is keyed by a derived id, not the provider name.</para>
///
/// <para>The <b>CLI</b> flow (<see cref="ProviderKind.Cli"/> — Claude Code, GitHub
/// Copilot) is a login status + Connect button that delegates to the CLI's native
/// login, driven by <see cref="ConnectSessionManager"/>.</para>
///
/// <para>The SAME section UI renders twice: once for the viewer's OWN providers in
/// their dotfile namespace (<c>{owner}/_Memex</c>), and once — gated on
/// <c>hub.IsGlobalAdmin()</c> via <see cref="AdminMenuGate.IsPlatformAdmin"/> — for the
/// shared PLATFORM catalog at <see cref="ModelProviderNodeType.RootNamespace"/>
/// (<c>Admin/Provider</c>). It is the IDENTICAL add-provider card + configured-providers
/// list — the only difference is the namespace prefix the writes target. The platform
/// section's writes are stamped <see cref="SyncBehavior.ExcludeThisAndChildren"/> so the
/// boot seeder (<see cref="BuiltInLanguageModelProvider"/>) create-if-absent seeds the
/// catalog once and never reverts an admin edit. Adding / changing a model = the same
/// fetch-and-tick flow; editing endpoint or key = delete + re-add (as the per-user GUI
/// already works).</para>
///
/// <para>Entries are stored as MeshNodes. Rendered with framework controls +
/// markdown — no hand-built HTML.</para>
/// </summary>
public static class ModelsSettingsTab
{
    public const string TabId = "Models";

    // Section scope keys — disambiguate the layout-area data ids so the per-owner and
    // platform cards never share form / fetch / selection / status state.
    private const string UserScope = "user";
    private const string PlatformScope = "platform";

    // ── scoped data ids ───────────────────────────────────────────────────────
    private static string FormId(string scope) => $"addProviderForm:{scope}";    // { type, endpoint, apiKey, name, filter, manual }
    private static string TypesId(string scope) => $"addProviderTypes:{scope}";   // Option[]
    private static string FetchedId(string scope) => $"fetchedModels:{scope}";    // string[]
    private static string SelId(string scope) => $"fetchedModelSel:{scope}";      // Dictionary<string,bool> keyed by index
    private static string ResultId(string scope) => $"modelProviderResult:{scope}"; // markdown string

    private const int MaxCheckboxes = 250;

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

        stack = stack
            .WithView(Controls.H2("Language Models"))
            .WithView(Controls.Markdown(
                "Bring your own AI provider credentials, or connect a co-hosted CLI with your " +
                "subscription. Keys never leave your namespace."));

        if (string.IsNullOrEmpty(ownerPath))
            return stack.WithView(Controls.Markdown("_No owner identity available._"));

        var catalogOptions = host.Hub.ServiceProvider.GetService<LanguageModelCatalogOptions>();
        var allSources = catalogOptions?.Sources.OrderBy(s => s.Order).ToList()
            ?? new List<LanguageModelCatalogSource>();
        var apiSources = allSources.Where(s => s.Kind == ProviderKind.Api).ToList();
        var cliSources = allSources.Where(s => s.Kind == ProviderKind.Cli).ToList();
        var byName = apiSources.ToDictionary(s => s.ProviderName, StringComparer.OrdinalIgnoreCase);
        var sessionManager = host.Hub.ServiceProvider.GetService<ConnectSessionManager>();

        // ── The viewer's OWN providers ({owner}/_Memex) ───────────────────────
        stack = stack.WithView(BuildProviderSection(
            host, UserScope, ownerPath, ModelProviderNodeType.UserNamespacePath(ownerPath),
            isPlatform: false, apiSources, cliSources, byName, providerService, sessionManager, userId));

        // ── Platform providers (Admin/Provider) — global admins only ──────────
        // Reuses the SAME section UI, targeted at the Admin partition's shared catalog.
        // Reactively gated on the viewer's Admin-scope grant (see AdminMenuGate): renders
        // empty until the positive grant surfaces, never for a non-admin.
        stack = stack.WithView((h, _) =>
            AdminMenuGate.IsPlatformAdmin(h)
                .Select(isAdmin => isAdmin
                    ? (UiControl?)BuildProviderSection(
                        h, PlatformScope, userId, ModelProviderNodeType.RootNamespace,
                        isPlatform: true, apiSources, cliSources, byName, providerService, sessionManager, userId,
                        sectionHeader: "Platform providers",
                        sectionIntro:
                            "Manage the shared platform model catalog at **`" + ModelProviderNodeType.RootNamespace +
                            "`** — every user sees these models. Edits are sync-protected, so a redeploy never " +
                            "reverts them. Visible to platform admins only.")
                    : (UiControl?)Controls.Stack.WithWidth("100%")));

        return stack;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  One provider section — rendered for the owner ({owner}/_Memex) AND, when
    //  the viewer is a global admin, for the platform catalog (Admin/Provider).
    //  IDENTICAL controls; the platform variant only targets a different namespace
    //  (writes go to Admin/Provider, stamped sync-excluded) and omits the per-user
    //  CLI-connect + active-models picker (personal choices, not platform config).
    // ════════════════════════════════════════════════════════════════════════

    private static UiControl BuildProviderSection(
        LayoutAreaHost host,
        string scope,
        string ownerPath,
        string targetNamespace,
        bool isPlatform,
        IReadOnlyList<LanguageModelCatalogSource> apiSources,
        IReadOnlyList<LanguageModelCatalogSource> cliSources,
        IReadOnlyDictionary<string, LanguageModelCatalogSource> byName,
        ModelProviderService providerService,
        ConnectSessionManager? sessionManager,
        string userId,
        string? sectionHeader = null,
        string? sectionIntro = null)
    {
        var sectionStyle = isPlatform
            ? "width: 100%; gap: 8px; margin-top: 24px; padding-top: 16px; border-top: 1px solid var(--neutral-stroke-rest);"
            : "width: 100%; gap: 8px;";
        var section = Controls.Stack.WithStyle(sectionStyle);

        if (!string.IsNullOrEmpty(sectionHeader))
            section = section.WithView(Controls.H2(sectionHeader));
        if (!string.IsNullOrEmpty(sectionIntro))
            section = section.WithView(Controls.Markdown(sectionIntro));

        // The storage namespace passed to the service: null = the owner's _Memex (the
        // service default), or the explicit platform namespace.
        string? serviceNamespace = isPlatform ? targetNamespace : null;

        // ── Add a provider (API) ──────────────────────────────────────────────
        if (apiSources.Count > 0)
        {
            section = section.WithView(BuildAddProviderCard(
                host, scope, apiSources, byName, providerService, ownerPath, serviceNamespace));

            // Live result/status line.
            section = section.WithView((h, _) =>
                h.Stream.GetDataStream<string>(ResultId(scope))
                    .Select(md => string.IsNullOrEmpty(md)
                        ? (UiControl?)Controls.Stack.WithWidth("100%")
                        : (UiControl?)Controls.Markdown(md))
                    .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));
        }

        // ── CLI providers — login status + connect (owner sections only) ──────
        if (!isPlatform && cliSources.Count > 0)
        {
            section = section.WithView(Controls.H3("CLI providers"));
            section = section.WithView(Controls.Markdown("Log in with your subscription — no key, no model list."));
            foreach (var src in cliSources)
                section = section.WithView(BuildCliCard(host, src, sessionManager, providerService, ownerPath, userId));
        }

        // ── Configured providers ──────────────────────────────────────────────
        section = section.WithView(Controls.H3("Configured providers"));
        section = section.WithView((h, _) =>
            providerService.GetProvidersForOwner(ownerPath, serviceNamespace)
                .Select(providers => providers.Count == 0
                    ? (UiControl?)Controls.Markdown("_No providers configured yet._")
                    : BuildProviderList(providers, providerService, scope)));

        // ── Active models (provider selection) — a per-user choice, owner sections only ──
        if (!isPlatform)
        {
            section = section.WithView(Controls.H3("Active models"));
            section = section.WithView(Controls.Markdown(
                "Choose which providers' models appear in your chat. Models an organisation shared " +
                "with you work even though their key stays hidden."));
            section = section.WithView((h, _) =>
            {
                var ws = h.Hub.GetWorkspace();
                var models = ws.GetQuery($"model-fanout:{ownerPath}", $"nodeType:{LanguageModelNodeType.NodeType}")
                    .Select(nodes => nodes
                        .Where(n => string.Equals(n.NodeType, LanguageModelNodeType.NodeType, StringComparison.OrdinalIgnoreCase))
                        .ToList());
                var selection = providerService.GetSelection(ownerPath).StartWith(ImmutableArray<string>.Empty);
                return models.CombineLatest(selection, (modelNodes, selected) =>
                    (UiControl?)BuildModelSelectionList(modelNodes, selected, providerService, ownerPath, scope));
            });
        }

        return section;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Add-provider card: type → URL → key → fetch → select → save
    // ════════════════════════════════════════════════════════════════════════

    private static UiControl BuildAddProviderCard(
        LayoutAreaHost host,
        string scope,
        IReadOnlyList<LanguageModelCatalogSource> apiSources,
        IReadOnlyDictionary<string, LanguageModelCatalogSource> byName,
        ModelProviderService providerService,
        string ownerPath,
        string? targetNamespace)
    {
        // Seed the form + option list + (empty) fetch state.
        var firstType = apiSources[0].ProviderName;
        host.UpdateData(FormId(scope), new Dictionary<string, object?>
        {
            ["type"] = firstType,
            ["endpoint"] = "",
            ["apiKey"] = "",
            ["name"] = "",
            ["filter"] = "",
            ["manual"] = "",
        });
        host.UpdateData(TypesId(scope), apiSources
            .Select(s => (Option)new Option<string>(s.ProviderName, s.EffectiveLabel))
            .ToArray());
        host.UpdateData(FetchedId(scope), Array.Empty<string>());
        host.UpdateData(SelId(scope), new Dictionary<string, bool>());

        var formPtr = LayoutAreaReference.GetDataPointer(FormId(scope));

        var card = Controls.Stack.WithWidth("100%")
            .WithStyle("padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; gap: 12px; margin-bottom: 12px;");

        card = card.WithView(Controls.H3("Add a provider"));
        card = card.WithView(Controls.Markdown(
            "Pick a type, paste the base URL (including `/v1`) + key, then **Fetch models** and tick the " +
            "ones to bring. For an OpenAI-compatible gateway (e.g. OpenRouter `https://openrouter.ai/api/v1`) " +
            "choose **OpenAI-compatible** and give it a name."));

        card = card.WithView(new SelectControl(
                new JsonPointerReference("type"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer(TypesId(scope))))
            {
                Label = "Provider type",
                DataContext = formPtr,
            });

        var row = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: flex-end; flex-wrap: wrap;");
        row = row.WithView(new TextFieldControl(new JsonPointerReference("endpoint"))
        {
            Label = "Base URL",
            Placeholder = "https://openrouter.ai/api/v1 (blank = provider default)",
            DataContext = formPtr,
        }.WithWidth("340px"));
        row = row.WithView(new TextFieldControl(new JsonPointerReference("apiKey"))
        {
            Label = "API key",
            Placeholder = "paste key here",
            Password = true,
            DataContext = formPtr,
        }.WithWidth("280px"));
        row = row.WithView(new TextFieldControl(new JsonPointerReference("name"))
        {
            Label = "Name (for custom URLs)",
            Placeholder = "e.g. OpenRouter",
            DataContext = formPtr,
        }.WithWidth("200px"));
        card = card.WithView(row);

        card = card.WithView(Controls.Button("Fetch models")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx => { FetchModels(ctx, scope, byName); return Task.CompletedTask; }));

        // Filter + manual-add (static so they keep focus while the list re-renders).
        var tools = Controls.Stack.WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: flex-end; flex-wrap: wrap;");
        tools = tools.WithView(new TextFieldControl(new JsonPointerReference("filter"))
        {
            Label = "Filter",
            Placeholder = "type to filter…",
            Immediate = true,
            DataContext = formPtr,
        }.WithWidth("220px"));
        tools = tools.WithView(new TextFieldControl(new JsonPointerReference("manual"))
        {
            Label = "Add a model id manually",
            Placeholder = "vendor/model",
            DataContext = formPtr,
        }.WithWidth("220px"));
        tools = tools.WithView(Controls.Button("Add id")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx => { AddManualModel(ctx, scope); return Task.CompletedTask; }));
        card = card.WithView(tools);

        // Checkable model list — re-renders on fetched-models / filter change only.
        card = card.WithView((h, _) =>
        {
            var fetched = h.Stream.GetDataStream<string[]>(FetchedId(scope)).StartWith(Array.Empty<string>());
            var form = h.Stream.GetDataStream<Dictionary<string, object?>>(FormId(scope))
                .StartWith(new Dictionary<string, object?>());
            return fetched.CombineLatest(form, (ids, f) =>
                (UiControl?)BuildModelChecklist(scope, ids ?? Array.Empty<string>(),
                    f?.GetValueOrDefault("filter")?.ToString() ?? ""));
        });

        card = card.WithView(Controls.Button("Save provider")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx => { SaveProvider(ctx, scope, byName, providerService, ownerPath, targetNamespace); return Task.CompletedTask; }));

        return card;
    }

    private static UiControl BuildModelChecklist(string scope, string[] ids, string filter)
    {
        if (ids.Length == 0)
            return Controls.Markdown("_No models yet — enter a base URL + key and click **Fetch models**._");

        var indexed = ids
            .Select((id, i) => (id, i))
            .Where(x => string.IsNullOrEmpty(filter)
                || x.id.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var selPtr = LayoutAreaReference.GetDataPointer(SelId(scope));
        var list = Controls.Stack.WithWidth("100%")
            .WithStyle("max-height: 320px; overflow-y: auto; gap: 2px; padding: 8px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px;");

        foreach (var (id, i) in indexed.Take(MaxCheckboxes))
            list = list.WithView(new CheckBoxControl(new JsonPointerReference(i.ToString()))
            {
                Label = id,
                DataContext = selPtr,
            });

        var shown = Math.Min(indexed.Count, MaxCheckboxes);
        var header = indexed.Count == ids.Length
            ? $"**{ids.Length}** models · showing {shown}"
            : $"**{indexed.Count}** of {ids.Length} match · showing {shown}";
        if (indexed.Count > MaxCheckboxes)
            header += $" — narrow with the filter to see the rest";

        return Controls.Stack.WithWidth("100%").WithStyle("gap: 6px;")
            .WithView(Controls.Markdown(header))
            .WithView(list);
    }

    private static void FetchModels(
        UiActionContext ctx, string scope, IReadOnlyDictionary<string, LanguageModelCatalogSource> byName)
    {
        ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(FormId(scope)).Take(1).Subscribe(form =>
        {
            var type = form?.GetValueOrDefault("type")?.ToString() ?? "";
            var endpoint = form?.GetValueOrDefault("endpoint")?.ToString()?.Trim();
            var apiKey = form?.GetValueOrDefault("apiKey")?.ToString()?.Trim() ?? "";
            var src = byName.GetValueOrDefault(type);
            var effEndpoint = string.IsNullOrEmpty(endpoint) ? src?.DefaultEndpoint : endpoint;

            if (string.IsNullOrEmpty(apiKey))
            {
                ctx.Host.UpdateData(ResultId(scope), ErrorMd("An API key is required to fetch models."));
                return;
            }

            var lister = ctx.Host.Hub.ServiceProvider.GetService<ProviderModelLister>();
            if (lister is null)
            {
                ctx.Host.UpdateData(ResultId(scope), ErrorMd("Model fetching is not available in this deployment."));
                return;
            }

            ctx.Host.UpdateData(ResultId(scope), PendingMd($"Fetching models from {type}…"));
            lister.ListModels(effEndpoint, apiKey, type).Subscribe(
                ids =>
                {
                    SeedFetched(ctx, scope, ids.ToArray());
                    ctx.Host.UpdateData(ResultId(scope), SuccessMd(
                        $"Found {ids.Count} models — tick the ones to bring, then **Save provider**."));
                },
                ex =>
                {
                    // Graceful fallback: show the catalog defaults so the user can still
                    // pick + add ids manually even when the provider has no /models endpoint.
                    var defaults = src?.EffectiveModelIds ?? ImmutableArray<string>.Empty;
                    SeedFetched(ctx, scope, defaults.ToArray());
                    ctx.Host.UpdateData(ResultId(scope), ErrorMd(
                        $"Couldn't fetch models: {ex.Message} Showing defaults — you can also add ids manually."));
                });
        });
    }

    private static void SeedFetched(UiActionContext ctx, string scope, string[] ids)
    {
        var sel = new Dictionary<string, bool>();
        for (var i = 0; i < ids.Length; i++) sel[i.ToString()] = false;
        ctx.Host.UpdateData(SelId(scope), sel);
        ctx.Host.UpdateData(FetchedId(scope), ids);
    }

    private static void AddManualModel(UiActionContext ctx, string scope)
    {
        ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(FormId(scope)).Take(1)
            .CombineLatest(
                ctx.Host.Stream.GetDataStream<string[]>(FetchedId(scope)).Take(1).StartWith(Array.Empty<string>()),
                ctx.Host.Stream.GetDataStream<Dictionary<string, bool>>(SelId(scope)).Take(1).StartWith(new Dictionary<string, bool>()),
                (form, ids, sel) => (form, ids: ids ?? Array.Empty<string>(), sel: sel ?? new Dictionary<string, bool>()))
            .Take(1)
            .Subscribe(t =>
            {
                var manual = t.form?.GetValueOrDefault("manual")?.ToString()?.Trim();
                if (string.IsNullOrEmpty(manual)) return;
                if (t.ids.Contains(manual, StringComparer.OrdinalIgnoreCase))
                {
                    ctx.Host.UpdateData(ResultId(scope), PendingMd($"{manual} is already in the list."));
                    return;
                }
                var newIds = t.ids.Append(manual).ToArray();
                var newSel = new Dictionary<string, bool>(t.sel) { [(newIds.Length - 1).ToString()] = true };
                ctx.Host.UpdateData(SelId(scope), newSel);
                ctx.Host.UpdateData(FetchedId(scope), newIds);
                // Clear the manual field.
                var form = new Dictionary<string, object?>(t.form ?? new Dictionary<string, object?>()) { ["manual"] = "" };
                ctx.Host.UpdateData(FormId(scope), form);
            });
    }

    private static void SaveProvider(
        UiActionContext ctx,
        string scope,
        IReadOnlyDictionary<string, LanguageModelCatalogSource> byName,
        ModelProviderService providerService,
        string ownerPath,
        string? targetNamespace)
    {
        ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(FormId(scope)).Take(1)
            .CombineLatest(
                ctx.Host.Stream.GetDataStream<string[]>(FetchedId(scope)).Take(1).StartWith(Array.Empty<string>()),
                ctx.Host.Stream.GetDataStream<Dictionary<string, bool>>(SelId(scope)).Take(1).StartWith(new Dictionary<string, bool>()),
                (form, ids, sel) => (form, ids: ids ?? Array.Empty<string>(), sel: sel ?? new Dictionary<string, bool>()))
            .Take(1)
            .Subscribe(t =>
            {
                var type = t.form?.GetValueOrDefault("type")?.ToString() ?? "";
                var apiKey = t.form?.GetValueOrDefault("apiKey")?.ToString()?.Trim() ?? "";
                var name = t.form?.GetValueOrDefault("name")?.ToString()?.Trim();
                var endpoint = t.form?.GetValueOrDefault("endpoint")?.ToString()?.Trim();
                var src = byName.GetValueOrDefault(type);
                var effEndpoint = string.IsNullOrEmpty(endpoint) ? src?.DefaultEndpoint : endpoint;

                var checkedIds = t.ids
                    .Where((_, i) => t.sel.GetValueOrDefault(i.ToString()))
                    .ToList();

                if (string.IsNullOrEmpty(apiKey))
                {
                    ctx.Host.UpdateData(ResultId(scope), ErrorMd("An API key is required."));
                    return;
                }
                if (checkedIds.Count == 0)
                {
                    ctx.Host.UpdateData(ResultId(scope), ErrorMd("Tick at least one model to bring."));
                    return;
                }
                if (string.Equals(type, "OpenAICompatible", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(effEndpoint))
                {
                    ctx.Host.UpdateData(ResultId(scope), ErrorMd("Enter the base URL for an OpenAI-compatible provider."));
                    return;
                }

                var instanceId = DeriveInstanceId(type, name, effEndpoint);
                ctx.Host.UpdateData(ResultId(scope), PendingMd($"Saving {instanceId}…"));
                providerService.CreateProvider(
                        ownerPath, type, apiKey,
                        label: string.IsNullOrEmpty(name) ? null : name,
                        endpointOverride: effEndpoint,
                        modelIdsOverride: checkedIds,
                        instanceId: instanceId,
                        targetNamespace: targetNamespace)
                    .Subscribe(
                        result => ctx.Host.UpdateData(ResultId(scope), SuccessMd(
                            $"Saved **{instanceId}** — {result.ModelNodes.Count} model(s) ready.")),
                        ex => ctx.Host.UpdateData(ResultId(scope), ErrorMd(ex.Message)));
            });
    }

    /// <summary>
    /// Node id for the new provider. A generic OpenAI-compatible provider derives a
    /// distinct id from the given name (or URL host) so several gateways coexist;
    /// named providers key by their type (one instance per type).
    /// </summary>
    private static string DeriveInstanceId(string type, string? name, string? endpoint)
    {
        if (!string.Equals(type, "OpenAICompatible", StringComparison.OrdinalIgnoreCase))
            return type;
        var fromName = Slug(name);
        if (!string.IsNullOrEmpty(fromName)) return fromName;
        if (!string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            var fromHost = Slug(uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(fromHost)) return fromHost;
        }
        return "openai-compatible";
    }

    private static string Slug(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var arr = s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(arr).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Configured providers + active-models / default-model management
    // ════════════════════════════════════════════════════════════════════════

    private static UiControl BuildProviderList(
        IReadOnlyList<ProviderInfo> providers, ModelProviderService service, string scope)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        foreach (var p in providers)
        {
            var endpointLabel = string.IsNullOrEmpty(p.Endpoint) ? "(default)" : p.Endpoint!;
            var modelsLabel = p.ModelIds.Count == 0 ? "no models" : $"{p.ModelIds.Count} model(s)";

            var row = Controls.Stack.WithOrientation(Orientation.Horizontal)
                .WithStyle("padding: 12px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; align-items: center; gap: 16px;");
            row = row.WithView(Controls.Markdown(
                $"**{p.Label ?? p.Provider}** ({p.Provider})  \n" +
                $"Endpoint: {endpointLabel} · Key: `{p.ApiKeyFingerprint}` · {modelsLabel} · " +
                $"Created {p.CreatedAt:yyyy-MM-dd}").WithStyle("flex: 1;"));

            var captured = p;
            row = row.WithView(Controls.Button("Delete")
                .WithAppearance(Appearance.Outline)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(ResultId(scope), PendingMd($"Deleting {captured.Label ?? captured.Provider}…"));
                    service.DeleteProvider(captured.NodePath).Subscribe(
                        ok => ctx.Host.UpdateData(ResultId(scope), ok
                            ? SuccessMd($"Deleted {captured.Label ?? captured.Provider}.")
                            : ErrorMd($"Failed to delete {captured.NodePath}.")),
                        ex => ctx.Host.UpdateData(ResultId(scope), ErrorMd(ex.Message)));
                    return Task.CompletedTask;
                }));
            container = container.WithView(row);
        }
        return container;
    }

    private static UiControl BuildModelSelectionList(
        IReadOnlyList<MeshNode> modelNodes,
        ImmutableArray<string> selected,
        ModelProviderService service,
        string ownerPath,
        string scope)
    {
        var byProvider = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in modelNodes)
        {
            var providerPath = ProviderPathOf(n);
            if (string.IsNullOrEmpty(providerPath)) continue;
            if (!byProvider.TryGetValue(providerPath, out var l))
                byProvider[providerPath] = l = new List<string>();
            l.Add(n.Name ?? n.Id);
        }

        if (byProvider.Count == 0)
            return Controls.Markdown("_No models discovered._");

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
            row = row.WithView(Controls.Markdown($"**{providerPath}**  \n{preview}").WithStyle("flex: 1;"));

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
                            ok => ctx.Host.UpdateData(ResultId(scope), ok
                                ? SuccessMd(capturedActive ? $"Removed {capturedPath}." : $"Added {capturedPath}.")
                                : ErrorMd("Failed to update selection.")),
                            ex => ctx.Host.UpdateData(ResultId(scope), ErrorMd(ex.Message)));
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

    // ════════════════════════════════════════════════════════════════════════
    //  CLI card — login status + connect (state machine, markdown-rendered)
    // ════════════════════════════════════════════════════════════════════════

    private static UiControl BuildCliCard(
        LayoutAreaHost host,
        LanguageModelCatalogSource src,
        ConnectSessionManager? sessionManager,
        ModelProviderService providerService,
        string ownerPath,
        string userId)
    {
        var provider = src.ProviderName.Equals("Copilot", StringComparison.OrdinalIgnoreCase)
            ? ConnectProvider.Copilot
            : ConnectProvider.ClaudeCode;
        var stateDataId = $"cliState:{src.ProviderName}";

        var card = Controls.Stack.WithWidth("100%")
            .WithStyle("padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; gap: 12px; margin-bottom: 12px;");
        card = card.WithView(Controls.Markdown($"**{src.EffectiveLabel}** · _CLI_"));

        if (sessionManager is null || !sessionManager.Supports(provider))
            return card.WithView(Controls.Markdown("_Connect is not available in this deployment._"));

        host.UpdateData(stateDataId, RenderCliBody(provider, src, stateDataId, sessionManager, ownerPath, userId));

        var configDir = ResolveConfigDir(host, userId, provider);
        sessionManager.IsLoggedIn(provider, configDir).Take(1).Subscribe(loggedIn =>
        {
            if (loggedIn)
                host.UpdateData(stateDataId, RenderConnectedBody(src, provider, stateDataId, sessionManager, ownerPath, userId, loginName: null));
        });

        return card.WithView((h, _) =>
            h.Stream.GetDataStream<UiControl>(stateDataId)
                .StartWith((UiControl)Controls.Markdown("…")));
    }

    private static UiControl RenderCliBody(
        ConnectProvider provider, LanguageModelCatalogSource src, string stateDataId,
        ConnectSessionManager sessionManager, string ownerPath, string userId)
    {
        var body = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        body = body.WithView(Controls.Markdown("⚪ Not connected — uses your subscription"));
        body = body.WithView(Controls.Button($"Connect {src.EffectiveLabel}")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                var host = ctx.Host;
                var configDir = ResolveConfigDir(host, userId, provider);
                host.UpdateData(stateDataId, Controls.Markdown("🟡 Connecting…"));
                sessionManager.StartConnect(ownerPath, provider, configDir).Subscribe(
                    st => host.UpdateData(stateDataId, RenderConnectingOrTerminal(provider, st, src, stateDataId, sessionManager, ownerPath, userId)),
                    ex => host.UpdateData(stateDataId, RenderError(provider, src, ex.Message, stateDataId, sessionManager, ownerPath, userId)));
                return Task.CompletedTask;
            }));
        return body;
    }

    private static UiControl RenderConnectingOrTerminal(
        ConnectProvider provider, ConnectStatus status, LanguageModelCatalogSource src, string stateDataId,
        ConnectSessionManager sessionManager, string ownerPath, string userId) =>
        status switch
        {
            ConnectStatus.Connecting c => RenderConnecting(provider, c.Challenge, src, stateDataId, sessionManager, ownerPath, userId),
            ConnectStatus.Connected => RenderConnectedBody(src, provider, stateDataId, sessionManager, ownerPath, userId, loginName: null),
            ConnectStatus.Error err => RenderError(provider, src, err.Reason, stateDataId, sessionManager, ownerPath, userId),
            _ => RenderCliBody(provider, src, stateDataId, sessionManager, ownerPath, userId),
        };

    private static UiControl RenderConnecting(
        ConnectProvider provider, ConnectChallenge challenge, LanguageModelCatalogSource src, string stateDataId,
        ConnectSessionManager sessionManager, string ownerPath, string userId)
    {
        var body = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        body = body.WithView(Controls.Markdown("🟡 Connecting…"));

        if (challenge.RequiresPastedCode)
        {
            body = body.WithView(Controls.Markdown(
                $"1. Authorize in your browser: [{challenge.VerificationUrl}]({challenge.VerificationUrl})  \n" +
                "2. Paste the code shown:"));
            body = body.WithView(BuildPasteCodeRow(provider, src, stateDataId, sessionManager, ownerPath, userId));
        }
        else
        {
            var code = string.IsNullOrEmpty(challenge.UserCode) ? "" : $"\n\n`{challenge.UserCode}`";
            body = body.WithView(Controls.Markdown(
                $"Enter this code at [{challenge.VerificationUrl}]({challenge.VerificationUrl}){code}\n\n⏳ auto-checking…"));
        }

        body = body.WithView(Controls.Button("Cancel")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx =>
            {
                sessionManager.Cancel(ownerPath, provider);
                ctx.Host.UpdateData(stateDataId, RenderCliBody(provider, src, stateDataId, sessionManager, ownerPath, userId));
                return Task.CompletedTask;
            }));
        return body;
    }

    private static UiControl BuildPasteCodeRow(
        ConnectProvider provider, LanguageModelCatalogSource src, string stateDataId,
        ConnectSessionManager sessionManager, string ownerPath, string userId)
    {
        var codeDataId = $"cliCode:{src.ProviderName}";
        var row = Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap: 8px; align-items: flex-end;");
        row = row.WithView(new TextFieldControl(new JsonPointerReference("code"))
        {
            Label = "Code",
            Placeholder = "paste here",
            DataContext = LayoutAreaReference.GetDataPointer(codeDataId),
        }.WithWidth("280px"));
        row = row.WithView(Controls.Button("Submit")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                var host = ctx.Host;
                host.Stream.GetDataStream<Dictionary<string, object?>>(codeDataId).Take(1).Subscribe(data =>
                {
                    var code = data?.GetValueOrDefault("code")?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(code))
                    {
                        host.UpdateData(ResultId(UserScope), ErrorMd("Please paste the code shown."));
                        return;
                    }
                    sessionManager.SubmitCode(ownerPath, provider, code).Subscribe(
                        st => host.UpdateData(stateDataId, RenderConnectingOrTerminal(provider, st, src, stateDataId, sessionManager, ownerPath, userId)),
                        ex => host.UpdateData(stateDataId, RenderError(provider, src, ex.Message, stateDataId, sessionManager, ownerPath, userId)));
                });
                return Task.CompletedTask;
            }));
        return row;
    }

    private static UiControl RenderConnectedBody(
        LanguageModelCatalogSource src, ConnectProvider provider, string stateDataId,
        ConnectSessionManager sessionManager, string ownerPath, string userId, string? loginName)
    {
        var who = string.IsNullOrEmpty(loginName) ? "" : $" as {loginName}";
        var body = Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("gap: 16px; align-items: center;");
        body = body.WithView(Controls.Markdown($"🟢 Connected{who}").WithStyle("flex: 1;"));
        body = body.WithView(Controls.Button("Disconnect")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx =>
            {
                sessionManager.Cancel(ownerPath, provider);
                var providerService = ctx.Host.Hub.ServiceProvider.GetRequiredService<ModelProviderService>();
                providerService.DeleteProvider($"{ModelProviderNodeType.UserNamespacePath(ownerPath)}/{src.ProviderName}").Subscribe(
                    _ => ctx.Host.UpdateData(stateDataId, RenderCliBody(provider, src, stateDataId, sessionManager, ownerPath, userId)),
                    ex => ctx.Host.UpdateData(ResultId(UserScope), ErrorMd(ex.Message)));
                return Task.CompletedTask;
            }));
        return body;
    }

    private static UiControl RenderError(
        ConnectProvider provider, LanguageModelCatalogSource src, string reason, string stateDataId,
        ConnectSessionManager sessionManager, string ownerPath, string userId)
    {
        var body = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        body = body.WithView(Controls.Markdown($"🔴 {reason}"));
        body = body.WithView(Controls.Button("Retry")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(stateDataId, RenderCliBody(provider, src, stateDataId, sessionManager, ownerPath, userId));
                return Task.CompletedTask;
            }));
        return body;
    }

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

    // ── status helpers (markdown) ─────────────────────────────────────────────
    private static string SuccessMd(string m) => $"✅ {m}";
    private static string ErrorMd(string m) => $"⚠️ {m}";
    private static string PendingMd(string m) => $"⏳ {m}";
}
