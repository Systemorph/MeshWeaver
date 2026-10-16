using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MeshWeaver.Mesh;

namespace MeshWeaver.GitSync;

/// <summary>
/// Names the TOP-LEVEL members of a refused node file that <see cref="MeshNode"/> does not have —
/// the diagnosis half of GitSync's strict read (#6638). The read stays strict: a file carrying a
/// retired member (<c>isPersistent</c>, a top-level <c>requires</c>, …) still refuses the whole
/// snapshot. This only turns the serializer's exception into a sentence that names the file, the
/// field and what has to change — the FILE — so the refusal is actionable from the sync node alone.
/// The same rule is a build error in every node repo's validate lane
/// (<c>.github/scripts/check-retired-node-fields.py</c>), so a refusal here means a file reached
/// a repository without passing that gate.
/// </summary>
internal static class UnmappedNodeMembers
{
    /// <summary>The top-level member names of <paramref name="json"/> that are not JSON members of
    /// <see cref="MeshNode"/> under <paramref name="options"/>; empty when the text is not a JSON
    /// object or every member maps. <c>$type</c> is the serializer's own marker and never counts.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="options">The options the strict read used.</param>
    /// <returns>The unmapped top-level member names, in file order.</returns>
    public static ImmutableList<string> Find(string json, JsonSerializerOptions options)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = options.AllowTrailingCommas,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];
            var info = options.GetTypeInfo(typeof(MeshNode));
            if (info.Kind != JsonTypeInfoKind.Object)
                return [];
            var comparer = options.PropertyNameCaseInsensitive
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var members = info.Properties.Select(p => p.Name).ToImmutableHashSet(comparer);
            return document.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(name => name != "$type" && !members.Contains(name))
                .ToImmutableList();
        }
    }

    /// <summary>The refusal sentence for one file: the unmapped fields when there are any,
    /// otherwise the serializer's own message.</summary>
    /// <param name="path">The file's repository path.</param>
    /// <param name="content">The file's text.</param>
    /// <param name="error">What the strict read threw.</param>
    /// <param name="options">The options the strict read used.</param>
    /// <returns>One line naming the file and why it was refused.</returns>
    public static string DescribeRefusal(string path, string content, Exception error, JsonSerializerOptions options)
    {
        var unmapped = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? Find(content, options)
            : [];
        if (unmapped.IsEmpty)
            return $"Failed to parse '{path}': {error.Message} — snapshot refused.";
        var fields = string.Join(", ", unmapped.Select(n => $"'{n}'"));
        return $"Failed to parse '{path}': field {fields} is not a member of MeshNode (a retired or "
               + "misspelled field) — remove it from the file; the reader stays strict, so the "
               + $"snapshot is refused. ({error.Message})";
    }
}
