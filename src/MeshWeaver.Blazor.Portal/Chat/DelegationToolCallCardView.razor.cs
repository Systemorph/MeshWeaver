using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Code-behind for <see cref="DelegationToolCallCardView"/>. Opens TWO
/// <see cref="IMeshNodeStreamCache.GetStream"/> subscriptions:
/// <list type="number">
///   <item><c>MessagePath</c> → the parent's response cell. Re-renders the
///     card's status badge + body whenever the projection in
///     <c>ChatClientAgentFactory.ExecuteDelegationAsync</c> writes a new
///     last-10-lines preview / status transition.</item>
///   <item><c>DelegationPath</c> → the sub-thread itself. Re-renders the
///     card title when the sub-thread is renamed. Cheap: same path resolves
///     to the same upstream cache handle that the sub-thread chat view is
///     also reading from.</item>
/// </list>
///
/// <para>Pattern reference: <c>Doc/GUI/ItemTemplateMeshNodeStreamBinding</c>.</para>
/// </summary>
public partial class DelegationToolCallCardView : BlazorView<DelegationToolCallCardControl, DelegationToolCallCardView>
{
    private string? messagePath;
    private string? delegationPath;

    // Live fields populated from the two cache subscriptions.
    private string? title;             // fallback shown when neither subThreadName nor agentName is known yet
    private string? subThreadName;
    private string? agentName;
    private string? body;
    private ToolCallStatus status = ToolCallStatus.Streaming;
    private string badgeIcon = "●";
    private string badgeText = "Running";
    private string badgeCssClass = "delegation-card-badge-running";

    protected override void BindData()
    {
        base.BindData();

        DataBind(ViewModel.MessagePath, x => x.messagePath, (val, _) => val as string ?? val?.ToString());
        DataBind(ViewModel.DelegationPath, x => x.delegationPath, (val, _) => val as string ?? val?.ToString());

        IMeshNodeStreamCache? cache;
        try
        {
            cache = Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        }
        catch (Exception ex)
        {
            var logger = Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger<DelegationToolCallCardView>();
            logger?.LogWarning(ex, "DelegationToolCallCardView: IMeshNodeStreamCache unavailable; card will not update live");
            return;
        }

        // Subscription 1: parent response cell — read live status + 10-line preview.
        if (!string.IsNullOrEmpty(messagePath) && !string.IsNullOrEmpty(delegationPath))
        {
            AddBinding(cache.GetStream(messagePath)
                .Where(n => n?.Content is not null)
                .Select(n => ToJsonElement(n.Content!))
                .Subscribe(je =>
                {
                    if (!je.TryGetProperty("toolCalls", out var toolCallsProp)
                        || toolCallsProp.ValueKind != JsonValueKind.Array)
                        return;

                    // Find the matching ToolCallEntry by DelegationPath.
                    JsonElement? match = null;
                    foreach (var entry in toolCallsProp.EnumerateArray())
                    {
                        if (entry.TryGetProperty("delegationPath", out var dp)
                            && dp.ValueKind == JsonValueKind.String
                            && string.Equals(dp.GetString(), delegationPath, StringComparison.Ordinal))
                        {
                            match = entry;
                            break;
                        }
                    }
                    if (match is null) return;

                    var changed = false;

                    var newStatus = match.Value.TryGetProperty("status", out var statusProp)
                        ? statusProp.ValueKind == JsonValueKind.String
                            && Enum.TryParse<ToolCallStatus>(statusProp.GetString(), out var parsed)
                                ? parsed
                                : statusProp.ValueKind == JsonValueKind.Number
                                    && Enum.IsDefined(typeof(ToolCallStatus), statusProp.GetInt32())
                                        ? (ToolCallStatus)statusProp.GetInt32()
                                        : status
                        : status;
                    // Back-compat: persisted entries with Status=Success but no Result are still streaming.
                    var resultText = match.Value.TryGetProperty("result", out var rp)
                        && rp.ValueKind == JsonValueKind.String
                            ? rp.GetString()
                            : null;
                    if (newStatus == ToolCallStatus.Success && string.IsNullOrEmpty(resultText))
                        newStatus = ToolCallStatus.Streaming;

                    if (newStatus != status)
                    {
                        status = newStatus;
                        (badgeIcon, badgeText, badgeCssClass) = MapBadge(status);
                        changed = true;
                    }

                    // Body is NOT read here — it streams from the sub-thread's
                    // response cell via subscription 2's nested sub. Parent's
                    // ToolCallEntry.Result is only populated at terminal (= the
                    // FCC FunctionResultContent payload) and matches the
                    // sub-thread cell's final text, so no need to mirror it.

                    // Title fallback (used when sub-thread Name isn't loaded yet).
                    var displayName = match.Value.TryGetProperty("displayName", out var dn)
                        && dn.ValueKind == JsonValueKind.String
                            ? dn.GetString()
                            : null;
                    if (displayName != title) { title = displayName; changed = true; }

                    // Parse agent name out of the Arguments JSON blob (best-effort).
                    var args = match.Value.TryGetProperty("arguments", out var ap)
                        && ap.ValueKind == JsonValueKind.String
                            ? ap.GetString()
                            : null;
                    var newAgent = ExtractAgentName(args);
                    if (newAgent != agentName) { agentName = newAgent; changed = true; }

                    if (changed) InvokeAsync(StateHasChanged);
                }));
        }

        // Subscription 2: sub-thread node — reads Name (for the card title)
        // AND ActiveMessageId (to derive the path of the currently-streaming
        // response cell, which is what we subscribe to in step 3 for live body).
        string? lastSubResponsePath = null;
        if (!string.IsNullOrEmpty(delegationPath))
        {
            AddBinding(cache.GetStream(delegationPath)
                .Where(n => n is not null)
                .Subscribe(node =>
                {
                    var changed = false;
                    var name = node.Name;
                    if (!string.IsNullOrEmpty(name) && name != subThreadName)
                    {
                        subThreadName = name;
                        changed = true;
                    }

                    // Sub-thread carries its current response cell via ActiveMessageId.
                    // Path = {DelegationPath}/{ActiveMessageId}. As soon as it appears,
                    // open the live body subscription so the card body streams from
                    // the sub-thread directly (no parent-projection mirroring).
                    var activeMsgId = (node.Content is MeshWeaver.AI.Thread t) ? t.ActiveMessageId : null;
                    if (!string.IsNullOrEmpty(activeMsgId))
                    {
                        var responsePath = $"{delegationPath}/{activeMsgId}";
                        if (responsePath != lastSubResponsePath)
                        {
                            lastSubResponsePath = responsePath;
                            AddBinding(cache.GetStream(responsePath)
                                .Where(rc => rc?.Content is not null)
                                .Subscribe(rc =>
                                {
                                    var msg = rc.Content as MeshWeaver.AI.ThreadMessage;
                                    var newBody = MeshWeaver.AI.ToolStatusFormatter.LastNLines(msg?.Text, 10);
                                    if (newBody != body)
                                    {
                                        body = newBody;
                                        InvokeAsync(StateHasChanged);
                                    }
                                }));
                        }
                    }

                    if (changed) InvokeAsync(StateHasChanged);
                }));
        }
    }

