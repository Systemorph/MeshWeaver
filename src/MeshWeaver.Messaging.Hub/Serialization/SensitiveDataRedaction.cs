using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// 🚨 <b>The ONE redaction point for <see cref="SensitiveDataAttribute"/>.</b> Every rendering of an
/// object that leaves the store's access control — a log line, an exception text, a telemetry
/// attribute, a hand-over to another instance — goes through the hub's LOGGING serializer options
/// (<see cref="GetLoggingSerializerOptions"/>), and those options replace every marked member's value
/// with <see cref="SensitiveDataAttribute.Placeholder"/>:
/// <list type="bullet">
///   <item>typed objects, through the <see cref="LoggingTypeInfoResolver"/>'s per-type modifier —
///     nested records, collections, dictionaries and polymorphic <c>object</c>-typed members (each
///     runtime type is resolved through the same options, so the modifier reaches it);</item>
///   <item>untyped JSON (<see cref="JsonElement"/> / <see cref="JsonNode"/>, e.g. a node's content
///     before it is materialised), by walking the tree and resolving each <c>$type</c> through the
///     hub's type registry and the mesh-wide content-type registry — which is how a NodeType's
///     content type compiled live from its node source is reached;</item>
///   <item>a <c>$type</c> that resolves NOWHERE in this process fails CLOSED: every scalar in that
///     object is redacted, because which members are sensitive cannot be known.</item>
/// </list>
/// <para>🚨 These options are for RENDERING only and are never handed to persistence or the wire:
/// the hub's own <see cref="IMessageHub.JsonSerializerOptions"/> stay untouched, so the stored value
/// stays the value. Do not redact what an authorised reader legitimately reads — this is about text
/// that leaves the access rules behind, never about access.</para>
/// <para>Doc: <c>Doc/Architecture/SensitiveContentInLogs</c>.</para>
/// </summary>
public static class SensitiveDataRedaction
{
    /// <summary>The text a marked value is replaced with — <see cref="SensitiveDataAttribute.Placeholder"/>.</summary>
    public const string Placeholder = SensitiveDataAttribute.Placeholder;

    /// <summary>
    /// The shortest value <see cref="ScrubText"/> removes from free text. Shorter values (a single
    /// letter, a one-digit number) would erase ordinary words and numbers and say nothing.
    /// </summary>
    public const int MinimumScrubLength = 3;

    /// <summary>Whether <paramref name="member"/> carries <see cref="SensitiveDataAttribute"/>.</summary>
    /// <param name="member">A property or field.</param>
    /// <returns><c>true</c> when the member is marked.</returns>
    public static bool IsSensitive(MemberInfo? member) =>
        member is not null && member.IsDefined(typeof(SensitiveDataAttribute), inherit: true);

    /// <summary>
    /// Whether a serialized property is marked — on its property/field, or on the positional record
    /// parameter it binds to (<c>record R([SensitiveData] string X)</c> puts the attribute on the
    /// parameter only).
    /// </summary>
    /// <param name="property">The resolved JSON property.</param>
    /// <returns><c>true</c> when the property is marked.</returns>
    public static bool IsSensitive(JsonPropertyInfo property)
    {
        if (property.AttributeProvider is not MemberInfo member)
            return false;
        if (IsSensitive(member))
            return true;
        if (property.AssociatedParameter?.AttributeProvider is ParameterInfo associated)
            return associated.IsDefined(typeof(SensitiveDataAttribute), inherit: true);
        // The association is made when the type info is CONFIGURED, after the modifiers ran — so
        // match the declaring type's constructor parameters by name as well.
        return member.DeclaringType is { } declaring
            && declaring.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Any(p => string.Equals(p.Name, member.Name, StringComparison.OrdinalIgnoreCase)
                          && p.IsDefined(typeof(SensitiveDataAttribute), inherit: true));
    }

