using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using MeshWeaver.Domain;
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

    /// <summary>The schema of <paramref name="type"/>.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="options">The serializer options its instances are written with (names, enums, polymorphism).</param>
    /// <returns>The schema document.</returns>
    public static JsonNode Generate(Type type, JsonSerializerOptions options)
        => options.GetJsonSchemaAsNode(type, new()
        {
            TransformSchemaNode = (ctx, node) =>
            {
                if (ctx.TypeInfo.Type == type)
                {
                    node["title"] = type.Name;
                    var typeDescription = MeshWeaver.Messaging.Serialization.XmlDocs.Summary(type);
                    if (!string.IsNullOrEmpty(typeDescription))
                        node["description"] = typeDescription;
                }

                if (ctx.PropertyInfo != null && node is JsonObject jsonObj)
                {
                    var actualPropertyInfo = ctx.PropertyInfo.DeclaringType.GetProperty(ctx.PropertyInfo.Name.ToPascalCase()!);
                    if (actualPropertyInfo != null)
                    {
                        var propertyDescription = MeshWeaver.Messaging.Serialization.XmlDocs.Summary(actualPropertyInfo);
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
