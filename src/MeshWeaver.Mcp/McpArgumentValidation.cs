using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MeshWeaver.Mcp;

/// <summary>
/// Names the argument a <c>tools/call</c> got wrong — instead of letting the caller see the SDK's
/// opaque <c>"An error occurred invoking '&lt;tool&gt;'."</c>.
///
/// <para><b>The bug this closes (#639).</b> Tool schemas change between images —
/// <c>create nodes</c> became <c>create node</c>, <c>delete path</c> became <c>delete paths</c>.
/// A caller still speaking the older dialect gets two failure modes today, both of them useless:</para>
/// <list type="number">
///   <item><b>The unknown argument is SILENTLY DROPPED.</b> <c>McpJsonUtilities.DefaultOptions</c>
///     leaves <c>UnmappedMemberHandling</c> at its default (<c>Skip</c>), so
///     <c>AIFunctionFactory</c>'s unexpected-key check never fires. When the renamed parameter has a
///     default (e.g. <c>edit_content replaceAll</c>), the call RUNS with the default — a half-executed
///     step that reports success. That is the "half-executed sequences" of the 2026-07-24
///     plugin-install retrospective.</item>
///   <item><b>Otherwise the binder throws and the message is swallowed.</b>
///     <c>AIFunctionFactory</c> throws <c>ArgumentException("The arguments dictionary is missing a
///     value for the required parameter 'node'.")</c>, and <c>McpServerImpl</c>'s <c>tools/call</c>
///     wrapper turns any non-<c>McpException</c> into the fixed text
///     <c>"An error occurred invoking 'create'."</c> — the argument name never reaches the caller.</item>
/// </list>
///
/// <para><b>The seam.</b> <see cref="CallToolFilter"/> is a single <c>CallToolFilter</c> registered
/// once in <see cref="McpExtensions.AddMeshMcp"/>, so EVERY tool — present and future — is covered
/// with no per-tool duplication. It runs inside the SDK's outermost <c>tools/call</c> wrapper (which
/// has already resolved <see cref="RequestContext{TParams}.MatchedPrimitive"/>) but BEFORE the
/// argument binder, and short-circuits with a normal <c>IsError</c> result carrying the real message.
/// Validation is driven entirely by the tool's own published <see cref="Tool.InputSchema"/>, so it can
/// never drift from what <c>tools/list</c> advertises.</para>
///
/// <para><b>One deliberate rejection, otherwise conservative.</b> Unknown argument NAMES are rejected
/// ON PURPOSE, and that is the point of this class: the binder currently ACCEPTS them —
/// <c>McpJsonUtilities.DefaultOptions</c> leaves <c>UnmappedMemberHandling</c> at <c>Skip</c> — so a
/// caller using an older tool dialect has its argument silently dropped and, when the renamed
/// parameter carries a default, gets a SUCCESS for a call that ignored what it asked for (#639's
/// half-executed sequences). 🚨 Do NOT "restore conservatism" by loosening this check; a silent drop
/// is the defect.</para>
///
/// <para>Everywhere ELSE the check stays strictly weaker than the binder — it must never reject a
/// VALUE the binder would have accepted, so: JSON <c>null</c> always passes through (the tool's own
/// required-field check gives a better answer), numeric strings are accepted for
/// <c>integer</c>/<c>number</c> parameters (<c>NumberHandling = AllowReadingFromString</c>), and a
/// parameter whose schema declares no <c>type</c> (<c>anyOf</c>/<c>$ref</c>) is not judged at all.</para>
///
/// <para>🚨 These strings are MODEL-FACING, not user-facing: they are read by the calling agent, never
/// rendered in the portal. Per AGENTS.md they stay English and are deliberately NOT localized — the
/// same rule that keeps LLM tool-parameter <c>[Description]</c>s untranslated.</para>
/// </summary>
public static class McpArgumentValidation
{
    /// <summary>
    /// The <c>tools/call</c> filter: validates the raw arguments against the matched tool's published
    /// input schema and answers with a naming error instead of delegating to the binder.
    /// </summary>
    /// <param name="next">The next handler in the <c>tools/call</c> pipeline.</param>
    /// <returns><paramref name="next"/> wrapped with argument validation.</returns>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> CallToolFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (request, cancellationToken) =>
            request.MatchedPrimitive is McpServerTool tool
            && Validate(tool.ProtocolTool, request.Params?.Arguments) is { } error
                ? new ValueTask<CallToolResult>(new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = error }]
                })
                : next(request, cancellationToken);

    /// <summary>
    /// Checks a call's arguments against a tool's published input schema.
    /// </summary>
    /// <param name="tool">The tool being called (its <see cref="Tool.InputSchema"/> IS the contract).</param>
    /// <param name="arguments">The raw arguments as received on the wire; may be null or empty.</param>
    /// <returns>
    /// <c>null</c> when the call binds — otherwise an <c>Error: …</c> message naming the offending
    /// argument, in the same shape the tools already use for their own failures.
    /// </returns>
    public static string? Validate(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        var schema = tool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            return null;   // no declared shape — nothing to check against

        // Declaration order, which is also the order the parameters appear in the tool's signature.
        var expected = properties.EnumerateObject().Select(p => p.Name).ToArray();
        var supplied = arguments as IEnumerable<KeyValuePair<string, JsonElement>> ?? [];

        // 1. Unknown argument — the rename tell, and the one the binder drops on the floor.
        foreach (var argument in supplied)
            if (!properties.TryGetProperty(argument.Key, out _))
                return $"Error: unknown argument '{argument.Key}' for tool '{tool.Name}' — expected one of: "
                     + $"{string.Join(", ", expected)}."
                     + (ClosestMatch(argument.Key, expected) is { } suggestion ? $" Did you mean '{suggestion}'?" : string.Empty)
                     + " (Tool schemas change between releases — call tools/list for the current shape.)";

        // 2. Missing required argument.
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            var requiredNames = required.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.String)
                .Select(r => r.GetString()!)
                .ToArray();
            foreach (var name in requiredNames)
                if (arguments is null || !arguments.ContainsKey(name))
                    return $"Error: missing required argument '{name}' for tool '{tool.Name}' — "
                         + $"required: {string.Join(", ", requiredNames)}.";
        }

        // 3. Wrong type.
        foreach (var argument in supplied)
            if (properties.TryGetProperty(argument.Key, out var parameterSchema)
                && Mismatch(parameterSchema, argument.Value) is { } declaredType)
                return $"Error: argument '{argument.Key}' for tool '{tool.Name}' expects {declaredType}, "
                     + $"got {Describe(argument.Value)}.";

        return null;
    }

    /// <summary>
    /// Returns the declared type name when <paramref name="value"/> cannot bind to
    /// <paramref name="parameterSchema"/>, or <c>null</c> when it can (or when the schema declares no
    /// judgeable type).
    /// </summary>
    private static string? Mismatch(JsonElement parameterSchema, JsonElement value)
    {
        if (parameterSchema.ValueKind != JsonValueKind.Object
            || !parameterSchema.TryGetProperty("type", out var declaredType))
            return null;   // anyOf / $ref / untyped — not ours to judge

        var declared = declaredType.ValueKind switch
        {
            JsonValueKind.String => [declaredType.GetString()!],
            JsonValueKind.Array => declaredType.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString()!)
                .ToArray(),
            _ => Array.Empty<string>()
        };

        // JSON null always passes through: the binder maps it to null and the tool's own
        // "'path' is required" answer is more useful than a type complaint.
        if (declared.Length == 0 || value.ValueKind == JsonValueKind.Null || declared.Any(t => Accepts(t, value)))
            return null;

        var judgeable = declared.Where(t => t != "null").ToArray();
        return judgeable.Length == 0 ? null : string.Join(" or ", judgeable);
    }

    private static bool Accepts(string declaredType, JsonElement value) => declaredType switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        // NumberHandling = AllowReadingFromString, so a numeric STRING binds — but only when it
        // really parses as one; "abc" for an int would still throw inside the binder.
        "integer" => value.ValueKind == JsonValueKind.Number
                     || (value.ValueKind == JsonValueKind.String
                         && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)),
        "number" => value.ValueKind == JsonValueKind.Number
                    || (value.ValueKind == JsonValueKind.String
                        && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true   // unknown type keyword — don't guess
    };

    /// <summary>Renders a received value as "&lt;kind&gt; (&lt;raw json, truncated&gt;)".</summary>
    private static string Describe(JsonElement value)
    {
        var kind = value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            JsonValueKind.Null => "null",
            _ => "nothing"
        };
        var raw = value.GetRawText().ReplaceLineEndings(" ");
        return raw.Length <= 60 ? $"{kind} ({raw})" : $"{kind} ({raw[..59]}…)";
    }

    /// <summary>
    /// The likeliest intended parameter for an unknown argument name — a rename is a small edit
    /// (<c>nodes</c>→<c>node</c>, <c>path</c>→<c>paths</c>, <c>replace_all</c>→<c>replaceAll</c>).
    /// Returns null when nothing is close enough to name with confidence.
    /// </summary>
    private static string? ClosestMatch(string supplied, IReadOnlyList<string> expected)
    {
        var normalized = Normalize(supplied);
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in expected)
        {
            var distance = Distance(normalized, Normalize(candidate));
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = candidate;
        }
        return best is not null && bestDistance <= Math.Max(2, Normalize(best).Length / 3) ? best : null;
    }

    /// <summary>Case- and underscore-insensitive, so <c>replace_all</c> matches <c>replaceAll</c>.</summary>
    private static string Normalize(string name) => name.Replace("_", string.Empty).ToLowerInvariant();

    /// <summary>Levenshtein edit distance over two normalized names (two-row DP).</summary>
    private static int Distance(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }
}
