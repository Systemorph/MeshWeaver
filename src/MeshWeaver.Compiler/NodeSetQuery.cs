using MeshWeaver.Mesh;

namespace MeshWeaver.Compiler;

/// <summary>
/// The MESH-FREE evaluator for the (small, closed) query language a NodeType's source discovery
/// actually speaks — the second half of issue #1763's source resolution.
///
/// <para><b>It is deliberately not a general query engine.</b> The mesh evaluates
/// <c>ParsedQuery</c> through <c>QueryParser</c> + <c>QueryEvaluator</c> + the storage adapters;
/// re-implementing that surface offline would be a large, silently-diverging copy. What
/// <see cref="CodeQueryResolver.Expand"/> can EMIT, by contrast, is four selectors wide —
/// <c>path:</c>, <c>namespace:</c>, <c>scope:</c> and <c>nodeType:</c> — and those semantics are
/// reproduced here EXACTLY as the runtime derives them:</para>
/// <list type="bullet">
///   <item><c>path:X</c> with no scope ⇒ <c>QueryScope.Exact</c> — the node AT X.</item>
///   <item><c>namespace:X</c> with no scope ⇒ <c>QueryScope.Children</c> — nodes whose
///     <c>Namespace</c> IS X (not the subtree).</item>
///   <item><c>namespace:X scope:subtree</c> ⇒ DEGRADES to <c>Descendants</c>: a namespace names a
///     namespace, never the node at X, so self is excluded. <c>path:X scope:subtree</c> keeps self.
///     (<c>QueryParser.ExtractReservedQualifiers</c> — the degradation is load-bearing: the default
///     source query is <c>namespace:{Type}/Source scope:subtree</c>, which must NOT pull in a node
///     literally at <c>{Type}/Source</c>.)</item>
///   <item>Every comparison — path, namespace and <c>nodeType</c> — is
///     <see cref="StringComparison.OrdinalIgnoreCase"/>, matching <c>MatchesPathValue</c> and
///     <c>QueryEvaluator</c>'s scalar equality.</item>
/// </list>
///
/// <para>🚨 <b>Anything outside that grammar is REFUSED, never approximated.</b> Free text (which
/// routes to vector search on a real mesh), wildcards, alternations, comparison operators, extra
/// selectors and <c>limit:</c> all make <see cref="TryParse"/> fail with a reason. The caller then
/// reports the source set UNESTABLISHED and refuses to compile — the same direction
/// <see cref="SourceSnapshot"/> takes at runtime, because a source set that is SHORT compiles into
/// completely genuine-looking CS0246/CS0103 diagnostics about code that is fine (#1218), and a
/// build produced from one is adopted by every portal without a murmur.</para>
/// </summary>
public static class NodeSetQuery
{
    /// <summary>The path/scope + nodeType constraint one expanded query imposes on a node.</summary>
    public sealed class Predicate
    {
        internal Predicate(string? path, Scope scope, string? nodeType)
        {
            this.path = path;
            this.scope = scope;
            this.nodeType = nodeType;
        }

        private readonly string? path;
        private readonly Scope scope;
        private readonly string? nodeType;

