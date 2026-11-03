using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.Layout;

/// <summary>One content type handed to <see cref="UmlSchemaProjection.FromContentSchemas"/>.</summary>
/// <param name="TypePath">The NodeType's path (<c>Reinsurance/Acceptance</c>) — the class's id.</param>
/// <param name="SchemaJson">The content type's JSON schema, as <c>@{type}/schema/</c> answers it.</param>
public record UmlSchemaInput(string TypePath, string? SchemaJson)
{
    /// <summary>The class name; defaults to the schema's title, else the path's last segment.</summary>
    public string? Name { get; init; }

    /// <summary>Where the class box links; null leaves it to the projection's <c>hrefOf</c>.</summary>
    public string? Href { get; init; }

    /// <summary>A <see cref="UmlClassKind"/> for this class (e.g. featured). Optional.</summary>
    public string? Kind { get; init; }
}

/// <summary>
/// Projects content-type JSON schemas — the ones <c>@{type}/schema/</c> publishes — onto a
/// <see cref="UmlClassDiagramControl"/>'s classes and relations. Pure.
/// <list type="bullet">
///   <item>Each input is a class; its properties (except the <c>$type</c> discriminator) are attributes
///     <c>name : Type [multiplicity]</c> — an array is <c>*</c>, a nullable value <c>0..1</c>.</item>
///   <item>A property carrying the <c>x-meshNode</c> extension references other nodes; each
///     <c>nodeType:{Path}</c> in its queries becomes an ASSOCIATION to that type, labelled with the
///     property's name, the target multiplicity <c>*</c> for an array, <c>0..1</c> for a nullable
///     reference, else <c>1</c>; the attribute's type is the target's name.</item>
///   <item>A referenced type that is not among the inputs becomes a <see cref="UmlClassKind.Foreign"/>
///     class with its package (the path's first segment) — so cross-package references show.</item>
///   <item>A schema that does not parse contributes a class with no attributes, never an exception.</item>
/// </list>
/// </summary>
public static class UmlSchemaProjection
{
    /// <summary>The schema extension carrying a reference property's node queries (<c>ContentJsonSchema.MeshNodeExtension</c>).</summary>
    public const string MeshNodeExtension = "x-meshNode";

    private const string NodeTypeToken = "nodeType:";