    /// <summary>
    /// The modifier the logging resolver applies to every object type: each marked property is
    /// replaced by a string property of the same JSON name whose value is the placeholder (null
    /// stays null). Runs only on the logging options' own <see cref="JsonTypeInfo"/> instances.
    /// </summary>
    /// <param name="typeInfo">An object type's metadata, resolved for the logging options.</param>
    internal static void RedactSensitiveMembers(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;
        for (var i = 0; i < typeInfo.Properties.Count; i++)
        {
            var original = typeInfo.Properties[i];
            if (!IsSensitive(original))
                continue;
            var getter = original.Get;
            var replacement = typeInfo.CreateJsonPropertyInfo(typeof(string), original.Name);
            replacement.Order = original.Order;
            replacement.Get = getter is null ? null : owner => getter(owner) is null ? null : Placeholder;
            replacement.ShouldSerialize = (_, value) => value is not null;
            typeInfo.Properties[i] = replacement;
        }
    }

    /// <summary>
    /// The hub's logging serializer options — built once per hub and cached on it. Use these for
    /// EVERY rendering of an object into a log line, an exception text, telemetry or an egress
    /// payload; never the hub's own <see cref="IMessageHub.JsonSerializerOptions"/>.
    /// </summary>
    /// <param name="hub">The hub whose type registry and converters the rendering uses.</param>
    /// <returns>Write-only options that strip <c>[PreventLogging]</c> members and redact
    /// <c>[SensitiveData]</c> values.</returns>
    public static JsonSerializerOptions GetLoggingSerializerOptions(this IMessageHub hub) =>
        hub is MessageHub concrete ? concrete.LoggingSerializerOptions : hub.CreateLoggingSerializerOptions();

    /// <summary>
    /// Renders <paramref name="value"/> as JSON for a log line or any other text that leaves the
    /// access rules behind, through <see cref="GetLoggingSerializerOptions"/>. Never throws: a value
    /// the options cannot render is named by its type.
    /// </summary>
    /// <param name="hub">The hub whose logging options are used.</param>
    /// <param name="value">The value to render; may be null.</param>
    /// <returns>The redacted JSON, or a type-only description.</returns>
    public static string RenderForLog(this IMessageHub hub, object? value)
    {
        if (value is null)
            return "null";
        try
        {
            return JsonSerializer.Serialize(value, value.GetType(), hub.GetLoggingSerializerOptions());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return $"{value.GetType().Name} [log-render failed: {ex.GetType().Name}]";
        }
    }