        /// <summary>True when <paramref name="node"/> satisfies every constraint.</summary>
        public bool Matches(MeshNode node)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (nodeType is not null
                && !string.Equals(node.NodeType, nodeType, StringComparison.OrdinalIgnoreCase))
                return false;
            if (path is null)
                return true;
            var nodePath = node.Path;
            var nodeNamespace = node.Namespace ?? string.Empty;
            if (path.Length == 0)
                return scope switch
                {
                    Scope.Children => nodeNamespace.Length == 0,
                    Scope.Exact => false,
                    _ => true,
                };
            return scope switch
            {
                Scope.Exact => string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase),
                Scope.Children => string.Equals(nodeNamespace, path, StringComparison.OrdinalIgnoreCase),
                Scope.Descendants => nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),
                Scope.Subtree => string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                                 || nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }
    }

    /// <summary>The scope walks a source query can express (the runtime's <c>QueryScope</c> subset
    /// reachable from <see cref="CodeQueryResolver.Expand"/> and hand-authored source queries).</summary>
    internal enum Scope
    {
        /// <summary>The node at the path.</summary>
        Exact,

        /// <summary>Nodes whose namespace IS the path.</summary>
        Children,

        /// <summary>Nodes strictly below the path.</summary>
        Descendants,

        /// <summary>The node at the path and everything below it.</summary>
        Subtree,
    }

    /// <summary>
    /// Parses one EXPANDED source query into a <see cref="Predicate"/>.
    /// Returns false — with a human-readable <paramref name="reason"/> naming what it could not
    /// evaluate — for anything outside the supported grammar. Never guesses.
    /// </summary>
    public static bool TryParse(string query, out Predicate predicate, out string reason)
    {
        predicate = new Predicate(null, Scope.Exact, null);
        reason = string.Empty;

        string? path = null;
        string? nodeType = null;
        var scope = Scope.Exact;
        var explicitScope = false;
        var namespaceUsed = false;
        var pathUsed = false;

        foreach (var token in (query ?? string.Empty)
                     .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0)
            {
                reason = $"free-text term '{token}' — on a mesh this routes to the vector index, "
                         + "which a build process cannot reproduce";
                return false;
            }
            var selector = token[..colon];
            var value = token[(colon + 1)..];
            if (value.Contains('|', StringComparison.Ordinal)
                || value.Contains('*', StringComparison.Ordinal))
            {
                reason = $"'{token}' uses an alternation/wildcard value, which this evaluator "
                         + "does not implement";
                return false;
            }
            if (!selector.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                reason = $"'{token}' carries a comparison operator; only plain 'selector:value' "
                         + "equality is supported";
                return false;
            }

            if (selector.Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                if (namespaceUsed || pathUsed)
                {
                    reason = $"'{query}' names more than one path/namespace; the runtime resolves "
                             + "that by last-token-wins, which is too subtle to reproduce blind";
                    return false;
                }
                path = value;
                pathUsed = true;
                continue;
            }
            if (selector.Equals("namespace", StringComparison.OrdinalIgnoreCase))
            {
                if (namespaceUsed || pathUsed)
                {
                    reason = $"'{query}' names more than one path/namespace; the runtime resolves "
                             + "that by last-token-wins, which is too subtle to reproduce blind";
                    return false;
                }
                path = value;
                namespaceUsed = true;
                continue;
            }
            if (selector.Equals("scope", StringComparison.OrdinalIgnoreCase))
            {
                explicitScope = true;
                switch (value.ToLowerInvariant())
                {
                    case "exact": scope = Scope.Exact; break;
                    case "children": scope = Scope.Children; break;
                    case "descendants": scope = Scope.Descendants; break;
                    case "subtree" or "selfanddescendants": scope = Scope.Subtree; break;
                    default:
                        reason = $"scope '{value}' is not one of exact/children/descendants/subtree";
                        return false;
                }
                continue;
            }
            if (selector.Equals("nodeType", StringComparison.OrdinalIgnoreCase))
            {
                if (nodeType is not null && !string.Equals(nodeType, value, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"'{query}' constrains nodeType twice ('{nodeType}' and '{value}')";
                    return false;
                }
                nodeType = value;
                continue;
            }

            reason = $"selector '{selector}' is not one of path/namespace/scope/nodeType — a "
                     + "build-process bake refuses to guess what the mesh would have matched";
            return false;
        }

        // The runtime's two namespace rules, verbatim (QueryParser.ExtractReservedQualifiers):
        // namespace: without an explicit scope means CHILDREN, and namespace: + subtree DEGRADES
        // to descendants so the node AT the namespace never leaks into its own result set.
        if (namespaceUsed && !explicitScope)
            scope = Scope.Children;
        if (namespaceUsed && scope == Scope.Subtree)
            scope = Scope.Descendants;

        predicate = new Predicate(path, scope, nodeType);
        return true;
    }
}
