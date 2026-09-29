using System.Reactive.Linq;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// 🚨 <b>A held package update becomes ONE blocking ticket on the control instance</b> — the
/// portal's <see cref="IPackageHoldDispatch"/> (policy <c>package-min-mesh-version</c>: "when we
/// cannot advance we file a blocking ticket, through dispatch").
///
/// <para><b>No new channel.</b> The ticket rides <see cref="SelfUpdateHandover.Announce"/> — the
/// signed POST into the control inbox (<c>Hosting:ControlInbox:Url</c> +
/// <c>Hosting:ControlInbox:Secret</c>, or the in-process delivery on the control instance itself)
/// that every self-update announcement already takes — as event
/// <see cref="SelfUpdateHandover.PackageHeldEvent"/>, with <c>severity: blocking</c>. The instance
/// never opens a GitHub issue; the control plane's triage decides what the ticket becomes.</para>
///
/// <para><b>No route is an answer, not a crash.</b> When the instance declares no control inbox
/// (or no record id), <see cref="Dispatch"/> errors with <see cref="SelfUpdateHandover.Missing"/>'s
/// sentence, which the catalog records on the install record as "NOT dispatched: …" and logs at
/// Warning.</para>
///
/// <para>🚨 <b>The control-plane half is MeshWeaver.Plugins'</b>, and until it lands an accepted
/// ticket is stored and then dropped by the inbox watcher: <c>PlatformBuildInboxWatcher</c> admits
/// only a self-update announcement under a deployment's own key, and <c>TriageIntake.IsTriageKind</c>
/// does not list <see cref="SelfUpdateHandover.PackageHeldEvent"/>. See
/// <c>Doc/Architecture/ModuleAdoptionPolicy</c> → "Blocking tickets".</para>
/// </summary>
public sealed class PackageHoldHandover : IPackageHoldDispatch
{
    /// <summary>The <c>severity</c> every held-update ticket carries.</summary>
    public const string Blocking = "blocking";

    /// <summary>The <c>reporter</c> field of a held-update ticket.</summary>
    public const string Reporter = "package-floor";

    /// <summary>The announcement a ticket is sent as (pure).</summary>
    /// <param name="ticket">The ticket.</param>
    /// <param name="now">Now.</param>
    public static SelfUpdateHandover.Announcement AnnouncementOf(PackageHoldTicket ticket, DateTimeOffset now) =>
        new()
        {
            Event = SelfUpdateHandover.PackageHeldEvent,
            CurrentVersion = ticket.Running,
            Package = ticket.Package,
            HeldVersion = ticket.HeldVersion,
            Floor = ticket.Floor,
            InstalledVersion = ticket.InstalledVersion,
            Reason = ticket.Summary,
            Unblocks = ticket.Unblocks,
            Severity = Blocking,
            DetectedAt = SelfUpdateHandover.Stamp(now),
            Reporter = Reporter,
        };

    /// <inheritdoc />
    public IObservable<string> Dispatch(IMessageHub hub, PackageHoldTicket ticket)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<PackageHoldHandover>();
        return Handover(hub, logger)
            .Announce(AnnouncementOf(ticket, DateTimeOffset.UtcNow))
            .Select(outcome => $"{outcome.Detail} ({outcome.Destination})");
    }

    /// <summary>The hand-over the ticket rides. Virtual seam for tests that present a configured
    /// control inbox without a second mesh.</summary>
    internal Func<IMessageHub, ILogger?, SelfUpdateHandover> Handover { get; init; } =
        (hub, logger) => new SelfUpdateHandover(hub, logger);
}
