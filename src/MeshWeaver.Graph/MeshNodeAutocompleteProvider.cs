using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;

namespace MeshWeaver.Graph;

/// <summary>
/// Generic autocomplete provider that queries child nodes from the mesh query service.
/// Uses the hub's address as the parent path to find children.
/// Returns relative paths when contextPath matches the hub address, absolute otherwise.
/// </summary>
internal class MeshNodeAutocompleteProvider(
    IMeshService meshQuery,
    IMessageHub hub,
    IAutocompletePrefixRegistry? prefixRegistry = null) : IAutocompleteProvider
{
    private const int DefaultMaxResults = 20;

    /// <inheritdoc />
    public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null) =>
        AutocompleteProviderObservable.FromAsyncEnumerable(ct => EnumerateAsync(query, contextPath, ct));

    private async IAsyncEnumerable<AutocompleteItem> EnumerateAsync(
        string query,
        string? contextPath,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Skip UCR prefix queries — handled by dedicated providers (Content, Data, etc.)
        if (StartsWithUcrPrefix(query))
            yield break;

        // Use the hub's address as the parent path
        var parentPath = hub.Address.ToString();

        var queryString = $"namespace:{parentPath}";
        if (!string.IsNullOrWhiteSpace(query))
            queryString += $" name:{query}";

        // Query for child nodes and yield results
        var count = 0;
        var stream = meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(queryString))
            .Take(1)
            .SelectMany(c => c.Items.ToObservable())
            .ToAsyncEnumerableSequence(ct);
        await foreach (var node in stream.WithCancellation(ct))
        {
            if (count >= DefaultMaxResults) break;
            count++;

            // Use relative name when context matches, absolute path otherwise
            var insertText = $"@{node.Path}/";
            var priority = 1000 - (node.Order ?? 0);

            if (!string.IsNullOrEmpty(contextPath))
            {
                if (node.Path.StartsWith(contextPath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    var relativeName = node.Path[(contextPath.Length + 1)..];
                    insertText = $"@{relativeName}/";
                    // Direct child or descendant of context: high priority
                    priority += relativeName.Contains('/') ? 1500 : 2000;
                }
                else
                {
                    // Check for sibling (same parent)
                    var contextParent = contextPath.LastIndexOf('/');
                    if (contextParent > 0)
                    {
                        var parent = contextPath[..contextParent];
                        if (node.Path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))
                            priority += 1000; // sibling
                    }
                }
            }

            // Prefer shorter paths
            var segmentCount = node.Path.Count(c => c == '/') + 1;
            priority -= segmentCount * 50;

            yield return new AutocompleteItem(
                Label: $"@{node.Path}/",
                InsertText: insertText,
                Description: node.Name ?? node.NodeType,
                Category: node.NodeType ?? "Nodes",
                Priority: priority,
                Kind: AutocompleteKind.Other
            );
        }
    }

    private bool StartsWithUcrPrefix(string? query)
    {
        if (string.IsNullOrEmpty(query) || prefixRegistry == null) return false;
        var p = query.StartsWith("@") ? query[1..] : query;
        if (p.StartsWith("/")) p = p[1..];
        var firstSlash = p.IndexOf('/');
        var firstSegment = firstSlash > 0 ? p[..firstSlash] : p;
        return prefixRegistry.IsRegistered(firstSegment);
    }
}
