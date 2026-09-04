#nullable enable
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// A single log entry recorded against an <see cref="ActivityLog"/>.
///
/// <para>🚨 <b>An activity transcript is a RENDERED surface, and <see cref="Message"/> alone cannot
/// be localized (#3236).</b> A log entry is written server-side at the moment the work happens, with
/// NO viewer in scope — a static-repo import runs as System at boot, a compile runs on a node hub, a
/// write-conflict record is raised inside a storage adapter — and the row it lands in is later read
/// by several viewers whose languages differ. Resolving a locale at write time would therefore be
/// wrong even where it is possible: it freezes one viewer's language into a shared record.</para>
///
/// <para>So a localizable entry carries the <see cref="MessageKey"/> and its
/// <see cref="MessageArgs"/> and is resolved at RENDER time, where the viewer's
/// <c>AccessContext.Locale</c> is what <c>LayoutAreaHost</c> already restores for the render scope.
/// <see cref="Message"/> stays the ENGLISH FALLBACK, so an un-migrated writer, an old persisted row
/// and a key that has since left the catalog all still render exactly as they did before.</para>
///
/// <para>Write a localizable entry with <see cref="WithKey"/>:
/// <code>
/// new LogMessage($"Node not found at path: {path}", LogLevel.Error)
///     .WithKey("activity.delete.notFound", ("path", path))
/// </code>
/// and render it with <c>message.Localize(host.ViewerLocale())</c>
/// (<c>LogMessageLocalizationExtensions</c>, in <c>MeshWeaver.Data</c>).</para>
/// </summary>
/// <param name="Message">The log message text, in English — the fallback when no
/// <see cref="MessageKey"/> is set or its key is not in the catalog.</param>
/// <param name="LogLevel">The severity of the message.</param>
public record LogMessage(string Message, LogLevel LogLevel)
{
    /// <summary>UTC timestamp when the message was created.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    /// <summary>The logging category that produced the message, if any.</summary>
    public string? CategoryName { get; init; }
    /// <summary>The active logging scopes at the time the message was created, if any.</summary>
    public IReadOnlyCollection<KeyValuePair<string, object>>? Scopes { get; init; } = [];

    /// <summary>
    /// Catalog key (<c>activity.*</c>); when set, <see cref="Message"/> is the English fallback and
    /// the renderer resolves THIS instead, in the viewer's language.
    /// <para>Null is the common case and always will be: every row persisted before #3236, and every
    /// entry whose text is verbatim tool output (a Roslyn diagnostic, a stack trace, an upstream
    /// exception message) that no catalog can sensibly carry.</para>
    /// </summary>
    public string? MessageKey { get; init; }

    /// <summary>
    /// The arguments for <see cref="MessageKey"/>, by NAME — the catalog template refers to them as
    /// <c>{path}</c>, <c>{count}</c>, … so a translator may reorder them freely for target-language
    /// word order. Named rather than positional precisely because these are PERSISTED: a row written
    /// months ago must still bind correctly to a template someone has since rewritten.
    /// <para>Values round-trip through JSON, so a value read back is typically a
    /// <see cref="System.Text.Json.JsonElement"/> rather than the CLR type that was written. The
    /// renderer handles that explicitly — it never casts (see
    /// <c>LocalizationCatalog.GetNamed</c>).</para>
    ///
    /// <para><b>On the cost.</b> The key plus the arguments re-state the variable parts of
    /// <see cref="Message"/>, so a keyed entry is meaningfully bigger than an unkeyed one and the
    /// activity head is re-serialised on every append. The growth is proportional and bounded — the
    /// window caps at <see cref="ActivityLog.MessageWindowLimit"/> entries, and the arguments are
    /// short (a path, a count, an upstream fragment) — so it does not change the shape of the write.
    /// Dropping the English fallback would be smaller and is deliberately not done: it is what keeps
    /// every pre-#3236 row, every un-migrated writer, and every key that later leaves the catalog
    /// rendering the sentence they were written with.</para>
    /// </summary>
    public ImmutableDictionary<string, object>? MessageArgs { get; init; }

    /// <summary>
    /// Marks this entry as localizable: the catalog <paramref name="key"/> resolved at render time,
    /// with <paramref name="args"/> bound to the template's named placeholders. The existing
    /// <see cref="Message"/> is kept as the English fallback — always write it as the sentence the
    /// key renders in English, so the two cannot drift.
    /// </summary>
    /// <param name="key">A key in <c>strings.en.json</c> / <c>strings.de.json</c>, conventionally
    /// under the <c>activity.</c> namespace. A blank key leaves the entry unchanged rather than
    /// throwing: an activity transcript must never be the thing that takes down the work it records.</param>
    /// <param name="args">Named arguments for the template's <c>{name}</c> placeholders. A null
    /// value binds as the empty string.</param>
    /// <returns>A copy carrying the key and arguments.</returns>
    public LogMessage WithKey(string key, params (string Name, object? Value)[] args)
    {
        if (string.IsNullOrWhiteSpace(key))
            return this;

        if (args is not { Length: > 0 })
            return this with { MessageKey = key, MessageArgs = null };

        var builder = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in args)
            if (!string.IsNullOrWhiteSpace(name))
                builder[name] = value ?? string.Empty;

        return this with
        {
            MessageKey = key,
            MessageArgs = builder.Count == 0 ? null : builder.ToImmutable(),
        };
    }
}
