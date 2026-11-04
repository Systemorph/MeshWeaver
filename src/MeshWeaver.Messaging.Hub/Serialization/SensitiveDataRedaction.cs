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
        // Attribute.IsDefined, never MemberInfo.IsDefined: the latter ignores `inherit` for
        // properties, so an override that does not repeat the attribute would drop the protection.
        member is not null && Attribute.IsDefined(member, typeof(SensitiveDataAttribute), inherit: true);

    /// <summary>
    /// Whether <paramref name="type"/> declares any marked member — a public property or field, or a
    /// public constructor parameter.
    /// </summary>
    /// <param name="type">The type to inspect.</param>
    /// <returns><c>true</c> when some member is marked.</returns>
    public static bool DeclaresSensitiveMembers(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(IsSensitive)
        || type.GetFields(BindingFlags.Public | BindingFlags.Instance).Any(IsSensitive)
        || type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Any(p => Attribute.IsDefined(p, typeof(SensitiveDataAttribute), inherit: true));

    /// <summary>
    /// Whether <paramref name="type"/> declares a marked member ITSELF or anywhere below it — through
    /// its members' types and collection element types (platform <c>System.*</c> types are not
    /// entered). The question a custom converter raises: it may write any descendant verbatim.
    /// </summary>
    /// <param name="type">The type to inspect.</param>
    /// <returns><c>true</c> when a marked member is reachable.</returns>
    public static bool ReachesSensitiveMembers(Type type) => Reaches(type, ImmutableHashSet<Type>.Empty, 0);

    private static bool Reaches(Type type, ImmutableHashSet<Type> seen, int depth)
    {
        if (depth > 8 || seen.Contains(type) || type.IsPrimitive || type.IsEnum || type == typeof(string)
            || type == typeof(object))
            return false;
        if (type.IsArray)
            return Reaches(type.GetElementType()!, seen, depth + 1);
        seen = seen.Add(type);
        if (type.IsGenericType && type.GetGenericArguments().Any(a => Reaches(a, seen, depth + 1)))
            return true;
        if (type.Namespace is { } ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)))
            return false;
        if (DeclaresSensitiveMembers(type))
            return true;
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                   .Where(p => p.GetIndexParameters().Length == 0)
                   .Any(p => Reaches(p.PropertyType, seen, depth + 1))
               || type.GetFields(BindingFlags.Public | BindingFlags.Instance).Any(f => Reaches(f.FieldType, seen, depth + 1));
    }

    /// <summary>The element type of a collection (and the value type of a dictionary), or null.</summary>
    internal static Type? ElementTypeOf(Type type, out bool dictionary)
    {
        dictionary = false;
        if (type == typeof(string))
            return null;
        if (type.IsArray)
            return type.GetElementType();
        var interfaces = type.IsInterface ? type.GetInterfaces().Append(type) : type.GetInterfaces();
        var dict = interfaces.FirstOrDefault(i => i.IsGenericType
            && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (dict is not null)
        {
            dictionary = true;
            return dict.GetGenericArguments()[1];
        }
        return interfaces.FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

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
        if (property.AssociatedParameter?.AttributeProvider is ParameterInfo associated
            && Attribute.IsDefined(associated, typeof(SensitiveDataAttribute), inherit: true))
            return true;
        // The association is made when the type info is CONFIGURED, after the modifiers ran, and a
        // derived positional record re-declares the parameter unmarked (`record Derived(string X) :
        // Base(X)`) — so match constructor parameters by name up the whole hierarchy.
        for (var declaring = member.DeclaringType; declaring is not null && declaring != typeof(object); declaring = declaring.BaseType)
            if (declaring.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Any(p => string.Equals(p.Name, member.Name, StringComparison.OrdinalIgnoreCase)
                          && Attribute.IsDefined(p, typeof(SensitiveDataAttribute), inherit: true)))
                return true;
        return false;
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
            if (!IsSensitive(original) && !HidesSensitiveMembers(original))
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
    /// Whether a property is written by its OWN converter (<c>[JsonConverter]</c> on the property)
    /// while its type declares marked members: the converter can write them under any name, so the
    /// member is treated as marked as a whole.
    /// </summary>
    /// <param name="property">The resolved JSON property.</param>
    /// <returns><c>true</c> when the property's converter hides marked members.</returns>
    internal static bool HidesSensitiveMembers(JsonPropertyInfo property) =>
        property.CustomConverter is not null && ReachesSensitiveMembers(property.PropertyType);

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

    /// <summary>One member of an object shape.</summary>
    /// <param name="Sensitive">Marked — or written by its own converter over marked members.</param>
    /// <param name="Opaque">Written by its own converter: its JSON cannot be mapped (collection incomplete).</param>
    /// <param name="Type">The member's declared type.</param>
    private readonly record struct Member(bool Sensitive, bool Opaque, Type Type);

    /// <summary>What a JSON object/array of one CLR type looks like to the walk.</summary>
    /// <param name="Kind">The type's JSON shape.</param>
    /// <param name="Members">For an object: JSON member name → member.</param>
    /// <param name="ElementType">For a collection or dictionary: the element (value) type.</param>
    /// <param name="WhollySensitive">A type that declares marked members but is written by a custom
    /// converter: its JSON cannot be mapped to members, so ALL of it is treated as marked.</param>
    /// <param name="Extension">The <c>[JsonExtensionData]</c> member, whose entries are flattened into
    /// the object and match no member name.</param>
    private sealed record Shape(
        JsonTypeInfoKind Kind,
        ImmutableDictionary<string, Member> Members,
        Type? ElementType,
        bool WhollySensitive = false,
        Member? Extension = null);

    private static readonly Shape Opaque =
        new(JsonTypeInfoKind.None, ImmutableDictionary<string, Member>.Empty, null);

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
        if (node is not null && declared is not null && ShapeOf(declared).WhollySensitive)
            return JsonValue.Create(SensitiveDataAttribute.Placeholder);
        var copy = node?.DeepClone();
        Walk(copy, declared, failClosed: false, redact: true,
            onSensitive: (_, _, set) => set(JsonValue.Create(SensitiveDataAttribute.Placeholder)), onUnresolved: null);
        return copy;
    }

    /// <summary>
    /// Adds every marked value under <paramref name="node"/> to <paramref name="sink"/>; returns
    /// <c>false</c> when the answer is incomplete — some object's <c>$type</c> could not be resolved,
    /// or a type's members could not be mapped.
    /// </summary>
    public bool Collect(JsonNode? node, Type? declared, ImmutableHashSet<string>.Builder sink)
    {
        if (node is not null && declared is not null && ShapeOf(declared).WhollySensitive)
        {
            // A marked root written by its own converter — whatever JSON shape it chose.
            AddLeaves(node, null, sink);
            return false;
        }
        var complete = true;
        Walk(node, declared, failClosed: false, redact: false,
            onSensitive: (value, type, _) => AddLeaves(value, type, sink), onUnresolved: () => complete = false);
        return complete;
    }

    /// <summary>
    /// Every value under a MARKED member. Keys count as values where they are data — a dictionary's
    /// keys, or the keys of an object no type describes — and never where they are a type's member names.
    /// </summary>
    private void AddLeaves(JsonNode? node, Type? type, ImmutableHashSet<string>.Builder sink)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var described = obj.TryGetPropertyValue("$type", out var d) && d is JsonValue dv
                                && dv.GetValueKind() == JsonValueKind.String
                    ? resolve(dv.GetValue<string>()) ?? (IsExact(type) ? type : null)
                    : IsExact(type) ? type : null;
                var shape = described is null ? Opaque : ShapeOf(described);
                foreach (var (key, child) in obj)
                {
                    if (key == "$type")
                        continue;
                    if (shape.Kind == JsonTypeInfoKind.Object)
                    {
                        AddLeaves(child, shape.Members.TryGetValue(key, out var m) ? m.Type : null, sink);
                        continue;
                    }
                    if (key.Length >= SensitiveDataRedaction.MinimumScrubLength)
                        sink.Add(key);
                    AddLeaves(child, shape.Kind == JsonTypeInfoKind.Dictionary ? shape.ElementType : null, sink);
                }
                break;
            }
            case JsonArray array:
            {
                var elementType = type is null ? null : ShapeOf(type) is { Kind: JsonTypeInfoKind.Enumerable } s ? s.ElementType : null;
                foreach (var child in array)
                    AddLeaves(child, elementType, sink);
                break;
            }
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

    /// <param name="failClosed">An enclosing object's members could not be known: every scalar below
    /// is redacted (rendering only).</param>
    /// <param name="redact">Rendering (true) or collecting (false).</param>
    private void Walk(JsonNode? node, Type? declared, bool failClosed, bool redact,
        Action<JsonNode?, Type?, Action<JsonNode?>> onSensitive, Action? onUnresolved)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var type = ResolveObjectType(obj, declared, out var resolvedAll);
                var closed = failClosed;
                if (!resolvedAll)
                {
                    // Which members are marked cannot be known. A COLLECTION reports itself incomplete;
                    // a RENDERING keeps only the identifying discriminator — keys can be data too.
                    onUnresolved?.Invoke();
                    if (redact)
                    {
                        var discriminator = obj.TryGetPropertyValue("$type", out var d) && d is JsonValue dv
                                            && dv.GetValueKind() == JsonValueKind.String ? dv.GetValue<string>() : null;
                        obj.Clear();
                        if (discriminator is not null)
                            obj["$type"] = discriminator;
                        obj["content"] = SensitiveDataAttribute.Placeholder;
                        return;
                    }
                }
                var shape = type is null ? Opaque : ShapeOf(type);
                if (shape.WhollySensitive)
                {
                    onUnresolved?.Invoke();
                    if (!redact)
                    {
                        onSensitive(obj, null, _ => { });
                        return;
                    }
                    closed = true;
                }
                foreach (var name in obj.Select(p => p.Key).ToArray())
                {
                    if (name == "$type")
                        continue;
                    var child = obj[name];
                    // A JSON member no declared member matches is an extension-data entry when the
                    // type has an extension bag: it inherits the bag's mark.
                    var extensionEntry = shape.Kind == JsonTypeInfoKind.Object && shape.Extension is not null
                                         && !shape.Members.ContainsKey(name);
                    Member? member = shape.Kind == JsonTypeInfoKind.Object
                        ? shape.Members.TryGetValue(name, out var m) ? m : shape.Extension
                        : null;
                    var childType = member?.Type ?? (shape.Kind == JsonTypeInfoKind.Dictionary ? shape.ElementType : null);
                    var wholly = childType is not null && child is not null && ShapeOf(childType).WhollySensitive;
                    if (member?.Sensitive == true || wholly)
                    {
                        // A custom-written member is treated as marked as a whole; its members could
                        // not be mapped, so the answer is reported incomplete.
                        if (wholly || member?.Opaque == true)
                            onUnresolved?.Invoke();
                        if (extensionEntry)
                        {
                            // A marked extension bag's KEYS are its data too: collected, and renamed
                            // away in a rendering.
                            if (!redact)
                                onSensitive(JsonValue.Create(name), null, _ => { });
                            else
                            {
                                obj.Remove(name);
                                var key = SensitiveDataAttribute.Placeholder;
                                for (var n = 2; obj.ContainsKey(key); n++)
                                    key = SensitiveDataAttribute.Placeholder + n;
                                obj[key] = JsonValue.Create(SensitiveDataAttribute.Placeholder);
                                continue;
                            }
                        }
                        if (child is not null)
                            onSensitive(child, member?.Opaque == true ? null : childType, replacement => obj[name] = replacement);
                        continue;
                    }
                    // Closed-scalar replacement BEFORE the member and dictionary branches: under an
                    // unknowable outer type even a known inner member could be the marked one.
                    if (closed && child is JsonValue)
                    {
                        obj[name] = JsonValue.Create(SensitiveDataAttribute.Placeholder);
                        continue;
                    }
                    Walk(child, childType, closed, redact, onSensitive, onUnresolved);
                }
                break;
            }
            case JsonArray array:
            {
                var elementType = declared is null ? null : ShapeOf(declared) is { Kind: JsonTypeInfoKind.Enumerable } s ? s.ElementType : null;
                for (var i = 0; i < array.Count; i++)
                {
                    if (elementType is not null && ShapeOf(elementType).WhollySensitive && array[i] is { } whole)
                    {
                        onUnresolved?.Invoke();
                        var index = i;
                        onSensitive(whole, null, replacement => array[index] = replacement);
                    }
                    else if (failClosed && array[i] is JsonValue)
                        array[i] = JsonValue.Create(SensitiveDataAttribute.Placeholder);
                    else
                        Walk(array[i], elementType, failClosed, redact, onSensitive, onUnresolved);
                }
                break;
            }
        }
    }

    /// <summary>
    /// The CLR type an object describes. An EXACT declared type (a runtime type, a concrete member
    /// type) is authoritative when the object's <c>$type</c> names it — never swapped for whatever
    /// generation the discriminator resolves to now, which after a rebuild may carry other
    /// annotations — and is refined only to a resolved SUBTYPE. A discriminator that names neither the
    /// declared type nor anything resolvable leaves <paramref name="resolved"/> false: fail closed.
    /// </summary>
    private Type? ResolveObjectType(JsonObject obj, Type? declared, out bool resolved)
    {
        resolved = true;
        var exact = IsExact(declared) ? declared : null;
        if (!obj.TryGetPropertyValue("$type", out var discriminatorNode))
            // No discriminator: the DECLARED contract still describes it — an interface dictionary's
            // value type, an abstract type's (marked) members — so keep it, not only an exact type.
            return declared is not null && declared != typeof(object) && declared != typeof(JsonElement)
                   && !typeof(JsonNode).IsAssignableFrom(declared)
                ? declared
                : null;
        if (discriminatorNode is not JsonValue dv || dv.GetValueKind() != JsonValueKind.String)
        {
            // A present but malformed discriminator (number, null, array, object) names nothing.
            resolved = false;
            return exact;
        }
        var discriminator = dv.GetValue<string>();
        if (exact is not null && Names(discriminator, exact))
            return exact;
        var candidate = resolve(discriminator);
        if (candidate is not null && (exact is null || exact.IsAssignableFrom(candidate)))
            return candidate;
        resolved = false;
        return exact;
    }

    private static bool Names(string discriminator, Type type) =>
        string.Equals(discriminator, type.Name, StringComparison.Ordinal)
        || string.Equals(discriminator, type.FullName, StringComparison.Ordinal);

    /// <summary>A declared type whose shape is fully known without a discriminator.</summary>
    private static bool IsExact(Type? declared) =>
        declared is not null && declared != typeof(object) && declared != typeof(JsonElement)
        && !typeof(JsonNode).IsAssignableFrom(declared) && !declared.IsAbstract && !declared.IsInterface;

    private Shape ShapeOf(Type type) => shapes.GetValue(type, Build);

    private static Member MemberOf(IEnumerable<JsonPropertyInfo> properties)
    {
        var all = properties.ToArray();
        var opaque = all.Any(SensitiveDataRedaction.HidesSensitiveMembers);
        return new Member(all.Any(SensitiveDataRedaction.IsSensitive) || opaque, opaque, all[0].PropertyType);
    }

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
            // Metadata that cannot be built maps nothing: fail closed when marked members are reachable.
            return SensitiveDataRedaction.ReachesSensitiveMembers(type) ? Opaque with { WhollySensitive = true } : Opaque;
        }
        if (info.Kind == JsonTypeInfoKind.None && SensitiveDataRedaction.ElementTypeOf(type, out var isDictionary) is { } element)
            // A collection written by a converter (the hub's ReadOnlyCollectionConverterFactory gives
            // IReadOnlyList<T>/IEnumerable<T> Kind None): keep its element shape — those converters write
            // each element through the options.
            return new Shape(isDictionary ? JsonTypeInfoKind.Dictionary : JsonTypeInfoKind.Enumerable,
                ImmutableDictionary<string, Member>.Empty, element);
        return info.Kind switch
        {
            JsonTypeInfoKind.Object => new Shape(JsonTypeInfoKind.Object,
                info.Properties
                    .Where(p => !p.IsExtensionData)
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableDictionary(
                        g => g.Key,
                        g => MemberOf(g),
                        StringComparer.OrdinalIgnoreCase),
                null,
                Extension: info.Properties.FirstOrDefault(p => p.IsExtensionData) is { } bag
                    ? MemberOf([bag]) with { Type = typeof(object) }
                    : null),
            JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary =>
                new Shape(info.Kind, ImmutableDictionary<string, Member>.Empty, info.ElementType),
            // Written by a custom converter: its JSON cannot be mapped to members. If it declares any
            // marked member, all of it is treated as marked.
            _ => SensitiveDataRedaction.ReachesSensitiveMembers(type) ? Opaque with { WhollySensitive = true } : Opaque,
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
            case JsonDocument document:
                WriteNode(writer, redactor.Redact(SensitiveJsonRedactor.FromElement(document.RootElement), null));
                return;
        }
        // A plain System.Object first: the inner converter would resolve `object` through these same
        // options — i.e. back to this converter — and recurse without its depth guard.
        if (value.GetType() == typeof(object))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
        else if (inner is not null)
            inner.Write(writer, value, options);
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

