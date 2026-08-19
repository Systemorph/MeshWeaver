using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>What clicking the header build chip does.</summary>
public enum PlatformUpdateChipAction
{
    /// <summary>Open the About page, which carries the full build identity the tooltip summarises.</summary>
    OpenAbout,

    /// <summary>
    /// Hard-reload the browser, moving this session onto whatever instance is serving now. Offered
    /// ONLY for <see cref="PlatformUpdateAvailability.UpdateAvailable"/> — see
    /// <see cref="PlatformUpdateChip.Describe"/> for why a held update deliberately does not get it.
    /// </summary>
    Refresh,
}

/// <summary>
/// Everything the header build chip renders, as a plain record — so the wording, the version it
/// names and the action it offers are unit-testable without a hub or a rendered circuit (the same
/// reason <see cref="Settings.AboutSettingsTab.UpdateStatusMarkdown"/> takes its localizer as a
/// function). The component holds only markup and wiring.
/// </summary>
/// <param name="DisplayText">
/// What the header shows: when this build was last deployed, or <c>null</c> when that is unknown
/// (the glyph still renders, so the button is never blank-but-clickable).
/// </param>
/// <param name="Tooltip">The full sentence on hover — build identity in every state.</param>
/// <param name="IsUpdate">Whether an update is pending, which is what the chip styles on.</param>
/// <param name="Action">What a click does.</param>
public record PlatformUpdateChip(
    string? DisplayText,
    string Tooltip,
    bool IsUpdate,
    PlatformUpdateChipAction Action)
{
    /// <summary>How many characters of the commit sha survive into the header.</summary>
    private const int ShaDisplayLength = 7;

    /// <summary>
    /// The build id as the HEADER shows it: the version, with SemVer build metadata (everything
    /// after <c>+</c> — the commit sha) abbreviated.
    ///
    /// <para>A local build's full version is <c>3.0.0-rc4.ci.0+8278244204d7e3d0cc95b1461c825383cf0875a9</c>:
    /// 48 characters, 40 of them hash, in a top bar between two icon buttons. The sha is kept
    /// rather than dropped because on a local build every version is <c>ci.0</c> — the sha is the
    /// only part that tells one build from the next. The full string stays on the tooltip and on
    /// the About page, so nothing is lost, it is one hover away.</para>
    /// </summary>
    public static string ShortVersion(string version)
    {
        if (string.IsNullOrEmpty(version)) return version;
        var plus = version.IndexOf('+');
        if (plus < 0) return version;                                  // no build metadata — already short

        var sha = version[(plus + 1)..];
        return sha.Length <= ShaDisplayLength
            ? version
            : $"{version[..(plus + 1)]}{sha[..ShaDisplayLength]}";
    }

    /// <summary>
    /// WHEN this build started serving, compact enough for the header, in the VIEWER's zone.
    ///
    /// <para>The version says WHICH build; this says SINCE WHEN, and a re-deploy of the same image
    /// changes only the second. Month-day-and-time without a year because the header is not an
    /// audit log — it answers "did this roll just now, or has it been up for days?" at a glance,
    /// and the full stamp is on the About page.</para>
    ///
    /// <para>🚨 The month is NUMERIC, never <c>MMM</c>. A month abbreviation formatted invariantly
    /// renders "Aug" to a German viewer exactly as it does to an English one — English text
    /// hard-coded into a localized UI, in the one place it is easiest to miss because it looks like
    /// formatting rather than copy. Numeric needs no catalog key and no culture at all, and it
    /// keeps the <c>yyyy-MM-dd HH:mm</c> field order the About page prints, minus the year the
    /// header has no room for.</para>
    ///
    /// <para>Returns <c>null</c> when the start time is unknown, so the caller renders nothing
    /// rather than an epoch date.</para>
    /// </summary>
    public static string? ShortStartedAt(DateTimeOffset startedAtUtc, string? viewerZoneId)
        => startedAtUtc == default
            ? null
            : DisplayTimeExtensions.ToDisplayTime(startedAtUtc, viewerZoneId)
                .ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Derives the chip from the update verdict and this process's identity.
    ///
    /// <para>The chip renders in EVERY state, including <see cref="PlatformUpdateAvailability.UpToDate"/>
    /// and <see cref="PlatformUpdateAvailability.Unknown"/>. That is what makes it a confirmation
    /// rather than an announcement: because the running build is always on screen, a refresh that
    /// lands on a new instance is visible as the value changing. An indicator that appeared only
    /// while an update was pending could say something was available but never that you had
    /// arrived — which is the question ahead of starting a thread round.</para>
    ///
    /// <para>The tooltip names the INSTANCE as well as the build, because two replicas can run the
    /// same build — the version alone cannot settle "did my session move?".</para>
    ///
    /// <para>🚨 The visible text is the deployment TIME, in every state — never a version. A build
    /// id is an identifier an ordinary reader cannot act on, and the header is the busiest strip in
    /// the portal; "Last deployed 08-18 15:35" answers the question people actually bring to it.
    /// The exact build did not disappear: it is on the tooltip, and in full on the About page. That
    /// holds even with an update pending — the glyph is what says one is waiting, and swapping the
    /// text for a newer unreadable identifier would not tell anyone more.</para>
    ///
    /// <para>🚨 A held update reads differently from an available one and offers NO refresh. Per
    /// <see cref="PlatformUpdateAvailability.UpdateHeld"/> a hold must never be silent; but
    /// refreshing cannot clear one, and offering the button anyway would teach the user to click at
    /// a problem they cannot fix from the header.</para>
    /// </summary>
    /// <param name="status">The verdict from <see cref="PlatformUpdateStatus.Observe"/>.</param>
    /// <param name="runningVersion">The build serving this session.</param>
    /// <param name="instanceName">The serving process — the pod name under Kubernetes.</param>
    /// <param name="deployedAtUtc">When this build started serving, UTC; <c>default</c> if unknown.</param>
    /// <param name="viewerZoneId">The viewer's IANA zone — the time is never shown as bare UTC.</param>
    /// <param name="localize">Key → text; taken as a function so the wording is testable.</param>
    public static PlatformUpdateChip Describe(
        PlatformUpdateStatus status,
        string runningVersion,
        string instanceName,
        DateTimeOffset deployedAtUtc,
        string? viewerZoneId,
        Func<string, string> localize)
    {
        // Build identity, in every state — the half of the tooltip that answers "where am I?".
        var running = $"{localize("about.version")}: {runningVersion} · {localize("about.instance")}: {instanceName}";

        // The one visible string, shared by every state: WHEN, not WHICH.
        var deployed = ShortStartedAt(deployedAtUtc, viewerZoneId) is { } when
            ? $"{localize("about.lastDeployed")} {when}"
            : null;

        return status.Availability switch
        {
            PlatformUpdateAvailability.UpdateAvailable => new(
                deployed,
                $"{localize("about.updateAvailable")} — {status.LatestVersion}. {running}. "
                + localize("ui.updateRefreshHint"),
                IsUpdate: true,
                PlatformUpdateChipAction.Refresh),

            PlatformUpdateAvailability.UpdateHeld => new(
                deployed,
                $"{localize("about.updateHeld")} — {status.LatestVersion}. {running}.",
                IsUpdate: true,
                PlatformUpdateChipAction.OpenAbout),

            // UpToDate and Unknown are the same chip on purpose. Unknown means nothing is polling,
            // so claiming "up to date" would be an unfounded verdict (PlatformUpdateAvailability
            // .Unknown) — but the running build is a fact either way, and it is the whole point.
            _ => new(deployed, running, IsUpdate: false, PlatformUpdateChipAction.OpenAbout),
        };
    }
}
