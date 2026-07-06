using System.Text.Json.Nodes;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// The one canonical JSON shape for a <see cref="ChunkPosition"/> — <c>{ "x", "y", "w", "h" }</c>, each a
/// fraction of the page in <c>[0, 1]</c> with a top-left origin. Shared by every surface (the pgvector
/// <c>bbox</c> column, the <c>search_chunks</c>/<c>get_chunk</c> tool envelopes, and the document viewer),
/// so the box a chunk is stored with is exactly the box the UI marks — no per-surface reformatting drift.
/// </summary>
public static class ChunkPositionJson
{
    /// <summary>Serializes a position to the canonical JSON string, or null when there is no position.</summary>
    public static string? Serialize(ChunkPosition? position) =>
        ToJsonObject(position)?.ToJsonString();

    /// <summary>Builds the canonical <c>{x,y,w,h}</c> node, or null when there is no position.</summary>
    public static JsonObject? ToJsonObject(ChunkPosition? position) =>
        position is null
            ? null
            : new JsonObject
            {
                ["x"] = position.X,
                ["y"] = position.Y,
                ["w"] = position.Width,
                ["h"] = position.Height,
            };

    /// <summary>
    /// Parses the canonical JSON back into a <see cref="ChunkPosition"/>, or null for null/empty input.
    /// Missing keys default to 0 (a degenerate but harmless box) — the JSONB is validated by Postgres on
    /// write and always written by <see cref="Serialize"/>, so this never has to defend against invalid JSON.
    /// </summary>
    public static ChunkPosition? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        if (JsonNode.Parse(json) is not JsonObject obj)
            return null;
        return new ChunkPosition(
            obj["x"]?.GetValue<double>() ?? 0,
            obj["y"]?.GetValue<double>() ?? 0,
            obj["w"]?.GetValue<double>() ?? 0,
            obj["h"]?.GetValue<double>() ?? 0);
    }
}
