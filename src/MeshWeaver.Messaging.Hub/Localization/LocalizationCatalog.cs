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
    /// Plural-aware lookup. Resolves <c>{key}.one</c> when <paramref name="count"/> is exactly 1 and
    /// <c>{key}.other</c> otherwise, then formats <paramref name="count"/> into it as <c>{0}</c>.
    /// English and German share this one/other split, which is why a two-form rule is sufficient
    /// here; a language with more forms (Polish, Russian, Arabic) would need this method extended
    /// rather than every call site changed.
    /// </summary>
    public static string Plural(string key, int count, string? locale)
        => Get(count == 1 ? $"{key}.one" : $"{key}.other", locale, count);

    private static string Lookup(string key, string? locale)
    {
        var resolved = Locales.Resolve(locale);
        if (Catalogs.TryGetValue(resolved, out var catalog)
            && catalog.TryGetValue(key, out var text))
            return text;

        if (!string.Equals(resolved, Locales.Default, StringComparison.OrdinalIgnoreCase)
            && Catalogs.TryGetValue(Locales.Default, out var fallback)
            && fallback.TryGetValue(key, out var english))
            return english;

        // Visible-but-harmless: the raw key surfaces in the UI so a gap is obvious in review.
        return key;
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
