using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Population + override logic for a user's <see cref="User.Locale"/> — the value that drives the
/// per-viewer TEXT seam (<c>AccessService.Localize</c>, resolved once onto
/// <see cref="AccessContext.Locale"/> when the circuit context is built). The direct twin of
/// <see cref="TimeZonePreference"/>: same write-once semantics, same reactive population, same
/// settings-picker shape — only the value differs (a BCP-47 language tag instead of an IANA zone).
/// The language list and the matching rule live in <see cref="Locales"/>; this type owns only the
/// profile-write policy.
///
/// <para>The one hard correctness property is <b>write-once</b>: the browser-detected language is
/// written ONLY when the profile has no language yet. A user's explicit pick (or a value written on
/// an earlier session) is never clobbered by a later session — someone who chose German but signs
/// in from an English-configured kiosk keeps German. <see cref="ShouldWrite"/> is the pure decision
/// (deterministically testable); <see cref="PopulateOnce"/> applies it reactively against the live
/// node stream, re-checking inside the write lambda so the guard holds even under a concurrent
/// write.</para>
/// </summary>
public static class LocalePreference
{
    /// <summary>
    /// The write-once decision. Returns the (normalized, supported) language tag to persist when —
    /// and only when — the profile currently has NO language (<paramref name="currentLocale"/>
    /// null/whitespace) and <paramref name="detectedLocale"/> resolves to a language this
    /// deployment actually ships. Returns <c>null</c> to mean "do not write": the profile already
    /// has a language (never overwrite), nothing was detected, or the detected language is one we
    /// do not ship.
    ///
    /// <para>That last case is deliberate. Leaving an unsupported language UNWRITTEN keeps the
    /// profile null rather than pinning it to a tag we would only render in English — so when a
    /// translation for it ships later, that user picks it up automatically instead of being stuck
    /// behind a stale explicit-looking value.</para>
    /// </summary>
    public static string? ShouldWrite(string? currentLocale, string? detectedLocale)
        => string.IsNullOrWhiteSpace(currentLocale) && !string.IsNullOrWhiteSpace(detectedLocale)
            ? Locales.TryMatch(detectedLocale)
            : null;

    /// <summary>
    /// Reactively populates the user node's <see cref="User.Locale"/> from a browser-detected
    /// language, ONCE. Reads the node at <paramref name="userPath"/> once off the shared node
    /// stream, and — only if the stored language is empty and the detected one is supported
    /// (<see cref="ShouldWrite"/>) — writes it through <c>GetMeshNodeStream(userPath).Update(...)</c>
    /// (so the write carries the caller's identity, per AccessContext propagation). The write lambda
    /// re-checks the latest state and no-ops if a language appeared in the meantime, so a non-empty
    /// value is NEVER overwritten. Emits the updated node when it wrote, or completes empty when it
    /// skipped. Cold — the caller MUST subscribe.
    /// </summary>
    public static IObservable<MeshNode> PopulateOnce(
        IMessageHub hub, string userPath, string? detectedLocale, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(userPath) || string.IsNullOrWhiteSpace(detectedLocale))
            return Observable.Empty<MeshNode>();

        return hub.GetMeshNodeStream(userPath)
            .Where(node => node is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .SelectMany(node =>
            {
                var user = node!.ContentAs<User>(options);
                var toWrite = ShouldWrite(user?.Locale, detectedLocale);
                if (user is null || toWrite is null)
                    return Observable.Empty<MeshNode>();

                return hub.GetMeshNodeStream(userPath).Update(current =>
                {
                    var u = current.ContentAs<User>(options);
                    // Write-once, re-checked against the LATEST state: if a language landed
                    // between the read above and this write, leave it untouched.
                    if (u is null || !string.IsNullOrWhiteSpace(u.Locale))
                        return current;
                    return current with { Content = u with { Locale = toWrite } };
                });
            });
    }
}
