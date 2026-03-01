using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Admin;

/// <summary>
/// Layout areas for the Platform node type.
/// Provides a Splitter layout with NavMenu (left) and content pane (right),
/// following the same pattern as SettingsLayoutArea.
/// Tabs: Overview, Auth Providers, Administrators.
/// </summary>
public static class PlatformLayoutAreas
{
    public const string OverviewTab = "Overview";
    public const string AuthProvidersTab = "AuthProviders";
    public const string AdministratorsTab = "Administrators";

    private const string PlatformArea = "Platform";

    public static MessageHubConfiguration AddPlatformViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(PlatformArea)
            .WithView(PlatformArea, Platform)
            .WithView(MeshNodeLayoutAreas.ThumbnailArea, MeshNodeLayoutAreas.Thumbnail)
            .WithView(MeshNodeLayoutAreas.CreateNodeArea, CreateLayoutArea.Create)
            .WithView(MeshNodeLayoutAreas.DeleteArea, DeleteLayoutArea.Delete));

    public static IObservable<UiControl?> Platform(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var hubAddress = host.Hub.Address;
        var tabId = host.Reference.Id?.ToString();

        if (string.IsNullOrEmpty(tabId))
            tabId = OverviewTab;

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return (UiControl?)BuildPlatformPage(host, node, hubAddress, hubPath, tabId);
        });
    }

    private static UiControl BuildPlatformPage(
        LayoutAreaHost host,
        MeshNode? node,
        object hubAddress,
        string hubPath,
        string tabId)
    {
        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("calc(100vh - 100px)"))
            .WithView(
                BuildNavMenu(hubAddress, hubPath, node),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                BuildContentPane(host, hubPath, tabId),
                skin => skin.WithSize("*")
            );
    }

    private static UiControl BuildNavMenu(object hubAddress, string hubPath, MeshNode? node)
    {
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        // Back to home
        navMenu = navMenu.WithView(
            new NavLinkControl(node?.Name ?? "Platform", FluentIcons.ArrowLeft(), "/")
        );

        // Overview tab
        var overviewHref = new LayoutAreaReference(PlatformArea) { Id = OverviewTab }.ToHref(hubAddress);
        navMenu = navMenu.WithView(
            new NavLinkControl("Overview", FluentIcons.Info(), overviewHref)
        );

        // Configuration group
        var configGroup = new NavGroupControl("Configuration")
            .WithIcon(FluentIcons.Settings())
            .WithSkin(s => s.WithExpanded(true));

        var authHref = new LayoutAreaReference(PlatformArea) { Id = AuthProvidersTab }.ToHref(hubAddress);
        configGroup = configGroup.WithView(
            new NavLinkControl("Auth Providers", FluentIcons.Shield(), authHref)
        );

        var adminsHref = new LayoutAreaReference(PlatformArea) { Id = AdministratorsTab }.ToHref(hubAddress);
        configGroup = configGroup.WithView(
            new NavLinkControl("Administrators", FluentIcons.People(), adminsHref)
        );

        navMenu = navMenu.WithNavGroup(configGroup);

        return navMenu;
    }

    private static UiControl BuildContentPane(LayoutAreaHost host, string hubPath, string tabId)
    {
        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; height: 100%; overflow: auto;");

        return tabId switch
        {
            OverviewTab => BuildOverviewTab(host, hubPath, stack),
            AuthProvidersTab => BuildAuthProvidersTab(host, stack),
            AdministratorsTab => BuildAdministratorsTab(host, stack),
            _ => BuildOverviewTab(host, hubPath, stack),
        };
    }

    // ── Overview Tab ───────────────────────────────────────────────────

    private static UiControl BuildOverviewTab(LayoutAreaHost host, string hubPath, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Platform Overview").WithStyle("margin: 0 0 24px 0;"));

        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        if (persistence == null)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: var(--warning-color);\">Persistence service not available.</p>"));
            return stack;
        }

        // Load initialization data asynchronously via observable
        stack = stack.WithView((h, _) =>
        {
            return Observable.FromAsync(async ct =>
            {
                var adminService = new AdminService(persistence);
                var init = await adminService.GetInitializationAsync(ct);

                var html = "<div style=\"display: grid; grid-template-columns: auto 1fr; gap: 8px 24px; max-width: 500px;\">";

                html += "<span style=\"font-weight: 600; color: var(--neutral-foreground-hint);\">Version</span>";
                html += $"<span>{init?.Version ?? "Unknown"}</span>";

                html += "<span style=\"font-weight: 600; color: var(--neutral-foreground-hint);\">Initialized At</span>";
                html += $"<span>{(init is { InitializedAt: var ts } && ts != default ? ts.ToString("yyyy-MM-dd HH:mm:ss UTC") : "N/A")}</span>";

                html += "<span style=\"font-weight: 600; color: var(--neutral-foreground-hint);\">Initialized By</span>";
                html += $"<span>{init?.InitializedBy ?? "N/A"}</span>";

                html += "<span style=\"font-weight: 600; color: var(--neutral-foreground-hint);\">Storage Type</span>";
                html += $"<span>{h.Hub.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>()?["Graph:Storage:Type"] ?? "FileSystem"}</span>";

                html += "</div>";

                return (UiControl?)Controls.Html(html);
            });
        });

        return stack;
    }

    // ── Auth Providers Tab ─────────────────────────────────────────────

    private static UiControl BuildAuthProvidersTab(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Authentication Providers").WithStyle("margin: 0 0 24px 0;"));

        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        if (persistence == null)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: var(--warning-color);\">Persistence service not available.</p>"));
            return stack;
        }

        // Build the form using observable data
        stack = stack.WithView((h, _) =>
        {
            return Observable.FromAsync(async ct =>
            {
                var adminService = new AdminService(persistence);
                var settings = await adminService.GetAuthProviderSettingsAsync(ct);
                return (UiControl?)BuildAuthProvidersForm(h, settings, adminService);
            });
        });

        return stack;
    }

    private static UiControl BuildAuthProvidersForm(
        LayoutAreaHost host,
        AuthProviderSettings settings,
        AdminService adminService)
    {
        const string dataId = "platformAuthProviders";

        // Build a flat dictionary for form binding
        var formData = new Dictionary<string, object?>
        {
            ["enableDevLogin"] = settings.EnableDevLogin,
            ["keyVaultUri"] = settings.KeyVaultUri ?? ""
        };

        foreach (var def in OAuthProviderDefinitions.All.Values)
        {
            var name = def.Name;
            var entry = settings.Providers.GetValueOrDefault(name);
            formData[$"{name}_enabled"] = entry?.Enabled ?? false;
            formData[$"{name}_appId"] = entry?.AppId ?? "";
            formData[$"{name}_secretName"] = entry?.KeyVaultSecretName ?? "";
            formData[$"{name}_tenantId"] = entry?.TenantId ?? "";
        }

        host.UpdateData(dataId, formData);

        var form = Controls.Stack.WithWidth("100%").WithStyle("gap: 16px;");

        // Dev login switch
        form = form.WithView(Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("align-items: center; gap: 12px;")
            .WithView(new CheckBoxControl(new JsonPointerReference("enableDevLogin"))
            {
                Label = "Enable Developer Login",
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            })
        );

        // KeyVault URI
        form = form.WithView(new TextFieldControl(new JsonPointerReference("keyVaultUri"))
        {
            Label = "Azure KeyVault URI",
            Placeholder = "https://my-vault.vault.azure.net/",
            DataContext = LayoutAreaReference.GetDataPointer(dataId)
        });

        // Divider
        form = form.WithView(Controls.Html("<hr style=\"border: none; border-top: 1px solid var(--neutral-stroke-rest); margin: 8px 0;\"/>"));
        form = form.WithView(Controls.H3("External Providers").WithStyle("margin: 0;"));

        // Provider sections
        foreach (var def in OAuthProviderDefinitions.All.Values)
        {
            var name = def.Name;
            var providerStack = Controls.Stack.WithWidth("100%")
                .WithStyle("padding: 12px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; gap: 8px;");

            providerStack = providerStack.WithView(
                new CheckBoxControl(new JsonPointerReference($"{name}_enabled"))
                {
                    Label = def.DisplayName,
                    DataContext = LayoutAreaReference.GetDataPointer(dataId)
                });

            providerStack = providerStack.WithView(
                new TextFieldControl(new JsonPointerReference($"{name}_appId"))
                {
                    Label = "App ID (Client ID)",
                    Placeholder = "e.g., 12345678-abcd-...",
                    DataContext = LayoutAreaReference.GetDataPointer(dataId)
                });

            providerStack = providerStack.WithView(
                new TextFieldControl(new JsonPointerReference($"{name}_secretName"))
                {
                    Label = "KeyVault Secret Name",
                    Placeholder = $"e.g., memex-{name.ToLowerInvariant()}-client-secret",
                    DataContext = LayoutAreaReference.GetDataPointer(dataId)
                });

            if (def.HasTenantId)
            {
                providerStack = providerStack.WithView(
                    new TextFieldControl(new JsonPointerReference($"{name}_tenantId"))
                    {
                        Label = "Tenant ID",
                        Placeholder = "common (for multi-tenant)",
                        DataContext = LayoutAreaReference.GetDataPointer(dataId)
                    });
            }

            form = form.WithView(providerStack);
        }

        // Save button
        form = form.WithView(Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("margin-top: 16px; gap: 8px;")
            .WithView(Controls.Button("Save Auth Providers")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(dataId)
                        .Take(1)
                        .Subscribe(async data =>
                        {
                            try
                            {
                                var updated = BuildAuthProviderSettings(data);
                                await adminService.SaveAuthProviderSettingsAsync(updated);
                                ctx.Host.UpdateData("platformSaveResult",
                                    "<p style=\"color: #4ade80;\">Auth providers saved. Restart the application for changes to take effect.</p>");
                            }
                            catch (Exception ex)
                            {
                                ctx.Host.UpdateData("platformSaveResult",
                                    $"<p style=\"color: #f87171;\">Save failed: {Escape(ex.Message)}</p>");
                            }
                        });
                })));

        // Result area
        host.UpdateData("platformSaveResult", "");
        form = form.WithView((h, _) =>
            h.Stream.GetDataStream<string>("platformSaveResult")
                .Select(html => string.IsNullOrEmpty(html)
                    ? (UiControl)Controls.Stack
                    : (UiControl)Controls.Html(html)));

        return form;
    }

    private static AuthProviderSettings BuildAuthProviderSettings(Dictionary<string, object?>? data)
    {
        if (data == null)
            return new AuthProviderSettings();

        var providers = new Dictionary<string, AuthProviderEntry>();
        foreach (var def in OAuthProviderDefinitions.All.Values)
        {
            var name = def.Name;
            var enabled = GetBool(data, $"{name}_enabled");
            if (enabled)
            {
                providers[name] = new AuthProviderEntry
                {
                    Enabled = true,
                    AppId = GetString(data, $"{name}_appId"),
                    KeyVaultSecretName = GetString(data, $"{name}_secretName"),
                    TenantId = NullIfEmpty(GetString(data, $"{name}_tenantId"))
                };
            }
        }

        return new AuthProviderSettings
        {
            EnableDevLogin = GetBool(data, "enableDevLogin"),
            KeyVaultUri = NullIfEmpty(GetString(data, "keyVaultUri")),
            Providers = providers
        };
    }

    // ── Administrators Tab ─────────────────────────────────────────────

    private static UiControl BuildAdministratorsTab(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Platform Administrators").WithStyle("margin: 0 0 16px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;\">" +
            "User IDs with admin access to Platform Settings (one per line).</p>"));

        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        if (persistence == null)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: var(--warning-color);\">Persistence service not available.</p>"));
            return stack;
        }

        stack = stack.WithView((h, _) =>
        {
            return Observable.FromAsync(async ct =>
            {
                var adminService = new AdminService(persistence);
                var settings = await adminService.GetAdminSettingsAsync(ct);
                return (UiControl?)BuildAdministratorsForm(h, settings, adminService);
            });
        });

        return stack;
    }

    private static UiControl BuildAdministratorsForm(
        LayoutAreaHost host,
        AdminSettings settings,
        AdminService adminService)
    {
        const string dataId = "platformAdminUsers";
        var adminUsersText = string.Join("\n", settings.AdminUsers);

        host.UpdateData(dataId, new Dictionary<string, object?> { ["adminUsers"] = adminUsersText });

        var form = Controls.Stack.WithWidth("100%").WithStyle("gap: 12px;");

        form = form.WithView(new TextFieldControl(new JsonPointerReference("adminUsers"))
        {
            Label = "Admin User IDs",
            Placeholder = "e.g., Roland\nAlice\nadmin@company.com",
            DataContext = LayoutAreaReference.GetDataPointer(dataId)
        });

        // Save button
        form = form.WithView(Controls.Stack.WithOrientation(Orientation.Horizontal).WithStyle("margin-top: 12px; gap: 8px;")
            .WithView(Controls.Button("Save Administrators")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(dataId)
                        .Take(1)
                        .Subscribe(async data =>
                        {
                            try
                            {
                                var text = GetString(data, "adminUsers");
                                var adminUsers = text
                                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();

                                await adminService.SaveAdminSettingsAsync(new AdminSettings { AdminUsers = adminUsers });
                                ctx.Host.UpdateData("adminSaveResult",
                                    "<p style=\"color: #4ade80;\">Administrators saved successfully.</p>");
                            }
                            catch (Exception ex)
                            {
                                ctx.Host.UpdateData("adminSaveResult",
                                    $"<p style=\"color: #f87171;\">Save failed: {Escape(ex.Message)}</p>");
                            }
                        });
                })));

        // Result area
        host.UpdateData("adminSaveResult", "");
        form = form.WithView((h, _) =>
            h.Stream.GetDataStream<string>("adminSaveResult")
                .Select(html => string.IsNullOrEmpty(html)
                    ? (UiControl)Controls.Stack
                    : (UiControl)Controls.Html(html)));

        return form;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static bool GetBool(Dictionary<string, object?>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out var val) || val == null)
            return false;
        if (val is bool b) return b;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.True) return true;
        if (val is JsonElement jf && jf.ValueKind == JsonValueKind.False) return false;
        return bool.TryParse(val.ToString(), out var parsed) && parsed;
    }

    private static string GetString(Dictionary<string, object?>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out var val) || val == null)
            return "";
        if (val is JsonElement je)
            return je.GetString() ?? je.ToString();
        return val.ToString() ?? "";
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Escape(string s) => System.Web.HttpUtility.HtmlEncode(s);
}