/// <summary>
/// Logging-options converter factory for a type that declares <see cref="SensitiveDataAttribute"/>
/// members but is written by a CUSTOM converter (its metadata has no properties, so the resolver's
/// modifier cannot reach the members): the whole value is written as the placeholder — fail closed.
/// </summary>
internal sealed class SensitiveOpaqueTypeConverterFactory(JsonSerializerOptions typing) : JsonConverterFactory
{
    private readonly ConditionalWeakTable<Type, object> verdicts = new();
    private static readonly object Yes = new();
    private static readonly object No = new();

    public override bool CanConvert(Type typeToConvert) =>
        verdicts.GetValue(typeToConvert, Decide) == Yes;

    private object Decide(Type type)
    {
        // A collection's converter writes each element through these options (the element type's own
        // modifier then applies), so a collection is never claimed here as a whole.
        if (type.IsPrimitive || type == typeof(string) || type == typeof(object) || type.IsEnum
            || SensitiveDataRedaction.ElementTypeOf(type, out _) is not null
            || !SensitiveDataRedaction.ReachesSensitiveMembers(type))
            return No;
        try
        {
            return typing.GetTypeInfo(type).Kind == JsonTypeInfoKind.None ? Yes : No;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return Yes;
        }
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(PlaceholderConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class PlaceholderConverter<T> : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("The logging serializer options are write-only.");

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(SensitiveDataAttribute.Placeholder);
    }
}

/// <summary>
/// Logging-options converter for <see cref="JsonDocument"/>: STJ's own converter writes a document
/// directly and bypasses the <see cref="JsonElement"/> converter, so its root is redacted here.
/// </summary>
internal sealed class SensitiveJsonDocumentConverter(SensitiveJsonRedactor redactor) : JsonConverter<JsonDocument>
{
    public override JsonDocument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("The logging serializer options are write-only.");

    public override void Write(Utf8JsonWriter writer, JsonDocument value, JsonSerializerOptions options) =>
        SensitiveObjectConverter.WriteNode(writer, redactor.Redact(SensitiveJsonRedactor.FromElement(value.RootElement), null));
}
