using System.Collections.Immutable;

namespace MeshWeaver.Mesh;

/// <summary>
/// One remembered completion acceptance: what the user picked, and what they had typed when they
/// picked it.
/// </summary>
/// <param name="Key">Identity of the chosen item — kind + label, so <c>Stack</c> the property and
/// <c>Stack</c> the class are remembered separately (VS Code keys its LRU the same way).</param>
/// <param name="Prefix">The word being completed at the moment of acceptance ("" when the user had
/// typed nothing — e.g. accepted straight off a <c>.</c>).</param>
/// <param name="Label">The item's label, which is what a later selection matches against.</param>
/// <param name="Kind">The LSP completion kind, as its wire value.</param>
/// <param name="Touch">Monotonic acceptance sequence — higher is more recent.</param>
public sealed record CompletionMemoryEntry(string Key, string Prefix, string Label, int Kind, long Touch);

/// <summary>
/// A user's completion acceptance history — the mesh's equivalent of VS Code's suggest memory
/// (<c>editor.suggestSelection</c>), stored per user at <c>{userId}/_Settings/Completions</c>.
///
/// <para><b>Why it exists.</b> Frequency over a corpus predicts what people generally write;
/// it cannot know that <i>you</i> always pick <c>Markdown</c> off <c>Controls.</c>. VS Code's
/// answer is to remember acceptances and use them to choose the SELECTED item — never to reorder
/// the list, so the matcher's ranking is left intact and only the highlighted row changes. This
/// type is that memory, and <see cref="Select"/> reproduces both of VS Code's modes:</para>
/// <list type="bullet">
///   <item><b>recentlyUsedByPrefix</b> — the strongest signal: what you accepted last time you had
///   typed this word. VS Code stores prefixes in a trie and looks up the LONGEST stored prefix that
///   is a prefix of the current word, so accepting <c>Stack</c> after typing <c>St</c> still
///   selects it when you later type <c>Sta</c>. <see cref="Select"/> does the same.</item>
///   <item><b>recentlyUsed</b> — the fallback when no prefix entry applies: among the candidates,
///   the one accepted most recently.</item>
/// </list>
///
/// <para>Bounded at <see cref="MaxEntries"/> (VS Code's own limit), dropping the least recently
/// accepted — the memory is a ranking hint, so losing the tail is free.</para>
/// </summary>
public sealed record CompletionMemory
{
    /// <summary>The retained acceptances, newest-touch first is NOT guaranteed — read via <see cref="Select"/>.</summary>
    public ImmutableList<CompletionMemoryEntry> Entries { get; init; } = ImmutableList<CompletionMemoryEntry>.Empty;

    /// <summary>The monotonic acceptance counter backing <see cref="CompletionMemoryEntry.Touch"/>.</summary>
    public long Sequence { get; init; }

    /// <summary>How many acceptances are retained — VS Code's suggest-memory bound.</summary>
    public const int MaxEntries = 300;

    /// <summary>
    /// Records an acceptance, replacing any previous entry for the same item+prefix and trimming
    /// the least recently accepted once the bound is exceeded. Pure — returns the new memory.
    /// </summary>
    public CompletionMemory Record(string? prefix, string label, int kind)
    {
        if (string.IsNullOrEmpty(label))
            return this;

        var touch = Sequence + 1;
        var key = KeyOf(kind, label);
        var word = prefix ?? string.Empty;
        var entries = Entries
            .RemoveAll(e => string.Equals(e.Key, key, StringComparison.Ordinal)
                && string.Equals(e.Prefix, word, StringComparison.OrdinalIgnoreCase))
            .Add(new CompletionMemoryEntry(key, word, label, kind, touch));

        if (entries.Count > MaxEntries)
            entries = entries.OrderByDescending(e => e.Touch).Take(MaxEntries).ToImmutableList();

        return this with { Entries = entries, Sequence = touch };
    }

    /// <summary>
    /// The label to PRESELECT among <paramref name="candidates"/> for a user who has currently
    /// typed <paramref name="prefix"/>, or null when this memory says nothing about them.
    /// Prefix memory wins over plain recency (VS Code's by-prefix mode is the more specific
    /// signal); within prefix memory the LONGEST stored prefix that still prefixes the typed word
    /// wins, then the most recent. Pure and total.
    /// </summary>
    public string? Select(IReadOnlyCollection<(string Label, int Kind)> candidates, string? prefix)
    {
        if (Entries.IsEmpty || candidates.Count == 0)
            return null;

        var keys = candidates.Select(c => KeyOf(c.Kind, c.Label)).ToHashSet(StringComparer.Ordinal);
        var word = prefix ?? string.Empty;

        if (word.Length > 0)
        {
            var byPrefix = Entries
                .Where(e => e.Prefix.Length > 0
                    && word.StartsWith(e.Prefix, StringComparison.OrdinalIgnoreCase)
                    && keys.Contains(e.Key))
                .OrderByDescending(e => e.Prefix.Length)
                .ThenByDescending(e => e.Touch)
                .FirstOrDefault();
            if (byPrefix is not null)
                return byPrefix.Label;
        }

        return Entries
            .Where(e => keys.Contains(e.Key))
            .OrderByDescending(e => e.Touch)
            .FirstOrDefault()
            ?.Label;
    }

    /// <summary>Item identity: kind + label, so same-named symbols of different kinds stay distinct.</summary>
    public static string KeyOf(int kind, string label) => $"{kind}:{label}";
}
