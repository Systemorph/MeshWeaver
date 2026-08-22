using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// One language's override of a node's USER-VISIBLE display metadata — the three
/// <see cref="MeshNode"/> fields a picker, a catalog card or an autocomplete row actually shows.
///
/// <para>🚨 <b>What is deliberately NOT here.</b> Nothing MODEL-facing and nothing ADDRESSABLE.
/// An agent's <c>AgentConfiguration.Description</c> is the delegation/hand-off catalogue the model
/// reads to choose an agent, and a skill's <c>Instructions</c> body is its procedure — both are
/// prompt text, and translating prompt text changes behaviour rather than presentation (the same
/// rule that keeps <c>[Description]</c> untranslated). Identifiers are excluded for a harder
/// reason: a skill is invoked by its node <b>Id</b> (<c>/space</c>) and an agent is resolved by
/// path, so those are wire tokens. This record touches display text only, which is why localizing
/// it can never change what a mention, a slash command or a delegation resolves to.</para>
/// </summary>
public sealed record LocalizedNodeText
{
    /// <summary>The node's display name in this language; null keeps the authored (English) one.</summary>
    public string? Name { get; init; }

    /// <summary>The node's help text in this language; null keeps the authored (English) one.</summary>
    public string? Description { get; init; }

    /// <summary>The node's grouping label in this language; null keeps the authored (English) one.</summary>
    public string? Category { get; init; }

    /// <summary>The display fields this record can carry, in author-facing order. The completeness
    /// guard reports against these names, so it cannot drift from the record.</summary>
    public static ImmutableList<string> Fields { get; } = ["name", "description", "category"];

    /// <summary>The value of <paramref name="field"/> (one of <see cref="Fields"/>), or null.</summary>
    /// <param name="field">The field name, case-insensitive.</param>
    /// <returns>The value, or null when unset or unknown.</returns>
    public string? Get(string field) => field?.ToLowerInvariant() switch
    {
        "name" => Name,
        "description" => Description,
        "category" => Category,
        _ => null,
    };

    /// <summary>True when every field this record can carry is set to something non-blank.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(Description)
        && !string.IsNullOrWhiteSpace(Category);
}

/// <summary>
/// Content that carries per-language overrides of its node's display metadata. Implemented by the
/// content types whose nodes are rendered as a NAME + DESCRIPTION somewhere a person reads — today
/// <c>SkillDefinition</c> and <c>AgentConfiguration</c>.
/// </summary>
public interface ILocalizedNodeText
{
    /// <summary>BCP-47 tag → the display overrides for that language. Null or empty = English only.</summary>
    IReadOnlyDictionary<string, LocalizedNodeText>? Translations { get; }
}

/// <summary>
/// The ONE rule for rendering a node's display metadata in a viewer's language — the node-data twin
/// of <c>LocalizeExtensions</c> (string catalog) and <c>[Translation]</c> (declarations). All three
/// resolve the language through <see cref="Locales"/>, so they cannot disagree about which language
/// a viewer is in.
///
/// <para>🚨 <b>The locale is an ARGUMENT, never read ambiently here.</b> Same hazard as
/// <c>ToDisplayTime</c>: <c>AccessContext.Locale</c> rides an <c>AsyncLocal</c> that does not
/// survive a scheduler hop, so a picker filled on a LATER emission would silently resolve to
/// English while the page around it rendered German — and two call sites resolving separately can
/// disagree with each other on the same page. Resolve ONCE on the render turn
/// (<c>host.ViewerLocale()</c> / <c>accessService.ViewerLocale()</c>) and pass the value down.</para>
///
/// <para><b>Per-FIELD fallback, never per-record.</b> A translation that sets only
/// <c>description</c> keeps the authored name — the alternative (an all-or-nothing swap) would
/// blank a name the author never intended to change. Absence therefore degrades to readable English
/// rather than to a hole, exactly like a missing catalog key.</para>
/// </summary>
public static class NodeTextTranslations
{
    /// <summary>
    /// The display overrides <paramref name="content"/> declares for <paramref name="locale"/>, or
    /// null when it declares none. The tag is resolved through <see cref="Locales.Resolve"/>, so
    /// <c>de-CH</c> and <c>de-AT</c> both find a <c>de</c> entry and an unsupported tag lands on
    /// English (which by construction has no entry — English IS the authored text).
    /// </summary>
    /// <param name="content">The node's content; anything not carrying translations yields null.</param>
    /// <param name="locale">The viewer's language tag, resolved once on the render turn.</param>
    /// <returns>The overrides, or null.</returns>
    public static LocalizedNodeText? For(object? content, string? locale)
    {
        if (content is not ILocalizedNodeText localized || localized.Translations is not { Count: > 0 } map)
            return null;
        var resolved = Locales.Resolve(locale);
        if (string.Equals(resolved, Locales.Default, StringComparison.OrdinalIgnoreCase))
            return null;   // English is the authored text; there is nothing to override it with.
        foreach (var (tag, text) in map)
            if (string.Equals(Locales.TryMatch(tag), resolved, StringComparison.OrdinalIgnoreCase))
                return text;
        return null;
    }

