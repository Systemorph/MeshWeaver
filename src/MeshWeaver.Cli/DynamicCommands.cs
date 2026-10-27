using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.Cli;

/// <summary>
/// Generates one CLI subcommand per MCP tool from its JSON input schema, so a tool the server adds
/// or changes reaches <c>memex</c> without a new CLI release.
///
/// <para><b>The mapping rule.</b> The command is the tool name in kebab-case (<c>get_diagnostics</c>
/// → <c>get-diagnostics</c>). Every schema property becomes an option <c>--kebab-name</c>:
/// <c>string</c> takes a value, an <c>enum</c> only one of its values, <c>integer</c>/<c>number</c> a
/// number, <c>boolean</c> is a flag (<c>--force</c>, or <c>--force false</c>), an <c>array</c> is
/// repeatable (<c>--tag a --tag b</c>, or one JSON array), and an <c>object</c> or any schema without a
/// simple type takes a JSON value. Every REQUIRED <c>string</c> property is ALSO a positional argument,
/// in schema order — so <c>memex get Doc/X</c>, <c>memex search 'nodeType:Agent'</c> and
/// <c>memex move A B</c> read as they always did. The whole argument object can instead be given as
/// <c>--json '{…}'</c> or <c>--file args.json</c>; options and positionals given beside it win. A
/// property whose kebab name collides with a global option is offered as <c>--arg-name</c>.</para>
///
/// <para><b>Compatibility shapes</b> (<see cref="LegacyShapes"/>) keep the verbs the CLI hand-coded
/// before the catalog existed reading the same way; each applies only when the tool still HAS the
/// named parameter, so a server rename degrades it to the generic rule rather than to a wrong call.</para>
/// </summary>
public static class DynamicCommands
{
    /// <summary>Option names a generated option must not take: they belong to the root or to the
    /// argument-object forms.</summary>
    public static readonly ImmutableHashSet<string> ReservedOptionNames =
        ["base-url", "token", "refresh", "json", "file", "args-file", "help", "version", "h"];

    /// <summary>
    /// The compatibility shapes for verbs the CLI hand-coded before it read the catalog.
    /// </summary>
    public static class LegacyShapes
    {
        /// <summary>Old command name → tool. Applied only when no tool already owns the name.</summary>
        public static readonly ImmutableDictionary<string, string> CommandAliases =
            ImmutableDictionary<string, string>.Empty.Add("diagnostics", "get_diagnostics");

        /// <summary>Tool → the parameter <c>-f/--file</c> fills with the file's TEXT (the old
        /// <c>create -f node.json</c>, <c>update -f nodes.json</c>, <c>patch -f fields.json</c>). On
        /// these tools the whole-argument file is <c>--args-file</c>.</summary>
        public static readonly ImmutableDictionary<string, string> FileFillsParameter =
            ImmutableDictionary<string, string>.Empty
                .Add("create", "node")
                .Add("update", "nodes")
                .Add("patch", "fields");

        /// <summary>(tool, parameter) → an extra option alias the old verb used.</summary>
        public static readonly ImmutableDictionary<(string Tool, string Parameter), string> OptionAliases =
            ImmutableDictionary<(string, string), string>.Empty
                .Add(("execute_script", "timeoutSeconds"), "--timeout")
                .Add(("mirror", "targetPath"), "--target");

        /// <summary>Tool → a string parameter that holds a JSON ARRAY, filled from a variadic
        /// positional (the old <c>delete a b c</c>).</summary>
        public static readonly ImmutableDictionary<string, string> VariadicJsonArray =
            ImmutableDictionary<string, string>.Empty.Add("delete", "paths");
    }

