#nullable enable
using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The WRITE-TIME and RENDER-TIME halves of a localizable bell row (#4373) — the
/// <see cref="Notification"/> counterpart of <c>LogMessage.WithKey</c> +
/// <c>LogMessageLocalizationExtensions.Localize</c> (#3236), deliberately the same shape so a
/// reader who knows one knows the other.
///
/// <para><b>Why a notification cannot be localized where it is written.</b> Every one is raised on
/// a background reaction — the package-update reconciler, a module-discovery scan, a startup
/// import — running as SYSTEM with no viewer in scope. Resolving a key there picks the system
/// default (English) and then BAKES it into a durable row that outlives the write by days and is
/// read by several people whose languages differ. So the writer stores the KEY and its arguments,
/// and the bell resolves against <c>AccessContext.Locale</c> at read time.</para>
///
/// <para><b>Every call is safe on a row that carries no key</b> — which is every row written
/// before #4373 — so a renderer may switch to this unconditionally and nothing has to be
/// migrated.</para>
/// </summary>
public static class NotificationLocalizationExtensions
{
    /// <summary>
    /// Records the catalog keys for <see cref="Notification.Title"/> and
    /// <see cref="Notification.Message"/>, keeping the already-set English as the fallback.
    ///
    /// <code>
    /// new Notification { Title = $"Update available: {name}", Message = detail }
    ///     .WithKeys("plugins.update.available.title", [("name", name)],
    ///               "plugins.update.available.body",  [("detail", detail)])
    /// </code>
    /// </summary>
    /// <param name="notification">The row being written.</param>
    /// <param name="titleKey">Catalog key for the title; ignored when null or blank.</param>
    /// <param name="titleArgs">Named arguments for <paramref name="titleKey"/>.</param>
    /// <param name="messageKey">Catalog key for the body; ignored when null or blank.</param>
    /// <param name="messageArgs">Named arguments for <paramref name="messageKey"/>.</param>
    public static Notification WithKeys(
        this Notification notification,
        string? titleKey = null,
        (string Name, object? Value)[]? titleArgs = null,
        string? messageKey = null,
        (string Name, object? Value)[]? messageArgs = null) =>
        notification with
        {
            TitleKey = string.IsNullOrWhiteSpace(titleKey) ? notification.TitleKey : titleKey,
            TitleArgs = string.IsNullOrWhiteSpace(titleKey) ? notification.TitleArgs : Bind(titleArgs),
            MessageKey = string.IsNullOrWhiteSpace(messageKey) ? notification.MessageKey : messageKey,
            MessageArgs = string.IsNullOrWhiteSpace(messageKey) ? notification.MessageArgs : Bind(messageArgs),
        };

    /// <summary>
    /// The title in <paramref name="locale"/>: the catalog rendering of
    /// <see cref="Notification.TitleKey"/> bound to <see cref="Notification.TitleArgs"/> when the
    /// row carries one, otherwise the stored English <see cref="Notification.Title"/>.
    /// </summary>
    public static string LocalizeTitle(this Notification notification, string? locale) =>
        notification.TitleKey is { Length: > 0 } key
            // The stored English is the fallback, so a key that has since left the catalog still
            // renders the sentence the writer meant rather than a raw `plugins.…` token.
            ? LocalizationCatalog.GetNamed(key, locale, notification.TitleArgs, notification.Title)
            : notification.Title;

    /// <summary><see cref="LocalizeTitle(Notification, string?)"/> for the body.</summary>
    public static string LocalizeMessage(this Notification notification, string? locale) =>
        notification.MessageKey is { Length: > 0 } key
            ? LocalizationCatalog.GetNamed(key, locale, notification.MessageArgs, notification.Message)
            : notification.Message;

    /// <summary><see cref="LocalizeTitle(Notification, string?)"/> against the current viewer.</summary>
    public static string LocalizeTitle(this Notification notification, AccessService? accessService) =>
        notification.LocalizeTitle(accessService.ViewerLocale());

    /// <summary><see cref="LocalizeMessage(Notification, string?)"/> against the current viewer.</summary>
    public static string LocalizeMessage(this Notification notification, AccessService? accessService) =>
        notification.LocalizeMessage(accessService.ViewerLocale());

    /// <summary>
    /// Named arguments, scalarized. Values round-trip through JSON, so a value read back is
    /// typically a <see cref="System.Text.Json.JsonElement"/> rather than the CLR type that was
    /// written; storing only scalars is what keeps the template binding stable across that trip —
    /// the same contract <c>LogMessage.WithKey</c> holds.
    /// </summary>
    private static ImmutableDictionary<string, object>? Bind((string Name, object? Value)[]? args)
    {
        if (args is not { Length: > 0 })
            return null;

        var builder = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in args)
            if (!string.IsNullOrWhiteSpace(name))
                builder[name] = Scalarize(value);
        return builder.ToImmutable();
    }

    private static object Scalarize(object? value) =>
        value switch
        {
            null => string.Empty,
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal
                or DateTime or DateTimeOffset or TimeSpan => value,
            _ => value.ToString() ?? string.Empty,
        };
}
