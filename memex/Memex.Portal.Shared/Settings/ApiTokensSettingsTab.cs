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
/// Settings tab for managing personal API tokens.
/// Registered via AddApiTokensSettingsTab() on MessageHubConfiguration.
///
/// <para>Built from framework controls only. This tab previously emitted hand-built HTML strings
/// for every element — including an inline <c>onclick</c> that reached into <c>document</c> and
/// <c>navigator.clipboard</c> — and hard-coded its English copy, so a German viewer read it in
/// English. Both are now bugs by AGENTS.md ("Never hand-roll UI", "ALWAYS think about
/// internationalization"); the rendering is <c>Controls.*</c> and every user-visible string is a
/// catalog key.</para>
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
                RequiredPermission: Permission.None)
            { LabelKey = "settings.apiTokens", GroupKey = "settings.groupSecurity" });
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

        stack = stack
            .WithView(Controls.Title(host.Localize("settings.apiTokens"), 2))
            .WithView(Controls.Markdown(host.Localize("apiTokens.intro")));

        const string createDataId = "apiTokenCreate";
        const string resultDataId = "apiTokenResult";

        host.UpdateData(createDataId, new Dictionary<string, object?>
        {
            ["label"] = "",
            ["expiryDays"] = 365
        });
        // NOTE: Do NOT initialize resultDataId here — CreateToken saves a MeshNode
        // which triggers the workspace stream, causing the Settings page to rebuild.
        // If we set resultDataId="" here, the rebuild would overwrite the token display
        // that the click handler just set. Instead, the reactive view uses .StartWith().
        //
        // The token list below subscribes to `tokenService.GetTokensForUser(userId)`
        // — a live synced query (workspace.GetQuery under the hood). New tokens
        // appear on CreateNode commit, revokes flip rows to "Revoked" on
        // workspace.GetMeshNodeStream(...).Update commit, deletes drop rows on
        // DeleteNode commit. No refresh trigger needed.

        var formRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 12px; align-items: flex-end; flex-wrap: wrap;");

        formRow = formRow.WithView(new TextFieldControl(new JsonPointerReference("label"))
        {
            Label = host.Localize("apiTokens.field.label"),
            Placeholder = host.Localize("apiTokens.field.labelPlaceholder"),
            DataContext = LayoutAreaReference.GetDataPointer(createDataId)
        }.WithWidth("240px"));

        formRow = formRow.WithView(new NumberFieldControl(new JsonPointerReference("expiryDays"), "Int32")
        {
            Label = host.Localize("apiTokens.field.expiryDays"),
            DataContext = LayoutAreaReference.GetDataPointer(createDataId)
        }.WithWidth("140px"));

        formRow = formRow.WithView(Controls.Button(host.Localize("ui.generateToken"))
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateData(resultDataId, host.Localize("apiTokens.starting"));

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
                            ctx.Host.UpdateData(resultDataId, host.Localize("apiTokens.needLabel"));
                            return;
                        }

                        DateTimeOffset? expiresAt = expiryDays > 0
                            ? DateTimeOffset.UtcNow.AddDays(expiryDays)
                            : null;

                        ctx.Host.UpdateData(resultDataId,
                            $"{host.Localize("apiTokens.creating")} **{label}**…");

                        // Observable-based service call — subscribes to the underlying
                        // hub.Post + RegisterCallback pipeline without any await.
                        tokenService.CreateToken(userId, userName, userEmail, label, expiresAt)
                            .Subscribe(
                                result =>
                                {
                                    // The token is shown ONCE — only its hash is stored. A fenced
                                    // block renders it selectable/copyable without this tab
                                    // shipping its own clipboard JavaScript.
                                    ctx.Host.UpdateData(resultDataId,
                                        $"**{host.Localize("apiTokens.copyNow")}**\n\n"
                                        + $"```\n{result.RawToken}\n```");
                                    // No list refresh trigger — the synced query
                                    // below re-emits automatically when the new
                                    // node commits to the workspace.
                                },
                                ex => ctx.Host.UpdateData(resultDataId,
                                    $"{host.Localize("apiTokens.error")} {ex.Message}"));
                    });

                return Task.CompletedTask;
            }));

        var createSection = Controls.Stack.WithWidth("100%")
            .WithStyle("padding: 16px; background: var(--neutral-layer-2); border-radius: 8px; gap: 12px; margin-bottom: 24px;")
            .WithView(Controls.Title(host.Localize("apiTokens.createHeading"), 3))
            .WithView(formRow);
        stack = stack.WithView(createSection);

        // Result area (newly created token display) — full width so the token text has room.
        stack = stack.WithView((h, _) =>
            h.Stream.GetDataStream<string>(resultDataId)
                .Select(markdown => string.IsNullOrEmpty(markdown)
                    ? (UiControl?)Controls.Stack.WithWidth("100%")
                    : (UiControl?)Controls.Stack.WithWidth("100%").WithView(Controls.Markdown(markdown)))
                .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        stack = stack.WithView(Controls.Title(host.Localize("apiTokens.yourTokens"), 3));

        // Live token list — bound directly to the synced query. The view
        // re-renders whenever the underlying mesh-query collection changes
        // (token created, revoked, deleted). No refresh trigger pattern;
        // see Doc/Architecture/SyncedMeshNodeQueries.md for the canonical
        // shape — every emission is a complete snapshot.
        stack = stack.WithView((h, _) =>
            string.IsNullOrEmpty(userId)
                ? Observable.Return<UiControl?>(Controls.Markdown(host.Localize("apiTokens.noIdentity")))
                : tokenService.GetTokensForUser(userId)
                    .Select(tokens => tokens.Count == 0
                        ? (UiControl?)Controls.Markdown(host.Localize("apiTokens.none"))
                        : BuildTokenList(host, tokens, tokenService, resultDataId)));

        return stack;
    }

    private static UiControl BuildTokenList(
        LayoutAreaHost host,
        IReadOnlyList<ApiTokenInfo> tokens,
        ApiTokenService tokenService,
        string resultDataId)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");

        foreach (var token in tokens)
        {
            var expired = token.ExpiresAt.HasValue && token.ExpiresAt.Value < DateTimeOffset.UtcNow;
            var statusKey = token.IsRevoked ? "apiTokens.status.revoked"
                : expired ? "apiTokens.status.expired"
                : "apiTokens.status.active";
            var never = host.Localize("apiTokens.never");

            var row = Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("padding: 12px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; align-items: center; gap: 16px;");

            // Markdown, not an HTML string: the label and hash are user/system data and Markdown
            // renders them as text rather than as markup we had to hand-escape.
            row = row.WithView(Controls.Markdown(
                    $"**{token.Label}**  \n"
                    + $"`{token.HashPrefix}` · {host.Localize("apiTokens.created")} {token.CreatedAt:yyyy-MM-dd}"
                    + $" · {host.Localize("apiTokens.expires")} {(token.ExpiresAt?.ToString("yyyy-MM-dd") ?? never)}"
                    + $" · {host.Localize("apiTokens.lastUsed")} {(token.LastUsedAt?.ToString("yyyy-MM-dd HH:mm") ?? never)}")
                .WithStyle("flex: 1;"));

            row = row.WithView(Controls.Markdown($"**{host.Localize(statusKey)}**"));

            var capturedForDelete = token;
            // Delete button — available for revoked or expired tokens to clean up the list.
            if (token.IsRevoked || expired)
            {
                row = row.WithView(Controls.Button(host.Localize("common.delete"))
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.UpdateData(resultDataId,
                            $"{host.Localize("apiTokens.deleting")} **{capturedForDelete.Label}**…");

                        // Reactive: Subscribe to the service observable
                        // (hub.Post + RegisterCallback under the hood). The
                        // list re-renders automatically when the synced
                        // query above sees the deletion.
                        tokenService.DeleteToken(capturedForDelete.NodePath).Subscribe(
                            _ => ctx.Host.UpdateData(resultDataId,
                                $"{host.Localize("apiTokens.deleted")} **{capturedForDelete.Label}**"),
                            ex => ctx.Host.UpdateData(resultDataId,
                                $"{host.Localize("apiTokens.deleteFailed")} {ex.Message}"));
                        return Task.CompletedTask;
                    }));
            }

            if (!token.IsRevoked)
            {
                var captured = token;
                row = row.WithView(Controls.Button(host.Localize("ui.revoke"))
                    .WithAppearance(Appearance.Outline)
                    .WithClickAction(ctx =>
                    {
                        ctx.Host.UpdateData(resultDataId,
                            $"{host.Localize("apiTokens.revoking")} **{captured.Label}**…");

                        // Reactive: subscribe to the factored-out observable.
                        // Revoke(...) bridges the service call to a single outcome
                        // record so the test can assert on the same composition
                        // the UI subscribes to — no await, no Task.Run. The
                        // list row flips to "Revoked" automatically when the
                        // synced query sees the IsRevoked change.
                        Revoke(tokenService, captured.NodePath, captured.Label).Subscribe(
                            outcome => ctx.Host.UpdateData(resultDataId, OutcomeMarkdown(host, outcome)));
                        return Task.CompletedTask;
                    }));
            }

            container = container.WithView(row);
        }

        return container;
    }

    /// <summary>
    /// Outcome of a token revoke/delete invocation — surfaced to both the
    /// click handler and the test. <see cref="Success"/> is the user-facing
    /// pass/fail; <see cref="Message"/> carries the optional error detail to
    /// embed in the result text. Kept internal so it stays a presentation-
    /// layer concern, not an exported API.
    /// </summary>
    internal record TokenActionOutcome(bool Success, string Label, string? Message = null);

    /// <summary>
    /// Factored-out revoke pipeline — single observable composition shared by
    /// the click handler and the test. Bridges
    /// <see cref="ApiTokenService.RevokeToken"/> to an outcome that includes
    /// the label (so the message can be rendered without recapturing it) and
    /// folds the OnError path into a successful emission of
    /// <c>(Success=false, Message=...)</c> so the test never has to assert on
    /// observable termination semantics. Subscribe once — Take(1)-equivalent
    /// shape because the underlying service emits exactly one value.
    /// </summary>
    internal static IObservable<TokenActionOutcome> Revoke(
        ApiTokenService tokenService, string nodePath, string label)
        => tokenService.RevokeToken(nodePath)
            .Select(success => new TokenActionOutcome(success, label))
            .Catch<TokenActionOutcome, Exception>(ex =>
                Observable.Return(new TokenActionOutcome(false, label, ex.Message)));

    private static string OutcomeMarkdown(LayoutAreaHost host, TokenActionOutcome outcome)
    {
        if (outcome.Success)
            return $"{host.Localize("apiTokens.revoked")} **{outcome.Label}**";
        return string.IsNullOrEmpty(outcome.Message)
            ? host.Localize("apiTokens.revokeFailed")
            : $"{host.Localize("apiTokens.revokeFailed")} {outcome.Message}";
    }
}
