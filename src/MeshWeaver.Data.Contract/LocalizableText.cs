#nullable enable
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// A refusal (or any other server-authored sentence) carried as BOTH its English text and the
/// catalog key that renders it in the viewer's language — the pair <see cref="LogMessage.WithKey"/>
/// needs, kept together from the place that composes the sentence to the place that logs it.
///
/// <para><b>Why the pair has to travel.</b> A refusal is frequently composed by a shared guard
/// (<c>AccessAssignmentGuard</c>, <c>NodeTypeResolution</c>) and logged by a handler several frames
/// away. Handing back only the finished English string — which is what those helpers did — throws
/// the key away at the first frame, and the handler cannot recover it: it does not know WHICH of the
/// guard's branches fired, so it cannot name the key or bind the arguments. That is precisely how
/// <c>HandleCreateOrUpdateNodeRequest</c>'s failure surface ended up English-only while its success
/// surface was localized (MeshWeaver#3917).</para>
///
/// <para><b>There is deliberately no implicit conversion from <see cref="string"/>.</b> An unkeyed
/// sentence is the defect this type exists to prevent, so producing one has to be spelled out —
/// <see cref="Verbatim"/> — and that name is the review signal. Use it only where the text really is
/// upstream output no catalog can carry (a Roslyn diagnostic, a stack trace, an exception message
/// quoted whole); where the sentence AROUND such a fragment is ours, key the sentence and pass the
/// fragment as an argument, exactly as <c>activity.dataUpdate.streamUpdateFailed</c> does.</para>
/// </summary>
/// <param name="English">The sentence in English — the wire value, and the fallback
/// <see cref="LogMessage.Message"/>. Always the sentence <paramref name="Key"/> renders in English,
/// so the two cannot drift.</param>
/// <param name="Key">The catalog key in <c>strings.en.json</c>/<c>strings.de.json</c>, or null when
/// the text is verbatim upstream output.</param>
/// <param name="Args">Named arguments for the key's <c>{name}</c> placeholders.</param>
public sealed record LocalizableText(
    string English,
    string? Key = null,
    ImmutableArray<KeyValuePair<string, object?>> Args = default)
{
    /// <summary>
    /// A localizable sentence: its English rendering plus the catalog key and named arguments that
    /// reproduce it in another language.
    /// </summary>
    /// <param name="english">The English sentence — write it as what <paramref name="key"/> renders
    /// in English.</param>
    /// <param name="key">The catalog key.</param>
    /// <param name="args">Named arguments for the template's <c>{name}</c> placeholders.</param>
    /// <returns>The pair.</returns>
    public static LocalizableText Keyed(string english, string key,
        params (string Name, object? Value)[] args)
        => new(english, key,
            args is { Length: > 0 }
                ? [.. args.Select(a => new KeyValuePair<string, object?>(a.Name, a.Value))]
                : []);

    /// <summary>
    /// Text that is NOT localizable because it is verbatim upstream output — an exception message,
    /// a Roslyn diagnostic, a storage adapter's own words. Renders identically in every language,
    /// which is the honest outcome for text this process did not author.
    /// </summary>
    /// <param name="english">The upstream text.</param>
    /// <returns>An unkeyed pair.</returns>
    public static LocalizableText Verbatim(string english) => new(english);

    /// <summary>
    /// The entry to append to an activity transcript: <see cref="English"/> as the stored fallback,
    /// <see cref="Key"/> and <see cref="Args"/> resolved at render time in the viewer's language.
    /// </summary>
    /// <param name="level">The severity to record.</param>
    /// <returns>The log entry.</returns>
    public LogMessage ToLogMessage(LogLevel level) =>
        // Chained unconditionally, on purpose. `WithKey` returns the entry UNCHANGED on a blank key
        // by contract, so the Verbatim case needs no branch — and the construction site then reads
        // as keyed to UnkeyedActivityLogMessageRatchetGuard, which scans the source shape rather
        // than the runtime value. A branch here would put this seam on that inventory forever.
        new LogMessage(English, level)
            .WithKey(Key ?? "",
                [.. (Args.IsDefault ? [] : Args).Select(a => (a.Key, a.Value))]);
}