    /// <summary><c>get_diagnostics</c> → <c>get-diagnostics</c>, <c>basePath</c> → <c>base-path</c>.</summary>
    public static string Kebab(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '_' or ' ' or '.')
            {
                if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
                continue;
            }
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])) && sb.Length > 0 && sb[^1] != '-')
                    sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>A parameter's binding: reads its value off a parse, or reports it absent.</summary>
    private sealed record Binding(string Name, bool Required, string Display, Func<ParseResult, (bool Given, JsonNode? Value, ImmutableList<string> Errors)> Read);

    /// <summary>
    /// Builds the command for <paramref name="tool"/>. <paramref name="invoke"/> runs the call with
    /// the assembled argument object and answers the exit code.
    /// </summary>
    public static Command Build(
        McpTool tool,
        string commandName,
        TextWriter err,
        Func<string, JsonObject, CancellationToken, Task<int>> invoke)
    {
        var cmd = new Command(commandName, Summary(tool.Description) ?? $"MCP tool '{tool.Name}'.");
        var properties = tool.InputSchema["properties"] as JsonObject ?? new JsonObject();
        var required = (tool.InputSchema["required"] as JsonArray ?? [])
            .Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .OfType<string>()
            .ToImmutableHashSet();

        // A compatibility shape sends a STRING (the file's text, a JSON-array string), so it applies
        // only while the parameter IS a plain string; any other schema takes the generic mapping.
        bool IsString(string? parameter) =>
            parameter is not null && properties[parameter] is JsonObject p && KindOf(p) == SchemaKind.String;
        LegacyShapes.FileFillsParameter.TryGetValue(tool.Name, out var fileFills);
        if (!IsString(fileFills)) fileFills = null;
        LegacyShapes.VariadicJsonArray.TryGetValue(tool.Name, out var variadic);
        if (!IsString(variadic)) variadic = null;

        var jsonOpt = new Option<string?>("--json") { Description = "All arguments as one JSON object; options given beside it win." };
        var argsFileOpt = fileFills is null
            ? new Option<string?>("--file", "--args-file") { Description = "Path to a JSON file holding all arguments as one object; options given beside it win." }
            : new Option<string?>("--args-file") { Description = "Path to a JSON file holding all arguments as one object; options given beside it win." };
        cmd.Options.Add(jsonOpt);
        cmd.Options.Add(argsFileOpt);

        var bindings = ImmutableList.CreateBuilder<Binding>();
        var usedOptionNames = new HashSet<string>(StringComparer.Ordinal) { "--json", "--file", "--args-file" };

        foreach (var (name, node) in properties)
        {
            var schema = node as JsonObject ?? new JsonObject();
            var isRequired = required.Contains(name);
            var kind = KindOf(schema);
            var kebab = Kebab(name);
            var optionName = AllocateOptionName(kebab, usedOptionNames);
            var description = Describe(schema, isRequired);

            Argument? positional = null;
            if (name == variadic)
            {
                var arg = new Argument<string[]>(kebab) { Description = description + " One or more values, sent as a JSON array.", Arity = ArgumentArity.ZeroOrMore };
                cmd.Arguments.Add(arg);
                positional = arg;
            }
            else if (isRequired && kind == SchemaKind.String)
            {
                var arg = new Argument<string?>(kebab) { Description = description + $" (or {optionName})", Arity = ArgumentArity.ZeroOrOne };
                cmd.Arguments.Add(arg);
                positional = arg;
            }

            var option = MakeOption(kind, optionName, schema, description);
            if (LegacyShapes.OptionAliases.TryGetValue((tool.Name, name), out var alias) && usedOptionNames.Add(alias))
                option.Aliases.Add(alias);
            cmd.Options.Add(option);

            Option<string?>? fileOption = null;
            if (name == fileFills)
            {
                fileOption = new Option<string?>("--file", "-f") { Description = $"Path to a file whose TEXT is sent as '{name}'." };
                cmd.Options.Add(fileOption);
            }

            bindings.Add(new Binding(name, isRequired, optionName, result =>
            {
                var (given, value, errors) = ReadOption(result, option, kind, schema, optionName);
                if (fileOption is not null && result.GetValue(fileOption) is { Length: > 0 } filePath)
                {
                    if (given) errors = errors.Add($"Give '{name}' once: {optionName} and --file both set it.");
                    else if (!File.Exists(filePath)) errors = errors.Add($"--file: no such file '{filePath}'.");
                    else { given = true; value = JsonValue.Create(File.ReadAllText(filePath)); }
                }
                switch (positional)
                {
                    case Argument<string[]> many when result.GetValue(many) is { Length: > 0 } values:
                        if (given) errors = errors.Add($"Give '{name}' once: positionally or with {optionName}.");
                        else
                        {
                            given = true;
                            value = values.Length == 1 && values[0].TrimStart().StartsWith('[')
                                ? JsonValue.Create(values[0])
                                : JsonValue.Create(new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()).ToJsonString());
                        }
                        break;
                    case Argument<string?> one when result.GetValue(one) is { } text:
                        if (given) errors = errors.Add($"Give '{name}' once: positionally or with {optionName}.");
                        else { given = true; value = JsonValue.Create(text); }
                        break;
                }
                return (given, value, errors);
            }));
        }

        var built = bindings.ToImmutable();
        cmd.SetAction(async (result, ct) =>
        {
            var (arguments, errors) = Assemble(result, built, jsonOpt, argsFileOpt);
            if (!errors.IsEmpty)
            {
                foreach (var e in errors) await err.WriteLineAsync(e);
                await err.WriteLineAsync($"Usage: see `memex {commandName} --help`.");
                return 1;
            }
            return await invoke(tool.Name, arguments, ct);
        });
        return cmd;
    }

    private static (JsonObject Arguments, ImmutableList<string> Errors) Assemble(
        ParseResult result, ImmutableList<Binding> bindings, Option<string?> jsonOpt, Option<string?> argsFileOpt)
    {
        var errors = ImmutableList<string>.Empty;
        var arguments = new JsonObject();

        void MergeObject(string? text, string source)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                if (JsonNode.Parse(text) is JsonObject obj)
                    foreach (var (k, v) in obj) arguments[k] = v?.DeepClone();
                else errors = errors.Add($"{source} must hold a JSON object.");
            }
            catch (JsonException ex)
            {
                errors = errors.Add($"{source} is not valid JSON: {ex.Message}");
            }
        }

        if (result.GetValue(argsFileOpt) is { Length: > 0 } argsFile)
        {
            if (File.Exists(argsFile)) MergeObject(File.ReadAllText(argsFile), $"--file '{argsFile}'");
            else errors = errors.Add($"--file: no such file '{argsFile}'.");
        }
        MergeObject(result.GetValue(jsonOpt), "--json");

        foreach (var binding in bindings)
        {
            var (given, value, errs) = binding.Read(result);
            errors = errors.AddRange(errs);
            if (given) arguments[binding.Name] = value;
        }

        foreach (var binding in bindings.Where(b => b.Required))
            // JSON Schema `required` is about PRESENCE: an explicit null satisfies it.
            if (!arguments.ContainsKey(binding.Name))
                errors = errors.Add($"Missing required argument '{binding.Name}' ({binding.Display}).");

        return (arguments, errors);
    }

    /// <summary>
    /// The option name for a property: <c>--kebab</c>, or — when that is reserved or already taken —
    /// <c>--arg-kebab</c>, <c>--arg2-kebab</c>, … until one is free, so every property stays
    /// addressable on its own. <paramref name="used"/> also holds the compatibility aliases.
    /// </summary>
    public static string AllocateOptionName(string kebab, ISet<string> used)
    {
        if (!ReservedOptionNames.Contains(kebab) && used.Add("--" + kebab)) return "--" + kebab;
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? "--arg-" + kebab : $"--arg{n}-{kebab}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private enum SchemaKind { String, Enum, Integer, Number, Boolean, Array, Json }

    private static SchemaKind KindOf(JsonObject schema)
    {
        if (schema["enum"] is JsonArray values && values.Count > 0 && values.All(v => v is JsonValue jv && jv.TryGetValue<string>(out _)))
            return SchemaKind.Enum;
        return TypeName(schema) switch
        {
            "string" => SchemaKind.String,
            "integer" => SchemaKind.Integer,
            "number" => SchemaKind.Number,
            "boolean" => SchemaKind.Boolean,
            "array" => SchemaKind.Array,
            _ => SchemaKind.Json,
        };
    }

    /// <summary>The schema's simple type; a nullable union (<c>["string","null"]</c>) is its non-null member.</summary>
    private static string? TypeName(JsonObject schema) => schema["type"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => a.Select(n => n is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null)
                        .Where(s => s is not null and not "null")
                        .Distinct()
                        .SingleOrDefaultIfMany(),
        _ => null,
    };

    private static string? SingleOrDefaultIfMany(this IEnumerable<string?> source)
    {
        var list = source.Take(2).ToList();
        return list.Count == 1 ? list[0] : null;
    }

    private static Option MakeOption(SchemaKind kind, string name, JsonObject schema, string description)
    {
        switch (kind)
        {
            case SchemaKind.Enum:
            {
                var values = ((JsonArray)schema["enum"]!).Select(v => v!.GetValue<string>()).ToArray();
                var o = new Option<string?>(name) { Description = description };
                o.AcceptOnlyFromAmong(values);
                return o;
            }
            case SchemaKind.Integer: return new Option<long?>(name) { Description = description };
            case SchemaKind.Number: return new Option<double?>(name) { Description = description };
            case SchemaKind.Boolean: return new Option<bool>(name) { Description = description };
            case SchemaKind.Array:
                return new Option<string[]>(name) { Description = description + " Repeatable, or one JSON array.", Arity = ArgumentArity.OneOrMore };
            case SchemaKind.Json:
                return new Option<string?>(name) { Description = description + " A JSON value." };
            default:
                return new Option<string?>(name) { Description = description };
        }
    }

    private static (bool Given, JsonNode? Value, ImmutableList<string> Errors) ReadOption(
        ParseResult result, Option option, SchemaKind kind, JsonObject schema, string display)
    {
        var none = ImmutableList<string>.Empty;
        // An option with a default (a bool flag) has an IMPLICIT result when not typed: not "given".
        if (result.GetResult(option) is not { Implicit: false }) return (false, null, none);
        switch (option)
        {
            case Option<long?> l: return (true, JsonValue.Create(result.GetValue(l)), none);
            case Option<double?> d: return (true, JsonValue.Create(result.GetValue(d)), none);
            case Option<bool> b: return (true, JsonValue.Create(result.GetValue(b)), none);
            case Option<string[]> many:
            {
                var values = result.GetValue(many) ?? [];
                var itemSchema = schema["items"] as JsonObject;
                var allowed = itemSchema is not null && KindOf(itemSchema) == SchemaKind.Enum
                    ? ((JsonArray)itemSchema["enum"]!).Select(v => v!.GetValue<string>()).ToImmutableHashSet(StringComparer.Ordinal)
                    : null;
                if (values.Length == 1 && values[0].TrimStart().StartsWith('['))
                {
                    var parsed = ParseJson(values[0], display);
                    if (allowed is not null && parsed.Item2 is JsonArray given)
                        foreach (var item in given)
                            if (item is not JsonValue v || !v.TryGetValue<string>(out var text) || !allowed.Contains(text))
                                parsed.Item3 = parsed.Item3.Add($"{display}: {item?.ToJsonString() ?? "null"} is not one of {string.Join(", ", allowed)}.");
                    return parsed;
                }
                var itemKind = itemSchema is not null ? KindOf(itemSchema) : SchemaKind.String;
                var array = new JsonArray();
                var errors = none;
                foreach (var value in values)
                {
                    if (allowed is not null && !allowed.Contains(value))
                    {
                        errors = errors.Add($"{display}: '{value}' is not one of {string.Join(", ", allowed)}.");
                        continue;
                    }
                    var (node, error) = Convert(value, itemKind);
                    if (error is not null) errors = errors.Add($"{display}: {error}");
                    else array.Add(node);
                }
                return (true, array, errors);
            }
            case Option<string?> s:
            {
                var text = result.GetValue(s);
                if (text is null) return (true, null, none);
                return kind == SchemaKind.Json ? ParseJson(text, display) : (true, JsonValue.Create(text), none);
            }
            default:
                return (false, null, none);
        }
    }

    private static (bool, JsonNode?, ImmutableList<string>) ParseJson(string text, string display)
    {
        try { return (true, JsonNode.Parse(text), ImmutableList<string>.Empty); }
        catch (JsonException ex) { return (true, null, [$"{display} is not valid JSON: {ex.Message}"]); }
    }

    private static (JsonNode? Node, string? Error) Convert(string value, SchemaKind kind) => kind switch
    {
        SchemaKind.Integer => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            ? (JsonValue.Create(l), null) : (null, $"'{value}' is not an integer."),
        SchemaKind.Number => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? (JsonValue.Create(d), null) : (null, $"'{value}' is not a number."),
        SchemaKind.Boolean => bool.TryParse(value, out var b)
            ? (JsonValue.Create(b), null) : (null, $"'{value}' is not true or false."),
        SchemaKind.Json or SchemaKind.Array => TryParse(value),
        _ => (JsonValue.Create(value), null),
    };

    private static (JsonNode?, string?) TryParse(string value)
    {
        try { return (JsonNode.Parse(value), null); }
        catch (JsonException ex) { return (null, $"'{value}' is not valid JSON: {ex.Message}"); }
    }

    private static string Describe(JsonObject schema, bool required)
    {
        var text = schema["description"] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : string.Empty;
        if (schema["default"] is JsonValue def && def.ToJsonString() is var d && d != "null")
            text = (text + $" Default: {d}.").Trim();
        return required ? (text + " (required)").Trim() : text;
    }

    /// <summary>The first paragraph of a tool description: what <c>memex --help</c> lists.</summary>
    public static string? Summary(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var normalized = description.Replace("\r\n", "\n").Trim();
        var cut = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var first = cut < 0 ? normalized : normalized[..cut];
        return first.Replace('\n', ' ').Trim();
    }
}
