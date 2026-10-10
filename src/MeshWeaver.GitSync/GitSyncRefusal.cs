using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// A refusal GitSync composes itself, carried as a STABLE catalog key plus NAMED arguments so it
/// renders in the language of whoever reads it — the same render-time model as a localizable
/// activity entry (<see cref="LogMessage.WithKey"/>, <c>LogMessageLocalizationExtensions</c>). The
/// refusal is raised server-side, often inside an activity running as System with no viewer in
/// scope, so the text cannot be chosen at the throw site; it is chosen where it is shown.
/// </summary>
/// <param name="Key">A key in <c>strings.en.json</c> / <c>strings.de.json</c> — or, for a
/// <see cref="Legacy"/> refusal, only the refusal's stable name.</param>
/// <param name="Args">The template's named arguments.</param>
public sealed record GitSyncRefusal(string Key, ImmutableDictionary<string, object> Args)
{
    /// <summary>
    /// For a refusal that predates this type: its exact, shipped English message. Such a refusal
    /// keeps its shipped THROWN contract — <see cref="ToException"/> throws a plain
    /// <see cref="InvalidOperationException"/> with exactly this message, which callers and the
    /// dependent suites assert on — while every RENDERED surface (the activity transcript, the
    /// settings tab) still resolves <see cref="Key"/> for the viewer. The English catalog entry
    /// is this same sentence. Null for every other refusal.
    /// </summary>
    public string? LegacyMessage { get; init; }

    /// <summary>A refusal that keeps its shipped exception type and English message unchanged
    /// (see <see cref="LegacyMessage"/>) and is rendered through <paramref name="key"/> with
    /// <paramref name="args"/> wherever a viewer reads it.</summary>
    public static GitSyncRefusal Legacy(string key, string message, params (string Name, object Value)[] args)
        => Of(key, args) with { LegacyMessage = message };

    /// <summary>Builds a refusal from a key and named arguments.</summary>
    public static GitSyncRefusal Of(string key, params (string Name, object Value)[] args)
        => new(key, args.ToImmutableDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal));

    /// <summary>The refusal in <paramref name="locale"/> (English when null or unknown).</summary>
    public string Render(string? locale) => LocalizationCatalog.GetNamed(Key, locale, Args, LegacyMessage);

    /// <summary>The refusal as a localizable activity entry: English text plus the key and its
    /// arguments, resolved at render time for each viewer.</summary>
    public LogMessage ToLogMessage(LogLevel level)
        => new LogMessage(LegacyMessage ?? Render(null), level)
            .WithKey(Key, Args.Select(a => (a.Key, (object?)a.Value)).ToArray());

    /// <summary>The exception that carries this refusal through an observable pipeline: a
    /// <see cref="GitSyncRefusalException"/> for a localizable refusal, the shipped plain
    /// <see cref="InvalidOperationException"/> for a <see cref="Legacy"/> one — whose exact type and
    /// message are the shipped contract, so the refusal rides along in <see cref="Exception.Data"/>
    /// (<see cref="GitSyncRefusalException.TryGet"/>) for the surfaces that render it.</summary>
    public Exception ToException()
    {
        if (LegacyMessage is null)
            return new GitSyncRefusalException(this);
        var legacy = new InvalidOperationException(LegacyMessage);
        legacy.Data[GitSyncRefusalException.DataKey] = this;
        return legacy;
    }
}

/// <summary>
/// Carries a <see cref="GitSyncRefusal"/> through an <see cref="IObservable{T}"/>'s error channel.
/// <see cref="Exception.Message"/> is the English rendering (for logs and callers that do not
/// localize); every GitSync surface that shows the error renders <see cref="Refusal"/> for its
/// viewer instead (<see cref="GitSyncRefusalException.Localize"/>).
/// </summary>
public sealed class GitSyncRefusalException(GitSyncRefusal refusal)
    : InvalidOperationException(refusal.Render(null))
{
    /// <summary>The key and arguments of the refusal.</summary>
    public GitSyncRefusal Refusal { get; } = refusal;

    /// <summary>The <see cref="Exception.Data"/> key a <see cref="GitSyncRefusal.Legacy"/> refusal's
    /// plain exception carries its refusal under.</summary>
    public const string DataKey = "MeshWeaver.GitSync.Refusal";

    /// <summary>The refusal an exception carries — as a <see cref="GitSyncRefusalException"/>, or in
    /// the <see cref="Exception.Data"/> of a legacy refusal's plain exception.</summary>
    public static bool TryGet(Exception exception, out GitSyncRefusal refusal)
    {
        if (exception is GitSyncRefusalException typed)
        {
            refusal = typed.Refusal;
            return true;
        }
        if (exception.Data[DataKey] is GitSyncRefusal carried)
        {
            refusal = carried;
            return true;
        }
        refusal = null!;
        return false;
    }

    /// <summary>
    /// The text to show a viewer in <paramref name="locale"/> for ANY exception: a GitSync refusal
    /// in the viewer's language, every other exception's own message unchanged.
    /// </summary>
    public static string Localize(Exception exception, string? locale)
        => TryGet(exception, out var refusal) ? refusal.Render(locale) : exception.Message;
}
