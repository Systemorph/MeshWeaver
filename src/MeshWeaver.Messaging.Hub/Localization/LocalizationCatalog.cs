using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;

namespace MeshWeaver.Messaging;

/// <summary>
/// The translated-string table for text that has NO declaration to hang a
/// <see cref="TranslationAttribute"/> off — Blazor markup, inline <c>Controls.*</c> literals,
/// toasts, dialog copy. Keys are dotted and namespaced by UI area (<c>chat.new</c>,
/// <c>menu.edit</c>, <c>settings.privacy</c>); the English catalog is the key list of record.
///
/// <para><b>Why this is <c>static readonly</c> and not a mesh-scoped singleton.</b> The repo bans
/// static COLLECTIONS because process-wide mutable state survives mesh disposal and bleeds across
/// tests and users. This table is never written after construction — it is loaded once from
/// embedded resources into <see cref="ImmutableDictionary{TKey,TValue}"/> and only ever read. That
/// puts it squarely in the sanctioned "immutable, read-only constant lookup" category alongside
/// media-type maps and role tables: identical in every test, nothing to <c>Clear()</c>, no
/// lifetime to tie to the mesh.</para>
///
/// <para>Lookup never throws and never returns null. A key missing from the requested language
/// falls back to English; a key missing from English falls back to the key itself, so an
/// untranslated string shows up as a visible <c>chat.new</c>-shaped token in the UI rather than as
/// a blank control — loud enough to notice, harmless enough to ship.</para>
/// </summary>
public static class LocalizationCatalog
{
    private static readonly ImmutableDictionary<string, ImmutableDictionary<string, string>> Catalogs
        = LoadAll();

    /// <summary>
    /// The keys defined in the English catalog — the key list of record, used by the
    /// translation-completeness test to assert every shipped language covers them.
    /// </summary>
    public static ImmutableHashSet<string> Keys
        => Catalogs.TryGetValue(Locales.Default, out var en)
            ? en.Keys.ToImmutableHashSet(StringComparer.Ordinal)
            : ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Returns the keys defined for <paramref name="locale"/>, for completeness checking.
    /// </summary>
    public static ImmutableHashSet<string> KeysFor(string locale)
        => Catalogs.TryGetValue(Locales.Resolve(locale), out var c)
            ? c.Keys.ToImmutableHashSet(StringComparer.Ordinal)
            : ImmutableHashSet<string>.Empty;

