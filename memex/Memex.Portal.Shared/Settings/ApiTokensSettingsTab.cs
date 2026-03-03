using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Memex.Portal.Shared.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Settings tab for managing API tokens.
/// Registered via AddApiTokensSettingsTab() on MessageHubConfiguration.
/// </summary>
public static class ApiTokensSettingsTab
{
    public const string TabId = "ApiTokens";

    public static MessageHubConfiguration AddApiTokensSettingsTab(
        this MessageHubConfiguration config)
    {
        return config.AddSettingsMenuItems(
            new SettingsMenuItemDefinition(
                Id: TabId,
                Label: "API Tokens",
                ContentBuilder: BuildApiTokensContent,
                Group: "Security",
                Icon: FluentIcons.Key(),
                Order: 230,
                RequiredPermission: Permission.Read));
    }

    internal static UiControl BuildApiTokensContent(
        LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var tokenService = host.Hub.ServiceProvider.GetRequiredService<ApiTokenService>();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var userContext = accessService?.Context;
        var userId = userContext?.ObjectId ?? "";
        var userName = userContext?.Name ?? "";
        var userEmail = userContext?.Email ?? "";

        stack = stack.WithView(Controls.H2("API Tokens").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;\">" +
            "Manage personal access tokens for API and MCP access.</p>"));

        const string createDataId = "apiTokenCreate";
        const string resultDataId = "apiTokenResult";
        const string tokenListRefreshId = "apiTokenListRefresh";

        host.UpdateData(createDataId, new Dictionary<string, object?>
        {
            ["label"] = "",
            ["expiryDays"] = 0
        });
        // NOTE: Do NOT initialize resultDataId here — CreateTokenAsync saves a MeshNode
        // which triggers the workspace stream, causing the Settings page to rebuild.
        // If we set resultDataId="" here, the rebuild would overwrite the token display
        // that the click handler just set. Instead, the reactive view uses .StartWith().
        host.UpdateData(tokenListRefreshId, DateTimeOffset.UtcNow.Ticks);

        // Create token form
        var createSection = Controls.Stack.WithWidth("100%")
            .WithStyle("padding: 16px; background: var(--neutral-layer-2); border-radius: 8px; gap: 12px; margin-bottom: 24px;");

        createSection = createSection.WithView(
            Controls.Html("<h3 style=\"margin: 0 0 8px 0; font-size: 1rem;\">Create New Token</h3>"));

        var formRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: flex-end; flex-wrap: wrap;");

        formRow = formRow.WithView(new TextFieldControl(new JsonPointerReference("label"))
        {
            Label = "Label",
            Placeholder = "e.g. Claude Code",
            DataContext = LayoutAreaReference.GetDataPointer(createDataId)
        });

        formRow = formRow.WithView(new NumberFieldControl(new JsonPointerReference("expiryDays"), "Int32")
        {
            Label = "Expires in (days)",
            DataContext = LayoutAreaReference.GetDataPointer(createDataId)
        });

        formRow = formRow.WithView(Controls.Button("Generate Token")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(async ctx =>
            {
                var label = "";
                var expiryDays = 0;
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(createDataId)
                    .Take(1)
                    .Subscribe(data =>
                    {
                        label = data?.GetValueOrDefault("label")?.ToString()?.Trim() ?? "";
                        if (data?.GetValueOrDefault("expiryDays") is { } ed)
                            int.TryParse(ed.ToString(), out expiryDays);
                    });

                if (string.IsNullOrEmpty(label))
                {
                    ctx.Host.UpdateData(resultDataId,
                        "<p style=\"color: var(--warning-color);\">Please enter a label.</p>");
                    return;
                }

                DateTimeOffset? expiresAt = expiryDays > 0
                    ? DateTimeOffset.UtcNow.AddDays(expiryDays)
                    : null;

                try
                {
                    var (rawToken, _) = await tokenService.CreateTokenAsync(
                        userId, userName, userEmail, label, expiresAt);

                    ctx.Host.UpdateData(resultDataId,
                        "<div style=\"padding: 12px; background: var(--warning-fill-rest); border-radius: 6px; margin-bottom: 16px;\">" +
                        "<strong>Copy your token now — it won't be shown again!</strong>" +
                        $"<div style=\"margin-top: 8px; font-family: monospace; background: var(--neutral-layer-4); padding: 8px 12px; " +
                        $"border-radius: 4px; word-break: break-all; user-select: all;\">{Esc(rawToken)}</div></div>");

                    ctx.Host.UpdateData(tokenListRefreshId, DateTimeOffset.UtcNow.Ticks);
                }
                catch (Exception ex)
                {
                    ctx.Host.UpdateData(resultDataId,
                        $"<p style=\"color: #f87171;\">Error: {Esc(ex.Message)}</p>");
                }
            }));

