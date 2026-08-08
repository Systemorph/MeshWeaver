namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The pure path arithmetic behind <see cref="NodeTypeGate"/> — no hub, no streams, no I/O, so the
/// rule that decides "is this path one of the gate's declared public surfaces" is unit-testable on
/// its own and reads identically wherever it is applied.
///
/// <para>Both entry points take the gated NODES as a <c>path → nodeType</c> map (the instances of
/// the gated types that currently exist) and resolve the NEAREST gated ancestor-or-self of the
/// target — longest matching path wins, so a plugin nested inside another plugin is decided by the
/// inner one.</para>
/// </summary>
public static class NodeTypeGateEvaluator
{
    /// <summary>
    /// Canonicalizes a declared surface: trims whitespace and slashes, collapses blank to
    /// <see cref="NodeTypeGate.Self"/>, and REFUSES anything containing a <c>..</c> traversal
    /// segment (returns <c>null</c>) — a gate must never be able to open a path outside the node
    /// it is anchored on.
    /// </summary>
    public static string? NormalizeSurface(string? surface)
    {
        var trimmed = (surface ?? string.Empty).Trim().Trim('/');
        if (trimmed.Length == 0 || trimmed == ".")
            return NodeTypeGate.Self;
        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (segment == "..")
                return null;
        return trimmed;
    }

    /// <summary>
    /// True when <paramref name="targetPath"/> is one of <paramref name="gate"/>'s declared public
    /// surfaces relative to <paramref name="gatedPath"/> — the surface node itself or anything
    /// beneath it. <see cref="NodeTypeGate.Self"/> matches ONLY <paramref name="gatedPath"/>
    /// exactly, never a descendant.
    /// </summary>
    public static bool IsPublicSurface(NodeTypeGate gate, string gatedPath, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (string.IsNullOrEmpty(gatedPath) || string.IsNullOrEmpty(targetPath))
            return false;

        foreach (var declared in gate.PublicSurfaces)
        {
            var surface = NormalizeSurface(declared);
            if (surface is null)
                continue;
            if (surface.Length == 0)
            {
                // The cover: the gated node itself, and deliberately nothing below it.
                if (string.Equals(targetPath, gatedPath, StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            var surfacePath = gatedPath + "/" + surface;
            if (string.Equals(targetPath, surfacePath, StringComparison.OrdinalIgnoreCase)
                || targetPath.StartsWith(surfacePath + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves <paramref name="gate"/>'s <see cref="NodeTypeGate.RedirectOnDenied"/> against the
    /// gated node's path — relative by default, absolute when the declaration starts with
    /// <c>'/'</c>. <c>null</c> when the gate declares none, or when the declaration is a traversal.
    /// The result carries no leading <c>'/'</c>, matching
    /// <c>PermissionEvaluator.GetRedirectOnDenied</c>'s contract.
    /// </summary>
    public static string? ResolveRedirect(NodeTypeGate gate, string gatedPath)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (string.IsNullOrWhiteSpace(gate.RedirectOnDenied) || string.IsNullOrEmpty(gatedPath))
            return null;

        var declared = gate.RedirectOnDenied!;
        if (declared.StartsWith('/'))
        {
            var absolute = declared.TrimStart('/');
            return absolute.Length == 0 ? null : absolute;
        }

        var relative = NormalizeSurface(declared);
        if (relative is null)
            return null;
        return relative.Length == 0 ? gatedPath : gatedPath + "/" + relative;
    }

    /// <summary>
    /// True when <paramref name="targetPath"/> sits on a declared public surface of the nearest
    /// gated ancestor-or-self — i.e. the framework must grant Read to EVERY reader, anonymous
    /// included. False whenever no gated ancestor exists, so a mesh that declares no gate is
    /// completely unaffected.
    /// </summary>
    public static bool IsAnonymouslyReadable(
        IReadOnlyList<NodeTypeGate> gates,
        IReadOnlyDictionary<string, string> gatedNodes,
        string targetPath)
        => Resolve(gates, gatedNodes, targetPath) is { } hit
           && IsPublicSurface(hit.Gate, hit.GatedPath, targetPath);

    /// <summary>
    /// The type-declared redirect target for <paramref name="targetPath"/> — the nearest gated
    /// ancestor-or-self's <see cref="NodeTypeGate.RedirectOnDenied"/>, resolved against that
    /// node's path. <c>null</c> when nothing applies.
    /// </summary>
    public static string? ResolveRedirect(
        IReadOnlyList<NodeTypeGate> gates,
        IReadOnlyDictionary<string, string> gatedNodes,
        string targetPath)
        => Resolve(gates, gatedNodes, targetPath) is { } hit
            ? ResolveRedirect(hit.Gate, hit.GatedPath)
            : null;

    /// <summary>
    /// The nearest gated ancestor-or-self of <paramref name="targetPath"/>: the LONGEST gated node
    /// path that the target equals or sits beneath, paired with its gate declaration.
    /// </summary>
    private static (NodeTypeGate Gate, string GatedPath)? Resolve(
        IReadOnlyList<NodeTypeGate> gates,
        IReadOnlyDictionary<string, string> gatedNodes,
        string targetPath)
    {
        if (gates is not { Count: > 0 } || gatedNodes is not { Count: > 0 }
            || string.IsNullOrEmpty(targetPath))
            return null;

        (NodeTypeGate Gate, string GatedPath)? best = null;
        foreach (var (gatedPath, nodeType) in gatedNodes)
        {
            if (string.IsNullOrEmpty(gatedPath))
                continue;
            if (!string.Equals(targetPath, gatedPath, StringComparison.OrdinalIgnoreCase)
                && !targetPath.StartsWith(gatedPath + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (best is { } current && current.GatedPath.Length >= gatedPath.Length)
                continue;   // a nearer (longer) gate already won

            foreach (var gate in gates)
            {
                if (!string.Equals(gate.NodeType, nodeType, StringComparison.OrdinalIgnoreCase))
                    continue;
                best = (gate, gatedPath);
                break;
            }
        }

        return best;
    }
}
