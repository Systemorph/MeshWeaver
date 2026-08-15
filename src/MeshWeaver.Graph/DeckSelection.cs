using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// The pure (IO-free) deck slide-selection core — a deck's order is defined in exactly ONE
/// place, shared by every consumer that must agree on it: <see cref="DeckSlidesCache"/> (the live
/// per-slide navigation streams), and the PDF/HTML export templates (runtime-compiled kernel
/// scripts). Formerly hosted on the retired core <c>DeckLayoutAreas</c>; the LIVE deck views now
/// ship in the Publish pack's in-mesh source, which carries its own copy of this logic
/// (<c>Publish/Deck/Source/DeckLayoutAreas.cs</c>) — keep the two in agreement.
/// </summary>
public static class DeckSelection
{
    /// <summary>
    /// Resolves a manifest reference to a full slide path. A reference is either a bare id
    /// (relative to the deck — <c>"intro"</c> → <c>"{deck}/intro"</c>, kept for backward
    /// compatibility) or an ABSOLUTE path to a slide anywhere in the mesh (any reference
    /// containing a <c>/</c> is treated as an absolute path and returned as-is).
    /// </summary>
    public static string ResolveSlidePath(string deckPath, string slideRef)
    {
        var trimmed = slideRef.Trim().TrimStart('/');
        if (trimmed.Length == 0)
            return deckPath;
        // A reference that names a path (contains '/') points at a slide ANYWHERE — presentation
        // is decoupled from the deck's children. A bare id is a child under the deck.
        return trimmed.Contains('/') ? trimmed : $"{deckPath}/{trimmed}";
    }

    /// <summary>
    /// Resolves a deck node's slide SELECTION. Returns either the explicit ordered manifest
    /// (<see cref="DeckContent.Slides"/>) resolved to full paths — in which case <c>Query</c> is
    /// <c>null</c> — or, when the manifest is empty, the live query to run
    /// (<see cref="DeckContent.Query"/>, else the default subtree of Slide nodes under the deck)
    /// with <c>Paths</c> empty.
    /// </summary>
    /// <param name="deckNode">The deck node whose <see cref="DeckContent"/> declares the selection.</param>
    /// <param name="deckPath">The deck's mesh path, used to resolve bare-id references and the default query.</param>
    /// <param name="options">JSON options used to read <see cref="DeckContent"/> off the node.</param>
    /// <returns>The ordered explicit slide paths, or the query to run when the manifest is empty —
    /// with <c>FilterSlideTypes</c> set when the consumer must additionally keep only
    /// <see cref="SlideNodeType.Matches">slide-typed</see> results.</returns>
    public static (ImmutableList<string> Paths, string? Query, bool FilterSlideTypes) ResolveDeckSelection(
        MeshNode? deckNode, string deckPath, JsonSerializerOptions options)
    {
        var deck = deckNode.ContentAs<DeckContent>(options);
        var paths = (deck?.Slides ?? ImmutableList<string>.Empty)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => ResolveSlidePath(deckPath, r))
            .ToImmutableList();
        // Empty manifest → a live query: the deck's custom Query, else the DEFAULT subtree of
        // slide nodes (a deck can be just "a folder of slides"; only slide nodes count).
        // 🚨 The default deliberately carries NO `nodeType:` term: that filter is EQUALITY, and a
        // plugin slide type's identity is its install path (`Publish/Slide`), so `nodeType:Slide`
        // silently dropped every plugin-typed slide from the deck. Instead FilterSlideTypes tells
        // the consumer to keep only SlideNodeType.Matches results — suffix-aware, and applied only
        // to the DEFAULT selection: a custom DeckContent.Query keeps full authority over what
        // counts as a slide (it can carry its own nodeType term, or none).
        var useDefaultQuery = paths.Count == 0 && string.IsNullOrWhiteSpace(deck?.Query);
        var query = paths.Count > 0
            ? null
            : useDefaultQuery
                ? $"path:{deckPath} scope:subtree"
                : deck!.Query!.Trim();
        return (paths, query, useDefaultQuery);
    }
}
