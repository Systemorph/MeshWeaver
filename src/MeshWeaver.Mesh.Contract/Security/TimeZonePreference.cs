using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Population + override logic for a user's <see cref="User.TimeZoneId"/> — the value that
/// drives the per-viewer timestamp DISPLAY seam (<c>AccessService.ToDisplayTime</c>, resolved
/// once onto <see cref="AccessContext.TimeZoneId"/> when the circuit context is built). Storage
/// stays UTC; this only sets the display zone.
///
/// <para>The one hard correctness property is <b>write-once</b>: the browser-detected zone is
/// written ONLY when the profile has no zone yet. A user's explicit pick (or a value written on
/// an earlier session) is never clobbered by a later session — a travelling user on a VPN/kiosk
/// that reports a different zone keeps their chosen home zone. <see cref="ShouldWrite"/> is the
/// pure decision (deterministically testable); <see cref="PopulateOnce"/> applies it reactively
/// against the live node stream, re-checking inside the write lambda so the guard holds even
/// under a concurrent write.</para>
/// </summary>
public static class TimeZonePreference
{
    /// <summary>
    /// The write-once decision. Returns the (trimmed) zone id to persist when — and only when —
    /// the profile currently has NO zone (<paramref name="currentZoneId"/> null/whitespace) and a
    /// non-empty <paramref name="detectedZoneId"/> is available. Returns <c>null</c> to mean
    /// "do not write" — either the profile already has a zone (never overwrite) or nothing was
    /// detected. Pure and deterministic; the whole write-once correctness property is pinned here.
    /// </summary>
    public static string? ShouldWrite(string? currentZoneId, string? detectedZoneId)
        => string.IsNullOrWhiteSpace(currentZoneId) && !string.IsNullOrWhiteSpace(detectedZoneId)
            ? detectedZoneId.Trim()
            : null;

    /// <summary>
    /// The named IANA zone ids available on this host, sorted, for the settings-editor picker.
    /// <see cref="TimeZoneInfo.HasIanaId"/> is honoured so the list is IANA on every platform
    /// (Windows ids are converted via <see cref="TimeZoneInfo.TryConvertWindowsIdToIanaId(string, out string?)"/>;
    /// on Linux/macOS the system ids already ARE IANA). Never fixed offsets — DST must stay
    /// automatic and per-region.
    /// </summary>
    public static ImmutableList<string> SystemZoneIds()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (zone.HasIanaId)
                ids.Add(zone.Id);
            else if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana)
                     && !string.IsNullOrEmpty(iana))
                ids.Add(iana);
        }
        return ids.ToImmutableList();
    }

    /// <summary>
    /// Reactively populates the user node's <see cref="User.TimeZoneId"/> from a browser-detected
    /// zone, ONCE. Reads the node at <paramref name="userPath"/> once off the shared node stream,
    /// and — only if the stored zone is empty (<see cref="ShouldWrite"/>) — writes the detected
    /// zone through <c>GetMeshNodeStream(userPath).Update(...)</c> (so the write carries the
    /// caller's identity, per AccessContext propagation). The write lambda re-checks the latest
    /// state and no-ops if a zone appeared in the meantime, so a non-empty value is NEVER
    /// overwritten. Emits the updated node when it wrote, or completes empty when it skipped.
    /// Cold — the caller MUST subscribe.
    /// </summary>
    public static IObservable<MeshNode> PopulateOnce(
        IMessageHub hub, string userPath, string? detectedZoneId, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(userPath) || string.IsNullOrWhiteSpace(detectedZoneId))
            return Observable.Empty<MeshNode>();

        return hub.GetMeshNodeStream(userPath)
            .Where(node => node is not null)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .SelectMany(node =>
            {
                var user = node!.ContentAs<User>(options);
                var toWrite = ShouldWrite(user?.TimeZoneId, detectedZoneId);
                if (user is null || toWrite is null)
                    return Observable.Empty<MeshNode>();

                return hub.GetMeshNodeStream(userPath).Update(current =>
                {
                    var u = current.ContentAs<User>(options);
                    // Write-once, re-checked against the LATEST state: if a zone landed
                    // between the read above and this write, leave it untouched.
                    if (u is null || !string.IsNullOrWhiteSpace(u.TimeZoneId))
                        return current;
                    return current with { Content = u with { TimeZoneId = toWrite } };
                });
            });
    }
}
