using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The ONE seam that resolves the current viewer's <see cref="PresentationScreen"/> — the
/// counterpart of <c>AccessService.ViewerZoneId()</c> / <c>ViewerLocale()</c> for the privacy
/// screen (issue #1803).
///
/// <para><b>Identity is read exactly the way the time zone is</b>: off the live
/// <see cref="AccessContext"/> (request-scoped <c>Context</c> first, then the per-circuit
/// <c>CircuitContext</c>), resolved ONCE on the render turn by the caller and then passed down as a
/// value. Never read an ambient context on a later emission — after a scheduler or I/O-pool hop the
/// <c>AsyncLocal</c> is gone and the answer silently becomes "nobody", which for a privacy screen
/// means "hide nothing".</para>
///
/// <para><b>The preference VALUE, unlike the time zone, is read LIVE off the viewer's own
/// <c>User</c> node</b> rather than off a field projected onto the AccessContext at circuit open.
/// That difference is deliberate and it is the whole feature: a time zone changes about never, so a
/// snapshot taken when the circuit opened is always right; a presentation toggle is flipped in the
/// seconds BEFORE a screen share, and a snapshot taken at circuit open would still say "off" — the
/// user would toggle, see the header light up, and share a portal that is still listing everything.
/// A stale time zone is a cosmetic lag; a stale screen is the leak the feature exists to prevent.
/// Binding to <c>GetMeshNodeStream(viewer)</c> — the process-wide shared handle, one per path, that
/// the portal already holds open for the viewer's own home — makes the toggle instant and costs no
/// new subscription machinery.</para>
/// </summary>
public static class PresentationScreenExtensions
{
    /// <summary>
    /// The current viewer's id (their partition key), resolved from the live
    /// <see cref="AccessContext"/> — request-scoped first, then per-circuit. Empty/null when there
    /// is no signed-in viewer. Capture this on the render turn; do not re-read it later.
    /// </summary>
    /// <param name="accessService">The access service, or null in a host that has none.</param>
    public static string? ViewerId(this AccessService? accessService)
    {
        var fromRequest = accessService?.Context?.ObjectId;
        if (!string.IsNullOrWhiteSpace(fromRequest))
            return fromRequest;
        var fromCircuit = accessService?.CircuitContext?.ObjectId;
        return string.IsNullOrWhiteSpace(fromCircuit) ? null : fromCircuit;
    }

    /// <summary>
    /// True when <paramref name="viewerId"/> is a real person's partition rather than an
    /// infrastructure principal — an anonymous visitor, the system identity and a hub credential
    /// all have no profile to read and are always <see cref="PresentationScreen.Off"/>.
    /// </summary>
    /// <param name="viewerId">The id from <see cref="ViewerId"/>.</param>
    public static bool IsPersonalViewer(string? viewerId)
        => !string.IsNullOrWhiteSpace(viewerId)
           && !string.Equals(viewerId, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase)
           && !string.Equals(viewerId, WellKnownUsers.Public, StringComparison.OrdinalIgnoreCase)
           && !string.Equals(viewerId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase)
           && !AccessService.LooksLikeHubPrincipal(viewerId);

    /// <summary>
    /// The live screen of the viewer <paramref name="accessService"/> currently describes. Emits
    /// immediately and again on every change to their profile, so a toggle re-renders every bound
    /// surface with no reload. An anonymous / system / hub caller gets a single
    /// <see cref="PresentationScreen.Off"/>.
    /// </summary>
    /// <param name="accessService">Resolves who is asking. Null ⇒ off.</param>
    /// <param name="hub">The hub whose node-stream cache and serializer options are used.</param>
    public static IObservable<PresentationScreen> ViewerScreen(
        this AccessService? accessService, IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return ScreenOf(hub, accessService.ViewerId());
    }

    /// <summary>
    /// The live screen of a NAMED viewer — the form used where the identity was already captured
    /// on the render turn (a layout area that resolved it at handler entry) and must not be
    /// re-derived from an ambient context that no longer flows.
    /// </summary>
    /// <param name="hub">The hub whose node-stream cache and serializer options are used.</param>
    /// <param name="viewerId">The viewer's partition key, from <see cref="ViewerId"/>.</param>
    public static IObservable<PresentationScreen> ScreenOf(IMessageHub hub, string? viewerId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (!IsPersonalViewer(viewerId))
            return Observable.Return(PresentationScreen.Off);

        var options = hub.JsonSerializerOptions;
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Mesh.Security.PresentationScreen");
        var projected = hub.GetMeshNodeStream(viewerId!)
            .Select(node => Project(node, options));
        return Observable.Defer(() => LastKnownOnFault(projected, viewerId!, logger))
            .DistinctUntilChanged();
    }

