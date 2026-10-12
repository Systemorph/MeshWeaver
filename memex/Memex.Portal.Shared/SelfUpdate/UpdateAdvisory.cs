using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// 🌍 One release-availability advisory as <c>Admin/UpdatePolicy</c> persists it
/// (<see cref="UpdatePolicyContent.AdvisoryItems"/>, MeshWeaver#6540): the English sentence, the
/// reason code that renders it in the viewer's language (a <see cref="ReleaseAdvisoryCode"/> — the
/// catalog key) and the code's named arguments. The same shape a notification persists its title
/// in (<c>TitleKey</c>/<c>TitleArgs</c>): the writer has no viewer in scope, several viewers with
/// different languages read the one record, so the key travels and the text is resolved at render
/// time.
/// </summary>
/// <param name="Text">The advisory in English — the log line's wording, and the fallback when
/// <see cref="Key"/> is null or no catalog carries it.</param>
public sealed record UpdateAdvisory(string Text)
{
    /// <summary>The reason code / catalog key, or null for text this process did not author (a
    /// verbatim upstream diagnostic).</summary>
    public string? Key { get; init; }

    /// <summary>The named arguments for <see cref="Key"/>'s <c>{name}</c> placeholders; values read
    /// back from JSON arrive as <see cref="System.Text.Json.JsonElement"/>, which
    /// <c>LocalizationCatalog.GetNamed</c> formats explicitly.</summary>
    public ImmutableDictionary<string, object>? Args { get; init; }

    /// <summary>The persisted form of a composed advisory.</summary>
    /// <param name="text">The verdict's advisory (<c>UpdatabilityVerdict.AdvisoryTexts</c>).</param>
    /// <returns>The item to store.</returns>
    public static UpdateAdvisory From(LocalizableText text) =>
        new(text.English) { Key = text.Key, Args = text.PersistedArgs() };

    /// <summary>
    /// The advisory in <paramref name="locale"/> — the catalog rendering of <see cref="Key"/> bound
    /// to <see cref="Args"/>, or the stored English when there is no key or no catalog carries it
    /// (an unknown code is never given a meaning of its own).
    /// </summary>
    /// <param name="locale">The VIEWER's language tag (<c>host.ViewerLocale()</c>).</param>
    /// <returns>The text to render; never null.</returns>
    public string Localize(string? locale) =>
        Key is { Length: > 0 } key
            ? LocalizationCatalog.GetNamed(key, locale, Args, Text)
            : Text;
}