    /// <summary>
    /// <paramref name="node"/> with its display metadata rendered in <paramref name="locale"/> —
    /// the form every picker, catalog and autocomplete surface should bind to. Returns the SAME
    /// instance when there is nothing to override, so a fully-English deployment pays nothing and
    /// reference equality still holds for callers that cache on it.
    /// </summary>
    /// <param name="node">The node to render.</param>
    /// <param name="locale">The viewer's language tag, resolved once on the render turn.</param>
    /// <returns>The node, with Name/Description/Category localized where a translation exists.</returns>
    public static MeshNode Localized(this MeshNode node, string? locale)
    {
        ArgumentNullException.ThrowIfNull(node);
        var text = For(node.Content, locale);
        if (text is null)
            return node;
        return node with
        {
            Name = Blank(text.Name) ? node.Name : text.Name,
            Description = Blank(text.Description) ? node.Description : text.Description,
            Category = Blank(text.Category) ? node.Category : text.Category,
        };
    }

    /// <summary>The localized display NAME of <paramref name="node"/>, falling back to its authored one.</summary>
    /// <param name="node">The node.</param>
    /// <param name="locale">The viewer's language tag.</param>
    /// <returns>The name, or null when the node has none in any language.</returns>
    public static string? LocalizedName(this MeshNode node, string? locale)
    {
        var text = For(node?.Content, locale)?.Name;
        return Blank(text) ? node?.Name : text;
    }

    /// <summary>The localized DESCRIPTION of <paramref name="node"/>, falling back to its authored one.</summary>
    /// <param name="node">The node.</param>
    /// <param name="locale">The viewer's language tag.</param>
    /// <returns>The description, or null when the node has none in any language.</returns>
    public static string? LocalizedDescription(this MeshNode node, string? locale)
    {
        var text = For(node?.Content, locale)?.Description;
        return Blank(text) ? node?.Description : text;
    }

    /// <summary>
    /// The languages <paramref name="content"/> declares a translation for, resolved to supported
    /// tags and de-duplicated. Empty for content that declares none — which is a complete, valid,
    /// English-only node, not a defect.
    /// </summary>
    /// <param name="content">The node's content.</param>
    /// <returns>The declared supported languages.</returns>
    public static ImmutableHashSet<string> DeclaredLocales(object? content)
    {
        if (content is not ILocalizedNodeText localized || localized.Translations is not { Count: > 0 } map)
            return [];
        var result = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in map.Keys)
            if (Locales.TryMatch(tag) is { } matched
                && !string.Equals(matched, Locales.Default, StringComparison.OrdinalIgnoreCase))
                result = result.Add(matched);
        return result;
    }

    /// <summary>
    /// The fields a declared translation MUST carry — the two a person actually reads in a picker
    /// row. <see cref="LocalizedNodeText.Category"/> is deliberately optional: a category is one
    /// grouping label shared by a whole pack ("Skills"), so requiring it per node would mean
    /// twenty-one copies of the same word, and a pack that does need it can still set it.
    /// </summary>
    public static ImmutableList<string> RequiredFields { get; } = ["name", "description"];

    /// <summary>
    /// 🚨 THE HALF-LOCALIZED GUARD. What <paramref name="node"/> is MISSING to be fully localized
    /// into <paramref name="requiredLocales"/>, as <c>"{locale}:{field}"</c> entries — empty when
    /// it is complete.
    ///
    /// <para>The rule is per PACK, not per node, and it is the only rule that makes a translated
    /// pack trustworthy. A missing translation is invisible: the field falls back to English, so a
    /// German picker with three English rows in it looks like a design choice rather than a gap.
    /// Callers derive <paramref name="requiredLocales"/> from the pack's own union of declared
    /// languages (<see cref="DeclaredLocales"/>), so an English-only pack requires nothing — the
    /// gate is "do not ship HALF a language", never "you must ship every language".</para>
    ///
    /// <para>A required field is only required when the node HAS one: a node with no description
    /// cannot be missing a translated description.</para>
    /// </summary>
    /// <param name="node">The node to check.</param>
    /// <param name="requiredLocales">The languages this node's pack must cover.</param>
    /// <returns>The missing <c>"{locale}:{field}"</c> entries, ordered.</returns>
    public static ImmutableList<string> MissingTranslations(
        MeshNode node, IEnumerable<string> requiredLocales)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(requiredLocales);
        var missing = ImmutableList.CreateBuilder<string>();
        foreach (var locale in requiredLocales.Select(Locales.Resolve).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            if (string.Equals(locale, Locales.Default, StringComparison.OrdinalIgnoreCase))
                continue;   // English IS the authored text — there is nothing to declare.
            var text = For(node.Content, locale);
            foreach (var field in RequiredFields)
            {
                var authored = field switch
                {
                    "name" => node.Name,
                    "description" => node.Description,
                    "category" => node.Category,
                    _ => null,
                };
                if (Blank(authored))
                    continue;   // nothing to translate
                if (Blank(text?.Get(field)))
                    missing.Add($"{locale}:{field}");
            }
        }
        return missing.ToImmutable();
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
