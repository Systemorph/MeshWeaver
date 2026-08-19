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
/// <param name="DisplayVersion">The version shown in the header.</param>
/// <param name="Tooltip">The full sentence on hover — build identity in every state.</param>
/// <param name="IsUpdate">Whether an update is pending, which is what the chip styles on.</param>
/// <param name="Action">What a click does.</param>
public record PlatformUpdateChip(
    string DisplayVersion,
    string Tooltip,
    bool IsUpdate,
    PlatformUpdateChipAction Action)
{
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
    /// <para>🚨 A held update reads differently from an available one and offers NO refresh. Per
    /// <see cref="PlatformUpdateAvailability.UpdateHeld"/> a hold must never be silent; but
    /// refreshing cannot clear one, and offering the button anyway would teach the user to click at
    /// a problem they cannot fix from the header.</para>
    /// </summary>
    /// <param name="status">The verdict from <see cref="PlatformUpdateStatus.Observe"/>.</param>
    /// <param name="runningVersion">The build serving this session.</param>
    /// <param name="instanceName">The serving process — the pod name under Kubernetes.</param>
    /// <param name="localize">Key → text; taken as a function so the wording is testable.</param>
    public static PlatformUpdateChip Describe(
        PlatformUpdateStatus status,
        string runningVersion,
        string instanceName,
        Func<string, string> localize)
    {
        // Build identity, in every state — the half of the tooltip that answers "where am I?".
        var running = $"{localize("about.version")}: {runningVersion} · {localize("about.instance")}: {instanceName}";

        return status.Availability switch
        {
            PlatformUpdateAvailability.UpdateAvailable => new(
                // The pending build is the actionable number, so it takes the visible slot; the
                // running one stays in the tooltip.
                status.LatestVersion ?? runningVersion,
                $"{localize("about.updateAvailable")} — {status.LatestVersion}. {running}. "
                + localize("ui.updateRefreshHint"),
                IsUpdate: true,
                PlatformUpdateChipAction.Refresh),

            PlatformUpdateAvailability.UpdateHeld => new(
                status.LatestVersion ?? runningVersion,
                $"{localize("about.updateHeld")} — {status.LatestVersion}. {running}.",
                IsUpdate: true,
                PlatformUpdateChipAction.OpenAbout),

            // UpToDate and Unknown are the same chip on purpose. Unknown means nothing is polling,
            // so claiming "up to date" would be an unfounded verdict (PlatformUpdateAvailability
            // .Unknown) — but the running build is a fact either way, and it is the whole point.
            _ => new(runningVersion, running, IsUpdate: false, PlatformUpdateChipAction.OpenAbout),
        };
    }
}
