using System.Text.Json.Nodes;

namespace MeshWeaver.Messaging;

/// <summary>
/// Recognises a <see cref="DisposeRequest"/> on a delivery BEFORE its target hub has read it. A
/// delivery that crossed a hub boundary is still packaged as <see cref="RawJson"/> at the routing
/// layer and at an Orleans grain (it is deserialised only inside the target's <c>MessageService</c>),
/// so <c>delivery.Message is DisposeRequest</c> is true only for an in-process post — measured on
/// the Orleans host, where the typed test never matched and a cold grain built its hub for a
/// dispose after all. The two places that must answer "is this a dispose?" without a hub —
/// <c>MonolithRoutingService.RouteImpl</c> and <c>MessageHubGrain.DeliverMessage</c> — read the
/// envelope's <c>$type</c> instead, and the two fields they report from it.
/// </summary>
public static class DisposeRequestEnvelope
{
    /// <summary>
    /// True when <paramref name="delivery"/> carries a <see cref="DisposeRequest"/>, typed or as a
    /// <see cref="RawJson"/> whose <c>$type</c> names it; <paramref name="request"/> then holds the
    /// typed instance, or one rebuilt from the envelope's <c>reason</c> and <c>cascadedFrom</c>.
    /// </summary>
    public static bool TryRead(IMessageDelivery delivery, out DisposeRequest? request)
    {
        switch (delivery.Message)
        {
            case DisposeRequest typed:
                request = typed;
                return true;
            case RawJson raw when NamesDisposeRequest(raw.Content, out var node):
                request = new DisposeRequest
                {
                    Reason = Text(node, "reason"),
                    CascadedFrom = Text(node, "cascadedFrom"),
                };
                return true;
            default:
                request = null;
                return false;
        }
    }

    private static bool NamesDisposeRequest(string content, out JsonObject node)
    {
        node = null!;
        if (string.IsNullOrEmpty(content) || !content.Contains(nameof(DisposeRequest), StringComparison.Ordinal))
            return false;
        try
        {
            if (JsonNode.Parse(content) is not JsonObject jo
                || !jo.TryGetPropertyValue("$type", out var type)
                || type?.ToString() is not { } typeName)
                return false;
            var name = typeName;
            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name[(dot + 1)..];
            if (!string.Equals(name, nameof(DisposeRequest), StringComparison.Ordinal))
                return false;
            node = jo;
            return true;
        }
        catch (Exception)
        {
            // Not JSON, or not an object — then it is not a packaged DisposeRequest either.
            return false;
        }
    }

    private static string? Text(JsonObject node, string camelCase)
    {
        if (node.TryGetPropertyValue(camelCase, out var value) && value is not null)
            return value.ToString();
        var pascal = char.ToUpperInvariant(camelCase[0]) + camelCase[1..];
        return node.TryGetPropertyValue(pascal, out var alt) && alt is not null ? alt.ToString() : null;
    }
}