    /// <summary>
    /// The screen a viewer's own <c>User</c> node describes. Content is read through
    /// <c>ContentAs&lt;User&gt;</c> — never a bare <c>is JsonElement</c> probe — because the same
    /// node arrives typed on the owning hub, as a <see cref="JsonElement"/> across a query seam, and
    /// as a <c>JsonNode</c> from the node builders, and a shape test that handles only one of those
    /// would silently answer "nothing hidden" for everybody else.
    /// </summary>
    /// <param name="viewerNode">The viewer's <c>User</c> node, or null when it does not exist yet.</param>
    /// <param name="options">Serializer options used to read the node's content.</param>
    public static PresentationScreen Project(MeshNode? viewerNode, JsonSerializerOptions options)
        => viewerNode is null
            ? PresentationScreen.Off
            : PresentationScreen.From(viewerNode.ContentAs<User>(options));

    /// <summary>
    /// Keeps the LAST KNOWN screen when the profile stream faults, falling back to
    /// <see cref="PresentationScreen.Off"/> only when nothing was ever read — and logs the fault
    /// rather than swallowing it.
    ///
    /// <para>Both halves matter. Resetting an ACTIVE screen to "off" on a transient read fault is
    /// the leak this feature exists to prevent, and it would happen mid-presentation, silently.
    /// Refusing to emit at all instead would hang every surface that waits for the first screen —
    /// a spinner where a page should be. Holding the last value keeps the screen up across the
    /// blip; the log line is what makes the degradation findable afterwards.</para>
    ///
    /// <para>Internal + observable-in/observable-out so a faulting profile stream is directly
    /// testable — that leg is the one a live-mesh test cannot arrange.</para>
    /// </summary>
    /// <param name="projected">The projected screen stream.</param>
    /// <param name="viewerId">The viewer, for the log line.</param>
    /// <param name="logger">Where the fault is reported. Null in hosts without logging.</param>
    internal static IObservable<PresentationScreen> LastKnownOnFault(
        IObservable<PresentationScreen> projected, string viewerId, ILogger? logger)
    {
        // Per-SUBSCRIPTION local (this method is only ever called inside Observable.Defer), never
        // shared and never static: two circuits watching the same viewer keep separate latches.
        var last = PresentationScreen.Off;
        return projected
            .Do(screen => last = screen)
            .Catch<PresentationScreen, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Presentation screen: the profile stream for {Viewer} faulted; holding the last "
                    + "known screen (active={Active}, marks={MarkCount}). The screen is NOT reset — "
                    + "resetting it mid-presentation is the leak it exists to prevent.",
                    viewerId, last.Active, last.MarkedPaths.Count);
                return Observable.Return(last);
            });
    }

    /// <summary>
    /// The viewer's screen SEEDED with <see cref="PresentationScreen.Off"/> — for a surface that
    /// must render whether or not the viewer has a profile to read.
    ///
    /// <para>🚨 Use this wherever the screen decides only how something is LABELLED, and the plain
    /// <see cref="ViewerScreen(LayoutAreaHost)"/> wherever painting early would LEAK. The
    /// difference is not stylistic. A surface that joins the unseeded screen into a
    /// <c>CombineLatest</c> renders nothing until that leg produces, and the leg is a subscription
    /// to the VIEWER's own <c>User</c> node — a node that need not exist (a test identity, a caller
    /// mid-onboarding, a virtual user). When it errors, <see cref="LastKnownOnFault"/> answers; when
    /// it merely never produces, the join stalls silently and forever, outside any test's
    /// method timeout, and the symptom is a wall-clock hang with no failing test to point at.</para>
    ///
    /// <para>"This viewer has no profile" is a defined, screened-safe answer — a viewer with no
    /// profile has marked nothing — so it belongs in the stream as a VALUE rather than as something
    /// to wait for. That is what the seed says.</para>
    /// </summary>
    /// <param name="screen">The viewer's screen stream.</param>
    public static IObservable<PresentationScreen> Seeded(this IObservable<PresentationScreen> screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return screen.StartWith(PresentationScreen.Off);
    }

    /// <summary>
    /// The live screen of the viewer this layout area is rendering for. Resolve it ONCE at handler
    /// entry and combine it into the area's stream; do not call it again from inside a projection.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    public static IObservable<PresentationScreen> ViewerScreen(this LayoutAreaHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Hub.ServiceProvider.GetService<AccessService>().ViewerScreen(host.Hub);
    }
}