    /// <summary>Projects the schemas.</summary>
    /// <param name="inputs">The content types.</param>
    /// <param name="hrefOf">The link for a type path (the explorer's type page); null leaves boxes unlinked.</param>
    /// <returns>The classes and relations to draw.</returns>
    public static (ImmutableList<UmlClass> Classes, ImmutableList<UmlRelation> Relations) FromContentSchemas(
        IEnumerable<UmlSchemaInput> inputs, Func<string, string?>? hrefOf = null)
    {
        var classes = ImmutableList.CreateBuilder<UmlClass>();
        var relations = ImmutableList.CreateBuilder<UmlRelation>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = (inputs ?? []).Where(i => i is not null && !string.IsNullOrWhiteSpace(i.TypePath)).ToList();
        // The class id a target resolves to: the input's own spelling when it is one, else the FIRST
        // spelling it was referenced by — so differently cased references to one type are one class
        // and the layout (which matches ids exactly) never loses a relation.
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in list)
        {
            known.Add(input.TypePath);
            canonical.TryAdd(input.TypePath, input.TypePath);
        }
        var foreign = new Dictionary<string, UmlClass>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in list)
        {
            var (title, description, properties) = Read(input.SchemaJson);
            var attributes = ImmutableList.CreateBuilder<UmlAttribute>();
            foreach (var (name, property) in properties)
            {
                var (type, many, nullable) = TypeOf(property);
                var targets = Targets(property).Select(t => canonical.TryGetValue(t, out var id) ? id : canonical[t] = t)
                    .Distinct(StringComparer.Ordinal).ToImmutableList();
                var multiplicity = many ? "*" : nullable ? "0..1" : null;
                attributes.Add(new UmlAttribute(name, targets.Count > 0 ? string.Join(" | ", targets.Select(Leaf)) : type, multiplicity)
                {
                    IsReference = targets.Count > 0,
                    // A property schema may legally be a boolean (`true`): it has no description.
                    Description = property.ValueKind == JsonValueKind.Object
                                  && property.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() : null,
                });
                foreach (var target in targets)
                {
                    relations.Add(new UmlRelation(input.TypePath, target)
                    {
                        Role = name,
                        ToMultiplicity = many ? "*" : nullable ? "0..1" : "1",
                    });
                    if (!known.Contains(target) && !foreign.ContainsKey(target))
                        foreign[target] = new UmlClass(target, Leaf(target))
                        {
                            Kind = UmlClassKind.Foreign,
                            Package = Package(target),
                            Href = hrefOf?.Invoke(target),
                        };
                }
            }

            classes.Add(new UmlClass(input.TypePath, input.Name ?? title ?? Leaf(input.TypePath))
            {
                Kind = input.Kind,
                Href = input.Href ?? hrefOf?.Invoke(input.TypePath),
                Description = description,
                Attributes = attributes.ToImmutable(),
            });
        }

        classes.AddRange(foreign.Values.OrderBy(c => c.Id, StringComparer.Ordinal));
        return (classes.ToImmutable(), relations.ToImmutable());
    }

    /// <summary>The last segment of a path — a type's display name.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The leaf.</returns>
    public static string Leaf(string path) => path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..];

    /// <summary>The first segment of a path — the package (plugin) a type belongs to.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The package.</returns>
    public static string Package(string path)
    {
        var trimmed = path.Trim('/');
        var i = trimmed.IndexOf('/');
        return i < 0 ? trimmed : trimmed[..i];
    }

    /// <summary>The NodeType paths a reference property's <c>x-meshNode</c> queries name, in order, distinct.</summary>
    /// <param name="property">The property schema.</param>
    /// <returns>The target paths; empty for a plain property.</returns>
    public static ImmutableList<string> Targets(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object
            || !property.TryGetProperty(MeshNodeExtension, out var queries)
            || queries.ValueKind != JsonValueKind.Array)
            return [];
        var targets = new List<string>();
        foreach (var query in queries.EnumerateArray())
        {
            if (query.ValueKind != JsonValueKind.String) continue;
            foreach (var path in NodeTypeValues(query.GetString() ?? string.Empty))
                if (!targets.Contains(path, StringComparer.OrdinalIgnoreCase))
                    targets.Add(path);
        }
        return [.. targets];
    }

    /// <summary>
    /// The POSITIVE <c>nodeType:</c> values of a query, in order: <c>nodeType:A</c>, a membership group
    /// <c>nodeType:(A OR B)</c> / <c>nodeType:(A, B)</c>, and a pipe list <c>nodeType:A|B</c>. A negated
    /// term (<c>-nodeType:X</c>) names no target.
    /// </summary>
    /// <param name="query">The query text.</param>
    /// <returns>The node type paths.</returns>
    public static IEnumerable<string> NodeTypeValues(string query)
    {
        var i = 0;
        while ((i = query.IndexOf(NodeTypeToken, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var negated = i > 0 && (query[i - 1] == '-' || query[i - 1] == '!');
            var boundary = i == 0 || char.IsWhiteSpace(query[i - 1]) || query[i - 1] == '(' || negated;
            var start = i + NodeTypeToken.Length;
            string value;
            if (start < query.Length && query[start] == '(')
            {
                var close = query.IndexOf(')', start);
                value = close < 0 ? query[(start + 1)..] : query[(start + 1)..close];
                i = close < 0 ? query.Length : close + 1;
            }
            else
            {
                var end = start;
                while (end < query.Length && !char.IsWhiteSpace(query[end]) && query[end] != ')') end++;
                value = query[start..end];
                i = end;
            }
            if (!boundary || negated)
                continue;
            foreach (var part in value.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries)
                         .SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                         .Where(p => !string.Equals(p, "OR", StringComparison.OrdinalIgnoreCase)))
            {
                var path = part.Trim('"', '\'', '/');
                if (path.Length > 0)
                    yield return path;
            }
        }
    }

    private static (string? Title, string? Description, List<(string Name, JsonElement Property)> Properties) Read(string? json)
    {
        var properties = new List<(string, JsonElement)>();
        if (string.IsNullOrWhiteSpace(json))
            return (null, null, properties);
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (null, null, properties);
        }
        if (root.ValueKind != JsonValueKind.Object)
            return (null, null, properties);

        string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var title = Str(root, "title");
        var description = Str(root, "description");
        // A polymorphic content type is published as anyOf over its concrete shapes: the first that
        // carries properties is the type's own.
        var holder = root.TryGetProperty("properties", out _) ? root : default;
        if (holder.ValueKind == JsonValueKind.Undefined && root.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
            foreach (var shape in anyOf.EnumerateArray())
                if (shape.ValueKind == JsonValueKind.Object && shape.TryGetProperty("properties", out _))
                {
                    holder = shape;
                    title ??= Str(shape, "title");
                    description ??= Str(shape, "description");
                    break;
                }
        if (holder.ValueKind == JsonValueKind.Object
            && holder.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var p in props.EnumerateObject())
                if (p.Name != "$type")
                    properties.Add((p.Name, p.Value));
        return (title, description, properties);
    }

    private static (string Type, bool Many, bool Nullable) TypeOf(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object)
            return ("object", false, false);
        var types = new List<string>();
        if (property.TryGetProperty("type", out var t))
        {
            if (t.ValueKind == JsonValueKind.String) types.Add(t.GetString()!);
            else if (t.ValueKind == JsonValueKind.Array)
                types.AddRange(t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        }
        var nullable = types.Remove("null");
        var primary = types.FirstOrDefault() ?? (property.TryGetProperty("enum", out _) ? "enum" : "object");
        if (primary == "array")
        {
            var item = property.TryGetProperty("items", out var items) ? TypeOf(items).Type : "object";
            return (item, true, nullable);
        }
        if (primary == "string" && property.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String)
            primary = f.GetString() switch
            {
                "date-time" => "DateTime",
                "date" => "Date",
                _ => "string",
            };
        if (primary == "object" && property.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            primary = title.GetString()!;
        return (primary, false, nullable);
    }
}
