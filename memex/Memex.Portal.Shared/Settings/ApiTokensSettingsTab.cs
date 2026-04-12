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
            ["expiryDays"] = 365
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
        }.WithWidth("240px"));

        formRow = formRow.WithView(new NumberFieldControl(new JsonPointerReference("expiryDays"), "Int32")
        {
            Label = "Expires in (days)",
            DataContext = LayoutAreaReference.GetDataPointer(createDataId)
        }.WithWidth("140px"));

        // Inline token result area driven by a data-bound render ID.
        // Using data binding (not UpdateArea with DialogControl) so the result survives
        // workspace stream rebuilds triggered by CreateNodeAsync. A separate counter
        // key guarantees each Generate click produces a distinct stream emission (so
        // repeated tokens show a dialog even if the raw token string is identical).
        const string tokenRenderKey = "apiTokenRenderKey";

        formRow = formRow.WithView(Controls.Button("Generate Token")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                // Immediate feedback so user knows click fired.
                ctx.Host.UpdateData(resultDataId,
                    "<p style=\"padding: 8px 12px; color: var(--neutral-foreground-hint);\">Starting…</p>");

                // Subscribe (no await) to read current form data, then kick off the token
                // creation via the service's observable API — fires hub.Post + RegisterCallback
                // internally, never blocks the click handler.
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(createDataId)
                    .Take(1)
                    .Subscribe(data =>
                    {
                        var label = data?.GetValueOrDefault("label")?.ToString()?.Trim() ?? "";
                        var expiryDays = 0;
                        if (data?.GetValueOrDefault("expiryDays") is { } ed)
                            int.TryParse(ed.ToString(), out expiryDays);

                        if (string.IsNullOrEmpty(label))
                        {
                            ctx.Host.UpdateData(resultDataId,
                                "<p style=\"padding: 8px 12px; background: var(--warning-fill-rest, #fef3c7); " +
                                "color: var(--warning-color, #92400e); border-radius: 6px;\">Please enter a label.</p>");
                            return;
                        }

                        DateTimeOffset? expiresAt = expiryDays > 0
                            ? DateTimeOffset.UtcNow.AddDays(expiryDays)
                            : null;

                        ctx.Host.UpdateData(resultDataId,
                            $"<p style=\"padding: 8px 12px; color: var(--neutral-foreground-hint);\">" +
                            $"Creating token '{Esc(label)}'…</p>");

                        // Observable-based service call — subscribes to the underlying
                        // hub.Post + RegisterCallback pipeline without any await.
                        tokenService.CreateToken(userId, userName, userEmail, label, expiresAt)
                            .Subscribe(
                                result =>
                                {
                                    var rawToken = result.RawToken;
                                    var tokenHtml =
                                        "<div style=\"padding: 16px; background: var(--neutral-layer-2); border-radius: 8px; " +
                                        "border: 1px solid var(--warning-color, #d4a72c); margin-bottom: 16px; " +
                                        "width: 100%; box-sizing: border-box;\">" +
                                        "<div style=\"font-weight: 600; margin-bottom: 8px; color: var(--warning-color, #92400e);\">" +
                                        "Copy your token now — it won't be shown again!</div>" +
                                        "<div style=\"font-family: ui-monospace, monospace; background: var(--neutral-layer-4); " +
                                        "padding: 12px; border-radius: 6px; word-break: break-all; user-select: all; " +
                                        "border: 1px solid var(--neutral-stroke-rest); cursor: pointer;\" " +
                                        "onclick=\"var r=document.createRange();r.selectNodeContents(this);" +
                                        "var s=window.getSelection();s.removeAllRanges();s.addRange(r);" +
                                        "navigator.clipboard&&navigator.clipboard.writeText(this.textContent);\">" +
                                        $"{Esc(rawToken)}</div>" +
                                        "<div style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint); " +
                                        "margin-top: 6px;\">Click the token above to select &amp; copy to clipboard.</div>" +
                                        "</div>";

                                    ctx.Host.UpdateData(resultDataId, tokenHtml);
                                    ctx.Host.UpdateData(tokenRenderKey, DateTimeOffset.UtcNow.Ticks);
                                    ctx.Host.UpdateData(tokenListRefreshId, DateTimeOffset.UtcNow.Ticks);
                                },
                                ex => ctx.Host.UpdateData(resultDataId,
                                    "<p style=\"padding: 8px 12px; color: #f87171; background: var(--neutral-layer-2); " +
                                    $"border-radius: 6px;\">Error: {Esc(ex.Message)}</p>"));
                    });

                return Task.CompletedTask;
            }));

        createSection = createSection.WithView(formRow);
        stack = stack.WithView(createSection);

        // Result area (newly created token display) — full width so the token text has room.
        stack = stack.WithView((h, _) =>
            h.Stream.GetDataStream<string>(resultDataId)
                .Select(html => string.IsNullOrEmpty(html)
                    ? (UiControl?)Controls.Stack.WithWidth("100%")
                    : (UiControl?)Controls.Stack.WithWidth("100%").WithView(Controls.Html(html)))
                .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

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

            var capturedForDelete = token;
            // Delete button — available for revoked or expired tokens to clean up the list.
            if (token.IsRevoked || (token.ExpiresAt.HasValue && token.ExpiresAt.Value < DateTimeOffset.UtcNow))
            {
                row = row.WithView(Controls.Button("Delete")
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.UpdateData(resultDataId,
                            "<p style=\"padding: 8px 12px; color: var(--neutral-foreground-hint); " +
                            $"background: var(--neutral-layer-2); border-radius: 6px;\">Deleting '{Esc(capturedForDelete.Label)}'…</p>");

                        // Reactive: Subscribe to the service observable (hub.Post + RegisterCallback under the hood).
                        tokenService.DeleteToken(capturedForDelete.NodePath).Subscribe(
                            _ =>
                            {
                                ctx.Host.UpdateData(resultDataId,
                                    "<p style=\"padding: 8px 12px; color: #4ade80; background: var(--neutral-layer-2); " +
                                    $"border-radius: 6px;\">Token '{Esc(capturedForDelete.Label)}' deleted.</p>");
                                ctx.Host.UpdateData(tokenListRefreshId, DateTimeOffset.UtcNow.Ticks);
                            },
                            ex => ctx.Host.UpdateData(resultDataId,
                                "<p style=\"padding: 8px 12px; color: #f87171; background: var(--neutral-layer-2); " +
                                $"border-radius: 6px;\">Failed to delete: {Esc(ex.Message)}</p>"));
                        return Task.CompletedTask;
                    }));
            }

            if (!token.IsRevoked)
            {
                var captured = token;
                row = row.WithView(Controls.Button("Revoke")
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.UpdateData(resultDataId,
                            "<p style=\"padding: 8px 12px; color: var(--neutral-foreground-hint); " +
                            $"background: var(--neutral-layer-2); border-radius: 6px;\">Revoking '{Esc(captured.Label)}'…</p>");

                        // Reactive: Subscribe to the service observable — no await, no Task.Run.
                        tokenService.RevokeToken(captured.NodePath).Subscribe(
                            success =>
                            {
                                ctx.Host.UpdateData(resultDataId, success
                                    ? "<p style=\"padding: 8px 12px; color: #4ade80; background: var(--neutral-layer-2); " +
                                      $"border-radius: 6px;\">Token '{Esc(captured.Label)}' revoked.</p>"
                                    : "<p style=\"padding: 8px 12px; color: #f87171; background: var(--neutral-layer-2); " +
                                      "border-radius: 6px;\">Failed to revoke token.</p>");
                                ctx.Host.UpdateData(tokenListRefreshId, DateTimeOffset.UtcNow.Ticks);
                            },
                            ex => ctx.Host.UpdateData(resultDataId,
                                "<p style=\"padding: 8px 12px; color: #f87171; background: var(--neutral-layer-2); " +
                                $"border-radius: 6px;\">Failed to revoke: {Esc(ex.Message)}</p>"));
                        return Task.CompletedTask;
                    }));
            }

            container = container.WithView(row);
        }

        return container;
    }

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);
}
