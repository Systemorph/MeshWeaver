using System.Text.Json;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// One property of a NodeType's content, as its JSON Schema states it — what a query completer needs
/// to offer <c>content.&lt;name&gt;:</c> and its values.
/// </summary>
/// <param name="Name">The camelCase property name, as a query addresses it (<c>content.status</c>).</param>
/// <param name="JsonType">The JSON type (<c>string</c>, <c>boolean</c>, <c>integer</c>, <c>number</c>, <c>array</c>,
/// <c>object</c>), or null when the schema states none (an enum or a union).</param>
/// <param name="EnumValues">The allowed values when the schema lists them (a C# enum), else empty.</param>
/// <param name="Description">The property's description (its XML doc), authored content — rendered as written.</param>
/// <param name="ReferenceQueries">For a property marked <c>[MeshNode("…")]</c>: the queries whose hits are its
/// allowed values (the schema's <see cref="ContentSchemaFacts.MeshNodeExtension"/>), else empty.</param>
/// <param name="Format">The schema <c>format</c> (<c>date-time</c>, …), when stated.</param>
public sealed record ContentPropertyFact(
    string Name,
    string? JsonType,
    IReadOnlyList<string> EnumValues,
    string? Description,
    IReadOnlyList<string> ReferenceQueries,
    string? Format = null)
{
    /// <summary>The property holds a boolean.</summary>
    public bool IsBoolean => JsonType == "boolean";

    /// <summary>The property holds a number.</summary>
    public bool IsNumber => JsonType is "integer" or "number";
}

/// <summary>
/// Reads the content properties out of a NodeType's JSON Schema (the document <c>@{NodeType}/schema/</c>
/// answers — <c>SchemaReference</c> → <c>SchemaInfo.Schema</c>). Pure.
/// <para>Shape-tolerant on purpose: a polymorphic content type comes back as
/// <c>{"anyOf":[{"properties":{…}}]}</c>, a plain one as <c>{"properties":{…}}</c>, a nullable property as
/// <c>"type":["string","null"]</c>; all of them are read, the <c>$type</c> discriminator is skipped, and a
/// document that is not a schema yields NO properties rather than an error — a completer that cannot read a
/// schema offers nothing for it, it does not fail the whole suggest list.</para>
/// </summary>
public static class ContentSchemaFacts
{
    /// <summary>
    /// The JSON Schema extension keyword carrying a <c>[MeshNode("…")]</c> property's queries
    /// (<c>"x-meshNode": ["nodeType:Crm/CounterpartyType"]</c>) — <see cref="MeshWeaver.Data.ContentJsonSchema.MeshNodeExtension"/>.
    /// </summary>
    public const string MeshNodeExtension = MeshWeaver.Data.ContentJsonSchema.MeshNodeExtension;

    /// <summary>
    /// The content properties of a CLR content type, read through the same generator the mesh publishes
    /// its schemas with (<see cref="MeshWeaver.Data.ContentJsonSchema"/>).
    /// </summary>
    /// <param name="contentType">The content type.</param>
    /// <param name="options">The serializer options its instances are written with.</param>
    /// <param name="docs">The mesh's XML-docs reader (<c>hub.XmlDocs()</c>).</param>
    /// <returns>The properties.</returns>
    public static IReadOnlyList<ContentPropertyFact> FromType(Type contentType, JsonSerializerOptions options, MeshWeaver.Messaging.Serialization.XmlDocsReader docs)
        => Parse(MeshWeaver.Data.ContentJsonSchema.Generate(contentType, options, docs).ToJsonString());

    /// <summary>
    /// Retired form of <see cref="FromType(Type, JsonSerializerOptions, MeshWeaver.Messaging.Serialization.XmlDocsReader)"/>
    /// without the mesh's reader: it parses the documentation files again on every call.
    /// </summary>
    /// <param name="contentType">The content type.</param>
    /// <param name="options">The serializer options its instances are written with.</param>
    /// <returns>The properties.</returns>
    [Obsolete("Pass the mesh's reader: FromType(type, options, hub.XmlDocs()).")]
    public static IReadOnlyList<ContentPropertyFact> FromType(Type contentType, JsonSerializerOptions options)
        => FromType(contentType, options, new MeshWeaver.Messaging.Serialization.XmlDocsReader());

    /// <summary>
    /// The content properties declared by <paramref name="jsonSchema"/>, in declaration order, first
    /// declaration winning when a union declares a name twice.
    /// </summary>
    /// <param name="jsonSchema">The schema document as text; null, empty, <c>{}</c> or malformed yields none.</param>
    /// <returns>The properties.</returns>
    public static IReadOnlyList<ContentPropertyFact> Parse(string? jsonSchema)
    {
        if (string.IsNullOrWhiteSpace(jsonSchema))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(jsonSchema);
            var result = new List<ContentPropertyFact>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Collect(doc.RootElement, result, seen, depth: 0);
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Collect(JsonElement schema, List<ContentPropertyFact> into, HashSet<string> seen, int depth)
    {
        if (schema.ValueKind != JsonValueKind.Object || depth > 3)
            return;
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
            {
                if (p.Name.StartsWith('$') || !seen.Add(p.Name))
                    continue;
                into.Add(ReadProperty(p.Name, p.Value));
            }
        }
        foreach (var union in new[] { "anyOf", "oneOf", "allOf" })
            if (schema.TryGetProperty(union, out var alts) && alts.ValueKind == JsonValueKind.Array)
                foreach (var alt in alts.EnumerateArray())
                    Collect(alt, into, seen, depth + 1);
    }

    private static ContentPropertyFact ReadProperty(string name, JsonElement p)
    {
        string? type = null;
        var enumValues = new List<string>();
        string? format = null;
        string? description = null;
        var refs = new List<string>();

        if (p.ValueKind == JsonValueKind.Object)
        {
            type = ReadType(p);
            ReadEnum(p, enumValues);
            // A nullable enum / union states its members one level down.
            foreach (var union in new[] { "anyOf", "oneOf" })
                if (p.TryGetProperty(union, out var alts) && alts.ValueKind == JsonValueKind.Array)
                    foreach (var alt in alts.EnumerateArray())
                    {
                        if (alt.ValueKind != JsonValueKind.Object) continue;
                        ReadEnum(alt, enumValues);
                        type ??= ReadType(alt);
                    }
            if (p.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String)
                format = f.GetString();
            if (p.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                description = d.GetString();
            if (p.TryGetProperty(MeshNodeExtension, out var mn) && mn.ValueKind == JsonValueKind.Array)
                foreach (var q in mn.EnumerateArray())
                    if (q.ValueKind == JsonValueKind.String && q.GetString() is { Length: > 0 } s)
                        refs.Add(s);
        }

        return new ContentPropertyFact(name, type, enumValues.Distinct(StringComparer.Ordinal).ToArray(),
            description, refs, format);
    }

    private static string? ReadType(JsonElement p)
    {
        if (!p.TryGetProperty("type", out var t))
            return null;
        if (t.ValueKind == JsonValueKind.String)
            return t.GetString();
        if (t.ValueKind == JsonValueKind.Array)
            foreach (var e in t.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s && s != "null")
                    return s;
        return null;
    }

    private static void ReadEnum(JsonElement p, List<string> into)
    {
        if (!p.TryGetProperty("enum", out var e) || e.ValueKind != JsonValueKind.Array)
            return;
        foreach (var v in e.EnumerateArray())
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                into.Add(s);
    }
}