    /// <summary>
    /// Redacts JSON TEXT — a payload, a tool argument, a stored document — for a log line or any
    /// other egress. Marked members are found through each object's <c>$type</c>; text that does not
    /// parse is never echoed (fail closed): only its length is reported.
    /// </summary>
    /// <param name="hub">The hub whose registries resolve <c>$type</c>.</param>
    /// <param name="json">The JSON text; may be null.</param>
    /// <returns>The redacted JSON text, or a length-only description.</returns>
    public static string RedactJson(this IMessageHub hub, string? json)
    {
        if (string.IsNullOrEmpty(json))
            return json ?? "null";
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return $"‹unparseable JSON, {json.Length} chars, not rendered›";
        }
        return Redactor(hub).Redact(node, declared: null)?.ToJsonString(hub.GetLoggingSerializerOptions()) ?? "null";
    }

    /// <summary>
    /// Every marked value <paramref name="content"/> holds, as text — the input for
    /// <see cref="ScrubText"/> when free text written ABOUT the content (a gist, a summary an agent
    /// wrote) is about to leave the instance. Values shorter than <see cref="MinimumScrubLength"/>
    /// and booleans are not collected.
    /// </summary>
    /// <param name="hub">The hub whose serializer and registries type the content.</param>
    /// <param name="content">Typed content, or untyped JSON carrying a <c>$type</c>.</param>
    /// <returns>The distinct marked values.</returns>
    public static ImmutableHashSet<string> SensitiveValues(this IMessageHub hub, object? content) =>
        hub.SensitiveValues(content, out _);

    /// <summary>
    /// <see cref="SensitiveValues(IMessageHub, object?)"/>, and whether the answer is COMPLETE:
    /// <paramref name="complete"/> is <c>false</c> when some object's <c>$type</c> resolves nowhere in
    /// this process (its marked members cannot be known) or the content could not be serialised. An
    /// egress path that must not leak treats an incomplete answer as "unknown" and fails closed.
    /// </summary>
    /// <param name="hub">The hub whose serializer and registries type the content.</param>
    /// <param name="content">Typed content, or untyped JSON carrying a <c>$type</c>.</param>
    /// <param name="complete">Whether every object in the content was typed.</param>
    /// <returns>The distinct marked values found.</returns>
    public static ImmutableHashSet<string> SensitiveValues(this IMessageHub hub, object? content, out bool complete)
    {
        var sink = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        complete = true;
        if (content is null)
            return sink.ToImmutable();
        var redactor = Redactor(hub);
        switch (content)
        {
            case JsonElement element:
                complete = redactor.Collect(SensitiveJsonRedactor.FromElement(element), null, sink);
                break;
            case JsonNode node:
                complete = redactor.Collect(node, null, sink);
                break;
            default:
                JsonNode? serialized;
                try
                {
                    serialized = JsonSerializer.SerializeToNode(content, content.GetType(), hub.JsonSerializerOptions);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    complete = false;
                    return sink.ToImmutable();
                }
                complete = redactor.Collect(serialized, content.GetType(), sink);
                break;
        }
        return sink.ToImmutable();
    }

    /// <summary>
    /// Replaces every occurrence of each of <paramref name="values"/> in <paramref name="text"/>
    /// with the placeholder (case-insensitive, longest value first). For FREE TEXT written about
    /// content — the attribute cannot see into prose, so the values themselves are removed.
    /// </summary>
    /// <param name="text">The free text; may be null.</param>
    /// <param name="values">The values to remove (see <see cref="SensitiveValues(IMessageHub, object?)"/>).</param>
    /// <returns>The scrubbed text.</returns>
    public static string? ScrubText(string? text, IEnumerable<string> values)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        foreach (var value in values
                     .Where(v => v.Length >= MinimumScrubLength)
                     .OrderByDescending(v => v.Length)
                     .ThenBy(v => v, StringComparer.Ordinal))
            text = text.Replace(value, Placeholder, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>
    /// The hub's JSON-tree redactor — cached alongside the logging options on a
    /// <see cref="MessageHub"/>.
    /// </summary>
    internal static SensitiveJsonRedactor Redactor(IMessageHub hub) =>
        hub is MessageHub concrete ? concrete.SensitiveRedactor : CreateRedactor(hub);

    internal static SensitiveJsonRedactor CreateRedactor(IMessageHub hub)
    {
        var contentTypes = hub.ServiceProvider.GetService<IMeshContentTypeRegistry>();
        var registry = hub.TypeRegistry;
        return new SensitiveJsonRedactor(hub.JsonSerializerOptions, name =>
            registry.GetType(name)
            ?? (contentTypes is not null && contentTypes.TryResolveByDiscriminator(name, out var compiled)
                ? compiled
                : null));
    }
}

/// <summary>
/// Walks a JSON tree with the CLR type it describes and finds every <see cref="SensitiveDataAttribute"/>
/// member — to redact it (on a COPY; the input is never mutated) or to collect its values.
/// </summary>
/// <param name="typing">The hub's own options — read for metadata (property names, element types)
/// only, never used to write.</param>
/// <param name="resolve">Resolves a <c>$type</c> discriminator to its CLR type, or null.</param>
internal sealed class SensitiveJsonRedactor(JsonSerializerOptions typing, Func<string, Type?> resolve)
{
    // Weak keys: a NodeType content type lives in a collectible assembly, and a cache on a long-lived
    // hub must never pin it past its recompile.
    private readonly ConditionalWeakTable<Type, Shape> shapes = new();

