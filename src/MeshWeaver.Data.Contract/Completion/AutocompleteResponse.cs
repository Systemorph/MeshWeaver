#nullable enable

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Response containing autocomplete suggestions.
/// <para>
/// Used in two shapes: (1) the one-shot <see cref="AutocompleteRequest"/>/response pair, where a
/// single response carries the settled snapshot (<see cref="IsComplete"/> = <c>true</c>); and
/// (2) the streaming <see cref="AutocompleteReference"/> workspace stream, where the producer pushes
/// a sequence of progressively-refined snapshots (<see cref="IsComplete"/> = <c>false</c>) and one
/// final snapshot with <see cref="IsComplete"/> = <c>true</c> when every provider has settled. The
/// streaming consumer does <c>TakeWhile(v =&gt; !v.IsComplete, inclusive: true)</c>.
/// </para>
/// </summary>
/// <param name="Items">The autocomplete items to display (current best, score-sorted).</param>
/// <param name="IsComplete">True when this is the settled (final) snapshot. Defaults to true so the
/// one-shot request/response callers are unaffected.</param>
/// <param name="Version">Monotonic snapshot version within a single streaming subscription (ordering
/// / diagnostics). Zero for one-shot responses.</param>
public record AutocompleteResponse(
    IReadOnlyList<AutocompleteItem> Items,
    bool IsComplete = true,
    int Version = 0);