        createSection = createSection.WithView(formRow);
        stack = stack.WithView(createSection);

        // Result area (newly created token display)
        stack = stack.WithView((h, _) =>
            h.Stream.GetDataStream<string>(resultDataId)
                .Select(html => string.IsNullOrEmpty(html)
                    ? (UiControl?)Controls.Stack
                    : (UiControl?)Controls.Html(html))
                .StartWith((UiControl?)Controls.Stack));

        // Token list
        stack = stack.WithView(
            Controls.Html("<h3 style=\"margin: 0 0 12px 0; font-size: 1rem;\">Your Tokens</h3>"));

        stack = stack.WithView((h, _) =>
            h.Stream.GetDataStream<long>(tokenListRefreshId)
                .SelectMany(async _ =>
                {
                    if (string.IsNullOrEmpty(userId))
                        return (UiControl?)Controls.Html(
                            "<p style=\"color: var(--neutral-foreground-hint);\">No user identity found.</p>");

                    var tokens = await tokenService.GetTokensForUserAsync(userId);

                    if (tokens.Count == 0)
                        return (UiControl?)Controls.Html(
                            "<p style=\"color: var(--neutral-foreground-hint);\">No tokens yet. Create one above.</p>");

                    return (UiControl?)BuildTokenList(tokens, tokenService, tokenListRefreshId, resultDataId);
                }));

        return stack;
    }

    private static UiControl BuildTokenList(
        List<ApiTokenInfo> tokens,
        ApiTokenService tokenService,
        string tokenListRefreshId,
        string resultDataId)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");

        foreach (var token in tokens)
        {
            var status = token.IsRevoked ? "Revoked"
                : (token.ExpiresAt.HasValue && token.ExpiresAt.Value < DateTimeOffset.UtcNow)
                    ? "Expired" : "Active";
            var statusColor = status == "Active" ? "#4ade80"
                : status == "Expired" ? "#fbbf24" : "#f87171";

            var row = Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("padding: 12px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; align-items: center; gap: 16px;");

            row = row.WithView(Controls.Html(
                $"<div style=\"flex: 1;\">" +
                $"<strong>{Esc(token.Label)}</strong>" +
                $"<div style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint);\">" +
                $"ID: {Esc(token.HashPrefix)} | " +
                $"Created: {token.CreatedAt:yyyy-MM-dd} | " +
                $"Expires: {(token.ExpiresAt?.ToString("yyyy-MM-dd") ?? "Never")} | " +
                $"Last used: {(token.LastUsedAt?.ToString("yyyy-MM-dd HH:mm") ?? "Never")}" +
                "</div></div>"));

            row = row.WithView(Controls.Html(
                $"<span style=\"color: {statusColor}; font-weight: 600; font-size: 0.85rem;\">{status}</span>"));

            if (!token.IsRevoked)
            {
                var captured = token;
                row = row.WithView(Controls.Button("Revoke")
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(async ctx =>
                    {
                        var success = await tokenService.RevokeTokenAsync(captured.NodePath);
                        ctx.Host.UpdateData(resultDataId, success
                            ? $"<p style=\"color: #4ade80;\">Token '{Esc(captured.Label)}' revoked.</p>"
                            : "<p style=\"color: #f87171;\">Failed to revoke token.</p>");
                        ctx.Host.UpdateData(tokenListRefreshId, DateTimeOffset.UtcNow.Ticks);
                    }));
            }

            container = container.WithView(row);
        }

        return container;
    }

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);
}