    /// <summary>What a JSON object/array of one CLR type looks like to the walk.</summary>
    private sealed record Shape(
        JsonTypeInfoKind Kind,
        ImmutableDictionary<string, (bool Sensitive, Type Type)> Members,
        Type? ElementType);

    private static readonly Shape Opaque =
        new(JsonTypeInfoKind.None, ImmutableDictionary<string, (bool, Type)>.Empty, null);

    /// <summary>A mutable node over an element — the element itself is immutable, so edits copy.</summary>
    internal static JsonNode? FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonObject.Create(element),
        JsonValueKind.Array => JsonArray.Create(element),
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        _ => JsonValue.Create(element),
    };

    /// <summary>Returns a redacted COPY of <paramref name="node"/>.</summary>
    public JsonNode? Redact(JsonNode? node, Type? declared)
    {
        var copy = node?.DeepClone();
        Walk(copy, declared, failClosed: false, onSensitive: (parent, set) => set(JsonValue.Create(SensitiveDataAttribute.Placeholder)));
        return copy;
    }

    /// <summary>
    /// Adds every marked value under <paramref name="node"/> to <paramref name="sink"/>; returns
    /// <c>false</c> when some object's <c>$type</c> could not be resolved (the answer is incomplete).
    /// </summary>
    public bool Collect(JsonNode? node, Type? declared, ImmutableHashSet<string>.Builder sink)
    {
        var complete = true;
        Walk(node, declared, failClosed: false, onSensitive: (value, _) => AddLeaves(value, sink),
            redactOpaque: false, onUnresolved: () => complete = false);
        return complete;
    }

    private static void AddLeaves(JsonNode? node, ImmutableHashSet<string>.Builder sink)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                    AddLeaves(child, sink);
                break;
            case JsonArray array:
                foreach (var child in array)
                    AddLeaves(child, sink);
                break;
            case JsonValue value:
                var text = value.GetValueKind() switch
                {
                    JsonValueKind.String => value.GetValue<string>(),
                    JsonValueKind.Number => value.ToJsonString(),
                    _ => null,
                };
                if (text is { Length: >= SensitiveDataRedaction.MinimumScrubLength })
                    sink.Add(text);
                break;
        }
    }

    private void Walk(JsonNode? node, Type? declared, bool failClosed,
        Action<JsonNode?, Action<JsonNode?>> onSensitive, bool redactOpaque = true, Action? onUnresolved = null)
    {
        switch (node)
        {
            case JsonObject obj:
                var type = declared;
                var closed = failClosed;
                if (obj.TryGetPropertyValue("$type", out var discriminator)
                    && discriminator is JsonValue dv && dv.GetValueKind() == JsonValueKind.String)
                {
                    var resolved = resolve(dv.GetValue<string>());
                    if (resolved is not null)
                        type = resolved;
                    else if (!IsKnownStructural(declared))
                    {
                        // A $type nobody in this process can resolve: which members are sensitive is
                        // unknowable, so a RENDERING fails closed and redacts every scalar below.
                        closed = redactOpaque;
                        onUnresolved?.Invoke();
                    }
                }
                var shape = type is null ? Opaque : ShapeOf(type);
                foreach (var name in obj.Select(p => p.Key).ToArray())
                {
                    var child = obj[name];
                    if (name == "$type")
                        continue;
                    if (shape.Kind == JsonTypeInfoKind.Object && shape.Members.TryGetValue(name, out var member))
                    {
                        if (member.Sensitive)
                        {
                            if (child is not null)
                                onSensitive(child, replacement => obj[name] = replacement);
                            continue;
                        }
                        Walk(child, member.Type, closed, onSensitive, redactOpaque, onUnresolved);
                    }
                    else if (shape.Kind == JsonTypeInfoKind.Dictionary)
                        Walk(child, shape.ElementType, closed, onSensitive, redactOpaque, onUnresolved);
                    else if (closed && child is JsonValue)
                        obj[name] = JsonValue.Create(SensitiveDataAttribute.Placeholder);
                    else
                        Walk(child, null, closed, onSensitive, redactOpaque, onUnresolved);
                }
                break;
            case JsonArray array:
                var elementType = declared is null ? null : ShapeOf(declared) is { Kind: JsonTypeInfoKind.Enumerable } s ? s.ElementType : null;
                for (var i = 0; i < array.Count; i++)
                {
                    if (failClosed && array[i] is JsonValue)
                        array[i] = JsonValue.Create(SensitiveDataAttribute.Placeholder);
                    else
                        Walk(array[i], elementType, failClosed, onSensitive, redactOpaque, onUnresolved);
                }
                break;
        }
    }

    /// <summary>A declared type whose shape is fully known without a discriminator.</summary>
    private static bool IsKnownStructural(Type? declared) =>
        declared is not null && declared != typeof(object) && !declared.IsAbstract && !declared.IsInterface;

    private Shape ShapeOf(Type type) => shapes.GetValue(type, Build);

    private Shape Build(Type type)
    {
        if (type == typeof(object) || type == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(type))
            return Opaque;
        JsonTypeInfo info;
        try
        {
            info = typing.GetTypeInfo(type);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return Opaque;
        }
        return info.Kind switch
        {
            JsonTypeInfoKind.Object => new Shape(JsonTypeInfoKind.Object,
                info.Properties
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableDictionary(
                        g => g.Key,
                        g => (g.Any(SensitiveDataRedaction.IsSensitive), g.First().PropertyType),
                        StringComparer.OrdinalIgnoreCase),
                null),
            JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary =>
                new Shape(info.Kind, ImmutableDictionary<string, (bool, Type)>.Empty, info.ElementType),
            _ => Opaque,
        };
    }
}