    /// <summary>
    /// Looks up <paramref name="key"/> in <paramref name="locale"/>, falling back to English and
    /// then to the key itself. <paramref name="args"/>, when supplied, are applied with
    /// <see cref="string.Format(IFormatProvider,string,object?[])"/> using the invariant culture —
    /// placeholders in translated text are positional (<c>{0}</c>), so a translator may reorder
    /// them for target-language word order without touching code.
    /// </summary>
    public static string Get(string key, string? locale, params object?[] args)
    {
        var text = Lookup(key, locale);
        if (args is not { Length: > 0 })
            return text;
        try
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args);
        }
        catch (FormatException)
        {
            // A malformed placeholder in a translation must never take down a render path.
            // Showing the unformatted template is strictly better than throwing mid-view.
            return text;
        }
    }

    /// <summary>
    /// Looks up <paramref name="key"/> in <paramref name="locale"/> and binds <paramref name="args"/>
    /// to its NAMED placeholders (<c>{path}</c>, <c>{count}</c>, …) — the lookup for text that was
    /// PERSISTED with its arguments and is resolved when someone reads it, i.e. activity transcripts
    /// (<c>LogMessage</c>, #3236).
    ///
    /// <para><b>Named, not positional, and that is the whole point.</b> A row written months ago must
    /// still bind correctly to a template that has since been rewritten or reordered; a positional
    /// <c>{0}</c> would silently rebind to a different value the moment a translator moved it. The
    /// two shapes cannot collide: <c>{0}</c> is not a valid name here, so a positional template is
    /// left untouched by this method and a named template is left untouched by
    /// <see cref="Get(string,string?,object?[])"/>'s <c>string.Format</c>.</para>
    ///
    /// <para>🚨 A key MISSING from every catalog falls back to <paramref name="fallback"/> — the
    /// English text the writer stored beside the key — and only then to the key itself. That is what
    /// makes removing or renaming an <c>activity.*</c> key safe: history keeps rendering the sentence
    /// it was written with instead of degrading to a raw token.</para>
    ///
    /// <para>Argument values arrive from JSON, so they are typically
    /// <see cref="JsonElement"/> rather than the CLR type that was written. They are formatted
    /// EXPLICITLY here — never cast — and numbers/dates use the culture derived from
    /// <paramref name="locale"/>, never an ambient <c>CultureInfo.CurrentCulture</c>.</para>
    /// </summary>
    /// <param name="key">The catalog key.</param>
    /// <param name="locale">The VIEWER's language tag, read explicitly off their AccessContext.</param>
    /// <param name="args">The named arguments, or null.</param>
    /// <param name="fallback">Text to use when the key is in no catalog; null falls back to the key.</param>
    /// <returns>The bound text; never null, never throws.</returns>
    public static string GetNamed(
        string key, string? locale, IReadOnlyDictionary<string, object>? args, string? fallback = null)
    {
        var resolved = Locales.Resolve(locale);
        var text = TryLookup(key, resolved, out var found)
            ? found
            : fallback ?? key;

        if (args is not { Count: > 0 } || text.IndexOf('{') < 0)
            return text;

        var culture = CultureOf(resolved);
        return NamedPlaceholder.Replace(text, match =>
            args.TryGetValue(match.Groups[1].Value, out var value)
                ? FormatArgument(value, culture)
                // An unknown name stays visible as `{name}` rather than becoming a blank: a gap in a
                // transcript should read as a gap, not as text that was never there.
                : match.Value);
    }

    /// <summary>
    /// Placeholder names are C#-identifier shaped, which is exactly what keeps this from touching a
    /// positional <c>{0}</c> template.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex NamedPlaceholder =
        new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Renders one persisted argument. <see cref="JsonElement"/> is handled by
    /// <see cref="JsonElement.ValueKind"/> rather than cast — a value that crossed a hub boundary and
    /// came back from storage is JSON, and <c>(string)value</c> on it is the silent-null trap
    /// AGENTS.md bans. Anything <see cref="IFormattable"/> is rendered in the VIEWER's culture.
    /// </summary>
    private static string FormatArgument(object? value, System.Globalization.CultureInfo culture) =>
        value switch
        {
            null => string.Empty,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? string.Empty,
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => string.Empty,
            JsonElement { ValueKind: JsonValueKind.Number } e =>
                e.TryGetInt64(out var l)
                    ? l.ToString(culture)
                    : e.TryGetDouble(out var d) ? d.ToString(culture) : e.GetRawText(),
            JsonElement e => e.ToString(),
            IFormattable f => f.ToString(null, culture),
            _ => value.ToString() ?? string.Empty,
        };

    /// <summary>
    /// The viewer's culture, derived EXPLICITLY from their resolved language tag. Never
    /// <c>CultureInfo.CurrentCulture</c> — on Blazor Server that is the container's culture, shared
    /// by every simultaneous viewer.
    /// </summary>
    private static System.Globalization.CultureInfo CultureOf(string resolvedLocale)
    {
        try
        {
            return System.Globalization.CultureInfo.GetCultureInfo(resolvedLocale);
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return System.Globalization.CultureInfo.InvariantCulture;
        }
    }

    /// <summary>
    /// Plural-aware lookup. Resolves <c>{key}.one</c> when <paramref name="count"/> is exactly 1 and
    /// <c>{key}.other</c> otherwise, then formats <paramref name="count"/> into it as <c>{0}</c>.
    /// English and German share this one/other split, which is why a two-form rule is sufficient
    /// here; a language with more forms (Polish, Russian, Arabic) would need this method extended
    /// rather than every call site changed.
    /// </summary>
    public static string Plural(string key, int count, string? locale)
        => Get(count == 1 ? $"{key}.one" : $"{key}.other", locale, count);

    private static string Lookup(string key, string? locale) =>
        // Visible-but-harmless: the raw key surfaces in the UI so a gap is obvious in review.
        TryLookup(key, Locales.Resolve(locale), out var text) ? text : key;

    /// <summary>
    /// The lookup half of <see cref="Lookup"/>, WITHOUT its key-as-text fallback — so a caller that
    /// has a better fallback than the raw key (an activity entry's stored English sentence) can use
    /// it. <paramref name="resolvedLocale"/> must already be through <see cref="Locales.Resolve"/>.
    /// </summary>
    private static bool TryLookup(string key, string resolvedLocale, out string text)
    {
        if (Catalogs.TryGetValue(resolvedLocale, out var catalog)
            && catalog.TryGetValue(key, out var localized))
        {
            text = localized;
            return true;
        }

        if (!string.Equals(resolvedLocale, Locales.Default, StringComparison.OrdinalIgnoreCase)
            && Catalogs.TryGetValue(Locales.Default, out var fallback)
            && fallback.TryGetValue(key, out var english))
        {
            text = english;
            return true;
        }

        text = key;
        return false;
    }

    private static ImmutableDictionary<string, ImmutableDictionary<string, string>> LoadAll()
    {
        var assembly = typeof(LocalizationCatalog).Assembly;
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var locale in Locales.Supported)
            builder[locale] = Load(assembly, locale);

        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, string> Load(Assembly assembly, string locale)
    {
        var resourceName = $"MeshWeaver.Messaging.Localization.strings.{locale}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return ImmutableDictionary<string, string>.Empty;

        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return ImmutableDictionary<string, string>.Empty;

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String)
                builder[property.Name] = property.Value.GetString()!;

        return builder.ToImmutable();
    }
}