    private JsonElement ToJsonElement(object content)
        => content is JsonElement je
            ? je
            : JsonSerializer.SerializeToElement(content, Hub.JsonSerializerOptions);

    private static (string Icon, string Text, string Css) MapBadge(ToolCallStatus s) => s switch
    {
        ToolCallStatus.Streaming => ("●", "Running",   "delegation-card-badge-running"),
        ToolCallStatus.Success   => ("✓", "Done",      "delegation-card-badge-done"),
        ToolCallStatus.Failed    => ("✕", "Failed",    "delegation-card-badge-failed"),
        ToolCallStatus.Cancelled => ("/", "Cancelled", "delegation-card-badge-cancelled"),
        _                        => ("●", "",          "delegation-card-badge-running")
    };

    /// <summary>
    /// Mirrors <c>ToolStatusFormatter.LastNLines</c> client-side so we don't
    /// take a transitive reference on MeshWeaver.AI from the Blazor.Portal
    /// project just for one helper. Single-purpose, no behaviour drift.
    /// </summary>
    private static string LastNLines(string text, int n)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (n <= 0) return string.Empty;
        var lines = text.Split('\n');
        if (lines.Length <= n) return text;
        return string.Join('\n', lines[(lines.Length - n)..]);
    }

    /// <summary>
    /// Pulls "agentName" out of the truncated JSON-serialised Arguments string
    /// on a delegation tool call. Best-effort: if the truncation cut the JSON
    /// short or the format changes, we just return null and the card falls
    /// back to subThreadName / displayName for the title.
    /// </summary>
    private static string? ExtractAgentName(string? argsJson)
    {
        if (string.IsNullOrEmpty(argsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("agentName", out var anProp)
                && anProp.ValueKind == JsonValueKind.String)
            {
                var name = anProp.GetString();
                if (string.IsNullOrEmpty(name)) return null;
                var slash = name.LastIndexOf('/');
                return slash >= 0 ? name[(slash + 1)..] : name;
            }
        }
        catch (JsonException)
        {
            // truncation may have cut JSON short — silently fall back
        }
        return null;
    }
}
