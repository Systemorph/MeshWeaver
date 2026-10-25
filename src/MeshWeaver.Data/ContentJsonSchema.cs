using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Utils;

namespace MeshWeaver.Data;

/// <summary>
/// The JSON Schema of a content type, as the mesh publishes it (<c>@{node}/schema/</c>): System.Text.Json's
/// schema for the type under the hub's serializer options, with the type's and each property's XML doc as
/// <c>description</c>, and a <c>[MeshNode("…")]</c> reference's queries as the <see cref="MeshNodeExtension"/>.
/// One generator for every reader — the schema read and the query autocomplete must never disagree.
/// </summary>
public static class ContentJsonSchema
{
    /// <summary>
    /// The extension keyword carrying a <c>[MeshNode("…")]</c> property's queries
    /// (<c>"x-meshNode": ["nodeType:Crm/Offering"]</c>) — WHICH nodes are its allowed values, so a reader
    /// with no CLR type (query autocomplete, an agent writing content) can resolve the members.
    /// </summary>
    public const string MeshNodeExtension = "x-meshNode";

    /// <summary>
    /// Retired form: it has no mesh reader, so it parses the documentation files again on every
    /// call. Use <see cref="Generate(Type, JsonSerializerOptions, XmlDocsReader)"/> with <c>hub.XmlDocs()</c>.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="options">The serializer options its instances are written with (names, enums, polymorphism).</param>
    /// <returns>The schema document.</returns>
    [Obsolete("Pass the mesh's reader: Generate(type, options, hub.XmlDocs()).")]
    public static JsonNode Generate(Type type, JsonSerializerOptions options)
        => Generate(type, options, new XmlDocsReader());

    /// <summary>The schema of <paramref name="type"/>.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="options">The serializer options its instances are written with (names, enums, polymorphism).</param>
    /// <param name="docs">The mesh's XML-docs reader (<c>hub.XmlDocs()</c>) the descriptions come from.</param>
    /// <returns>The schema document.</returns>
    public static JsonNode Generate(Type type, JsonSerializerOptions options, XmlDocsReader docs)
        => options.GetJsonSchemaAsNode(type, new()
        {
            TransformSchemaNode = (ctx, node) =>
            {
                if (ctx.TypeInfo.Type == type)
                {
                    node["title"] = type.Name;
                    var typeDescription = docs.Summary(type);
                    if (!string.IsNullOrEmpty(typeDescription))
                        node["description"] = typeDescription;
                }

                if (ctx.PropertyInfo != null && node is JsonObject jsonObj)
                {
                    var actualPropertyInfo = ctx.PropertyInfo.DeclaringType.GetProperty(ctx.PropertyInfo.Name.ToPascalCase()!);
                    if (actualPropertyInfo != null)
                    {
                        var propertyDescription = docs.Summary(actualPropertyInfo);
                        if (!string.IsNullOrEmpty(propertyDescription))
                            jsonObj["description"] = propertyDescription;

                        if (actualPropertyInfo.GetCustomAttribute<MeshNodeAttribute>() is { Queries.Length: > 0 } meshNode)
                        {
                            var queries = new JsonArray();
                            foreach (var q in meshNode.Queries)
                                queries.Add(q);
                            jsonObj[MeshNodeExtension] = queries;
                        }
                    }
                }

                return node;
            }
        });
}