/// <summary>
/// Logging-options converter for <c>object</c>-typed members: untyped JSON is walked and redacted;
/// anything else is handed to the hub's own polymorphic converter, which serialises the runtime type
/// through these same options — so the per-type modifier reaches it.
/// </summary>
internal sealed class SensitiveObjectConverter(SensitiveJsonRedactor redactor, JsonConverter<object>? inner)
    : JsonConverter<object>
{
    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("The logging serializer options are write-only.");

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement element:
                WriteNode(writer, redactor.Redact(SensitiveJsonRedactor.FromElement(element), null));
                return;
            case JsonNode node:
                WriteNode(writer, redactor.Redact(node, null));
                return;
        }
        if (inner is not null)
            inner.Write(writer, value, options);
        else if (value.GetType() == typeof(object))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
        else
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    internal static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        if (node is null)
            writer.WriteNullValue();
        else
            node.WriteTo(writer);
    }
}

/// <summary>Logging-options converter for <see cref="JsonElement"/>-typed members.</summary>
internal sealed class SensitiveJsonElementConverter(SensitiveJsonRedactor redactor) : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("The logging serializer options are write-only.");

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) =>
        SensitiveObjectConverter.WriteNode(writer, redactor.Redact(SensitiveJsonRedactor.FromElement(value), null));
}

/// <summary>Logging-options converter for <see cref="JsonNode"/>-typed members (and subtypes).</summary>
internal sealed class SensitiveJsonNodeConverter(SensitiveJsonRedactor redactor) : JsonConverter<JsonNode>
{
    public override bool CanConvert(Type typeToConvert) => typeof(JsonNode).IsAssignableFrom(typeToConvert);

    public override JsonNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("The logging serializer options are write-only.");

    public override void Write(Utf8JsonWriter writer, JsonNode value, JsonSerializerOptions options) =>
        SensitiveObjectConverter.WriteNode(writer, redactor.Redact(value, null));
}
