using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Markdown;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Code-behind for <see cref="ThreadMessageItemView"/>: opens ONE
/// <see cref="IMeshNodeStreamCache.GetStream"/> subscription on the bound
/// <see cref="ThreadMessageItemControl.MessagePath"/> and re-renders on each
/// emission. The Razor markup branches on <see cref="Role"/> to pick the input
/// or output body.
///
/// <para>This is the canonical "list of MeshNodes" rendering shape — see
/// <c>Doc/GUI/ItemTemplateMeshNodeStreamBinding</c>. ONE cache subscription
/// per visible message, NO per-message layout-area round-trip, instant
/// pending-text rendering before the satellite cell materialises.</para>
/// </summary>
public partial class ThreadMessageItemView : BlazorView<ThreadMessageItemControl, ThreadMessageItemView>
{
    // Resolved from the ItemTemplate's per-item DataContext via JsonPointerReference
    // (when the backend ships the control inside an ItemTemplateControl).
    private string? messagePath;
    private string? pendingText;
    private bool isPending;

    // Live fields populated from the cache emission.
    private string Role = "user";
    private string AuthorName = "";
    private string? modelName;
    private string? messageText;
    private IReadOnlyList<ToolCallEntry>? toolCalls;

    private bool IsUser => Role.Equals("user", StringComparison.OrdinalIgnoreCase);

    private MarkdownControl MarkdownVm => new MarkdownControl(messageText ?? "")
        .WithStyle("background: transparent;");

    /// <summary>
    /// Builds the per-card <see cref="DelegationToolCallCardControl"/> for one
    /// delegation tool call. The card view subscribes independently to the same
    /// cache (one shared upstream handle per path), so there's no extra
    /// round-trip cost per card beyond the two downstream subscriptions inside
    /// <see cref="DelegationToolCallCardView"/>.
    /// </summary>
    private DelegationToolCallCardControl CardFor(ToolCallEntry call)
        => new DelegationToolCallCardControl
        {
            MessagePath = messagePath,
            DelegationPath = call.DelegationPath
        };

    protected override void BindData()
    {
        base.BindData();

        // Resolve MessagePath + PendingText from the ItemTemplate's per-item
        // data context. DataBind handles the JsonPointerReference resolution.
        DataBind(ViewModel.MessagePath, x => x.messagePath, (val, prev) =>
            val as string ?? val?.ToString());
        DataBind(ViewModel.PendingText, x => x.pendingText, (val, prev) =>
            val as string ?? val?.ToString());

        if (string.IsNullOrEmpty(messagePath))
        {
            // Pending-only entry — no satellite cell yet. Render the typed text
            // and skip the subscription. Once the cell exists, the layout area
            // re-emits with a MessagePath and we reach the branch below.
            isPending = !string.IsNullOrEmpty(pendingText);
            return;
        }

        // 🚨 Cross-hub MeshNode reads go through IMeshNodeStreamCache — process-wide
        // shared handle per path. Multiple visible cards on the same path share ONE
        // upstream subscription. See Doc/GUI/ItemTemplateMeshNodeStreamBinding.
        IMeshNodeStreamCache? cache;
        try
        {
            cache = Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        }
        catch (Exception ex)
        {
            var logger = Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger<ThreadMessageItemView>();
            logger?.LogWarning(ex, "ThreadMessageItemView: IMeshNodeStreamCache unavailable; falling back to pending text only");
            isPending = !string.IsNullOrEmpty(pendingText);
            return;
        }

        // Pending-render gate: until the cell exists, render the pending text.
        // First emission with non-null Content swaps to live ThreadMessage.Text
        // by flipping isPending = false on the same Blazor component (no flicker).
        isPending = !string.IsNullOrEmpty(pendingText);

        // 🚨 2026-05-21 — BindData runs inside a sync-stream emission scope,
        // which leaves accessService.Context stamped with the sync hub's own
        // address (e.g. "sync/0ANs..."). The cache's RLS gate then asks
        // "does sync/... have Read on this thread message?" and the answer is
        // no — the sync hub isn't a user. Result: UnauthorizedAccessException
        // killed the Blazor circuit on every chat render
        // ("User 'sync/0ANs...' lacks Read permission on
        // Systemorph/_Thread/.../<msg-id>").
        //
        // Access to the parent Thread was already enforced at navigation time
        // (PathResolutionService + NavigationService.LoadNodeWithPreRenderedHtml
        // ran with the user's identity). Reading the message cells from inside
        // the already-authorized thread renderer is safe to do under System
        // — the thread IS the access boundary, individual messages don't have
        // independent gates.
        //
        // The scope is open only for the synchronous GetStream call (which
        // captures captured-context eagerly at chaining time — see
        // MeshNodeStreamCache.cs:155). Subscribers downstream re-stamp the
        // captured value via CarryAccessContext; no AsyncLocal leak to other
        // components.
        var accessService = Hub.ServiceProvider.GetService<AccessService>();
        IObservable<MeshNode> stream;
        using (accessService?.ImpersonateAsSystem())
        {
            stream = cache.GetStream(messagePath);
        }

        AddBinding(stream
            .Where(n => n?.Content is not null)
            .Select(n => ToJsonElement(n.Content!))
            .Subscribe(je =>
            {
                var changed = false;

                // The first emission with content means the satellite cell exists —
                // drop the pending fallback so subsequent renders use the live data.
                if (isPending) { isPending = false; changed = true; }

                var role = je.TryGetProperty("role", out var roleProp) && roleProp.ValueKind == JsonValueKind.String
                    ? roleProp.GetString() ?? "user"
                    : "user";
                if (role != Role) { Role = role; changed = true; }

                var explicitAuthor = je.TryGetProperty("authorName", out var authorProp) && authorProp.ValueKind == JsonValueKind.String
                    ? authorProp.GetString()
                    : null;
                var agentName = je.TryGetProperty("agentName", out var agentProp) && agentProp.ValueKind == JsonValueKind.String
                    ? agentProp.GetString()
                    : null;
                var author = explicitAuthor
                    ?? (role.Equals("user", StringComparison.OrdinalIgnoreCase)
                        ? "You"
                        : agentName ?? "Assistant");
                if (author != AuthorName) { AuthorName = author; changed = true; }

                var model = je.TryGetProperty("modelName", out var modelProp) && modelProp.ValueKind == JsonValueKind.String
                    ? modelProp.GetString()
                    : null;
                if (model != modelName) { modelName = model; changed = true; }

                var text = je.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String
                    ? textProp.GetString()
                    : null;
                if (text != messageText) { messageText = text; changed = true; }

                IReadOnlyList<ToolCallEntry>? newToolCalls = je.TryGetProperty("toolCalls", out var tcProp)
                    && tcProp.ValueKind == JsonValueKind.Array
                        ? tcProp.Deserialize<List<ToolCallEntry>>(Hub.JsonSerializerOptions)
                        : null;
                if (!ToolCallsEqual(newToolCalls, toolCalls))
                {
                    toolCalls = newToolCalls;
                    changed = true;
                }

                if (changed) InvokeAsync(StateHasChanged);
            }));
    }

    private JsonElement ToJsonElement(object content)
        => content is JsonElement je
            ? je
            : JsonSerializer.SerializeToElement(content, Hub.JsonSerializerOptions);

    private static bool ToolCallsEqual(IReadOnlyList<ToolCallEntry>? a, IReadOnlyList<ToolCallEntry>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!Equals(a[i], b[i])) return false;
        return true;
    }
}
