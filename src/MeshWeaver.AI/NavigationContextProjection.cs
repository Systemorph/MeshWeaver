using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// The serializable navigation REFERENCE carried from the composer to the agent round alongside
/// the main-node path (<c>ContextPath</c>): the layout <see cref="Area"/> / <see cref="AreaId"/>
/// and the optional query parameters as key/value pairs. JSON-serialized with the mesh's standard
/// options. The address is NOT here — it travels as the plain main-node path. The agent loads node
/// CONTENT on demand via its Get tool; this is reference only.
/// </summary>
public sealed record NavigationReference
{
    /// <summary>The layout area name the user is viewing (e.g. <c>Overview</c>, <c>VersionDiff</c>).</summary>
    public string? Area { get; init; }

    /// <summary>The layout area id, when the area is parameterized.</summary>
    public string? AreaId { get; init; }

    /// <summary>The optional query parameters as key/value pairs (the <c>?k=v&amp;…</c> on the URL).</summary>
    public ImmutableDictionary<string, string>? Parameters { get; init; }
}

/// <summary>
/// Projects the page the user is currently viewing (<see cref="NavigationContext"/>) into the
/// <see cref="AgentContext"/> shipped to the agent.
///
/// <para>🎯 ALWAYS resolves to the MAIN NODE — a satellite (<c>_Thread</c>/<c>_Comment</c>/
/// <c>_Activity</c>/<c>_Approval</c>/…) is mapped to its OWNER node, never shipped as the
/// context. When the user starts a chat while viewing another thread or an activity, the chat's
/// context must be that satellite's owner, not the satellite itself.</para>
///
/// <para>Captures the full navigation reference: the owner address, the layout <see cref="Area"/>,
/// and the optional query parameters as key/value pairs (<see cref="NavigationContext.Args"/>).
/// This is a REFERENCE — the agent loads node CONTENT on demand via its <c>Get</c> tool. We never
/// inline content (token cost, staleness, and the &gt;30 s eager-load hang that removed the old
/// child-enumeration from the system prompt).</para>
/// </summary>
public static class NavigationContextProjection
{
    /// <summary>
    /// Builds the agent context from the current navigation context, resolving the owner
    /// (main) node, the area, and the query parameters. Returns an empty context when
    /// <paramref name="ctx"/> is <see langword="null"/> or has no resolvable main node.
    /// </summary>
    public static AgentContext ToAgentContext(NavigationContext? ctx)
    {
        if (ctx is null)
            return new AgentContext();

        var mainNode = ResolveMainNode(ctx);
        if (string.IsNullOrEmpty(mainNode))
            return new AgentContext();

        var area = string.IsNullOrWhiteSpace(ctx.Area) ? null : ctx.Area;
        var parameters = ctx.Args is { Count: > 0 } ? ctx.Args : null;

        return new AgentContext
        {
            Address = new Address(mainNode),
            Context = mainNode,
            LayoutArea = area is null ? null : new LayoutAreaReference(area) { Id = ctx.Id },
            Parameters = parameters,
            // Reference only — the node identity (path/type/name); content is loaded via Get.
            Node = ctx.Node,
        };
    }

    /// <summary>
    /// Extracts the serializable navigation reference (area + params) from the current context,
    /// or <see langword="null"/> when there is neither an area nor any parameters to carry.
    /// The main-node address is carried separately as the plain context path.
    /// </summary>
    public static NavigationReference? ToReference(NavigationContext? ctx)
    {
        if (ctx is null)
            return null;

        var area = string.IsNullOrWhiteSpace(ctx.Area) ? null : ctx.Area;
        var parameters = ctx.Args is { Count: > 0 } ? ctx.Args : null;

        if (area is null && parameters is null)
            return null;

        return new NavigationReference
        {
            Area = area,
            AreaId = area is null ? null : ctx.Id,
            Parameters = parameters,
        };
    }

    /// <summary>
    /// Builds the <see cref="AgentContext"/> from the resolved main-node path plus a carried
    /// <see cref="NavigationReference"/> (area + params). Used server-side at round execution where
    /// the main node is already resolved (the context path) and the node has been loaded.
    /// </summary>
    public static AgentContext ToAgentContext(string? mainNodePath, NavigationReference? reference, MeshNode? node)
    {
        if (string.IsNullOrEmpty(mainNodePath))
            return new AgentContext { Node = node };

        return new AgentContext
        {
            Address = new Address(mainNodePath),
            Context = mainNodePath,
            LayoutArea = string.IsNullOrEmpty(reference?.Area)
                ? null
                : new LayoutAreaReference(reference.Area) { Id = reference.AreaId },
            Parameters = reference?.Parameters,
            Node = node,
        };
    }

    /// <summary>
    /// The OWNER (main) node of the current navigation target. Prefers the loaded node's
    /// authoritative <see cref="MeshWeaver.Mesh.MeshNode.MainNode"/>; falls back to stripping
    /// satellite segments off the resolved namespace when the node hasn't loaded yet.
    /// </summary>
    public static string ResolveMainNode(NavigationContext ctx)
    {
        // Authoritative: a loaded node carries its owner explicitly (a thread ABOUT another
        // node points its MainNode there, which the string-strip below could not recover).
        if (ctx.Node is { MainNode: { Length: > 0 } mainNode })
            return StripSatelliteSegments(mainNode);

        // Fallback (node not yet loaded): strip satellite segments off the resolved namespace.
        return StripSatelliteSegments(ctx.Namespace);
    }

    /// <summary>
    /// Returns everything before the first satellite segment (a path segment starting with
    /// <c>_</c>, e.g. <c>_Thread</c>/<c>_Comment</c>/<c>_Activity</c>). Returns the path
    /// unchanged when it has no satellite segment.
    /// </summary>
    public static string StripSatelliteSegments(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
            if (segments[i].StartsWith('_'))
                return string.Join('/', segments, 0, i);

        return path;
    }
}
