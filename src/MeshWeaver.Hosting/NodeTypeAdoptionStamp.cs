using System;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// The ONE reading of a NodeType record's adoption stamp (<see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>)
/// for the readers that turn the mesh's NodeType records into a reference set — the deployment
/// report's adopted-artifact inventory and the prebuilt-bundle retention pass.
///
/// <para>🚨 It tells THREE states apart, and the readers it replaced each folded two of them:</para>
/// <list type="bullet">
///   <item><b>No content at all</b> — a statically-registered NodeType that declares nothing
///     (<c>ControlLaneRecord</c>, <c>WebhookEvent</c>: a <c>new MeshNode(…) { NodeType = "NodeType",
///     HubConfiguration = … }</c> with no <see cref="NodeTypeDefinition"/>). It is code in the
///     image: nothing was compiled for it, so it adopts no framework. That is an ANSWER — null.</item>
///   <item><b>A readable <see cref="NodeTypeDefinition"/></b> — its stamp, or null when it never
///     compiled.</item>
///   <item><b>Content that is present but NOT readable as a definition</b> (an untyped
///     <see cref="JsonElement"/> whose <c>$type</c> nobody resolves, a foreign CLR type). The stamp
///     is UNKNOWN, so this THROWS: the reference set it would feed is incomplete, and a reference
///     set that silently drops a member lets retention delete a bundle someone adopted.</item>
/// </list>
///
/// <para>Measured 2026-10-06 on memex, memex-cloud and control: the report folded the first state
/// into the third (<c>ContentAs</c> answers null for both), threw
/// <c>NodeType adoption record ControlLaneRecord could not be read</c> on every report, and so
/// every instance reported <c>AdoptedFrameworkInventoryComplete=false</c> — which the control
/// instance's retention (<c>DeploymentPinnedReferences.ReportedBuildsOf</c>) refuses, so no
/// artifact was ever retained-or-deleted against a complete inventory. The retention pass folded
/// the third state into the first and dropped an unreadable record from its reference set without a
/// word.</para>
/// </summary>
public static class NodeTypeAdoptionStamp
{
    /// <summary>
    /// The framework identity <paramref name="node"/> records as adopted, or null when it records
    /// none (no content, or a definition that never compiled). Throws
    /// <see cref="InvalidOperationException"/> when the node carries content that cannot be read
    /// as a <see cref="NodeTypeDefinition"/> — the stamp is unknown, never absent.
    /// </summary>
    /// <param name="node">A record of node type <see cref="MeshNode.NodeTypePath"/>.</param>
    /// <param name="options">The reading hub's serializer options.</param>
    /// <param name="logger">Diagnostics for the content read.</param>
    public static string? CompiledFrameworkVersionOf(MeshNode node, JsonSerializerOptions options, ILogger? logger = null)
    {
        if (node.Content is null)
            return null;
        var definition = node.ContentAs<NodeTypeDefinition>(options, logger)
            ?? throw new InvalidOperationException(
                // The prefix is the established fingerprint (the swallow audit found this incident
                // by it) — a genuinely unreadable record must still match it.
                $"NodeType adoption record {node.Path} could not be read: it carries content of type "
                + $"{DescribeContent(node.Content)} that cannot be read as a NodeTypeDefinition — its adoption stamp is unknown");
        return string.IsNullOrWhiteSpace(definition.CompiledFrameworkVersion)
            ? null
            : definition.CompiledFrameworkVersion;
    }

    private static string DescribeContent(object content) =>
        content is JsonElement element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("$type", out var type)
            && type.ValueKind == JsonValueKind.String
                ? $"'{type.GetString()}' (untyped JSON)"
                : content.GetType().Name;
}
