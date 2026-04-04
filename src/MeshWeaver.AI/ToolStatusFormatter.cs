using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

/// <summary>
/// Formats FunctionCallContent into human-readable status messages
/// for display in the thread message bubble during agent execution.
/// </summary>
public static class ToolStatusFormatter
{
    private const int MaxArgLength = 60;

    /// <summary>
    /// Formats a function call into a readable status string.
    /// </summary>
    public static string Format(FunctionCallContent functionCall)
    {
        var name = functionCall.Name;
        var args = functionCall.Arguments;

        return name switch
        {
            "Get" => FormatArg("Fetching {0}", args, "path"),
            "Search" => FormatArg("Searching \"{0}\"", args, "query"),
            "Create" => "Creating node...",
            "Update" => "Updating...",
            "Delete" => "Deleting...",
            "NavigateTo" => FormatArg("Navigating to {0}", args, "path"),
            "SearchWeb" => FormatArg("Searching web for \"{0}\"", args, "query"),
            "FetchWebPage" => FormatArg("Fetching {0}", args, "url"),
            "delegate_to_agent" => FormatDelegation(args),
            "handoff_to_agent" => FormatArg("Handing off to {0}...", args, "agentName"),
            "store_plan" => "Storing plan...",
            "AddComment" => FormatArg("Adding comment on \"{0}\"...", args, "selectedText"),
            "SuggestEdit" => "Suggesting edit...",
            "UploadContent" => FormatArg("Uploading {0}...", args, "filePath"),
            _ when name.StartsWith("delegate_to_") => $"Delegating to {name["delegate_to_".Length..]}...",
            _ => $"Calling {name}..."
        };
    }

    private static string FormatDelegation(IDictionary<string, object?>? args)
    {
        var agent = GetArg(args, "agentName");
        // Strip "Agent/" prefix for cleaner display
        if (agent != null && agent.Contains('/'))
            agent = agent.Split('/').Last();
        return $"Delegating to {agent ?? "Agent"}...";
    }

    private static string FormatArg(string template, IDictionary<string, object?>? args, string key)
    {
        var value = GetArg(args, key);
        return value != null ? string.Format(template, Truncate(value)) : template.Replace("{0}", "...");
    }

    private static string? GetArg(IDictionary<string, object?>? args, string key)
    {
        if (args == null || !args.TryGetValue(key, out var val) || val == null)
            return null;

        // Handle JsonElement values from AI framework deserialization
        if (val is JsonElement jsonElement)
        {
            return jsonElement.ValueKind == JsonValueKind.String
                ? jsonElement.GetString()
                : jsonElement.ToString();
        }

        return val.ToString();
    }

    private static string Truncate(string value)
    {
        if (value.Length <= MaxArgLength)
            return value;
        return value[..(MaxArgLength - 3)] + "...";
    }
}
