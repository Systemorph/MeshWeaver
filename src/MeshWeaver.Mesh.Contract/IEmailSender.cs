namespace MeshWeaver.Mesh;

/// <summary>
/// Sends outbound system email. Not hub-reachable — called from Blazor click actions, the
/// invitation flow, and mesh scripts (via <see cref="HubEmailExtensions.SendEmail"/>) — so it
/// exposes a reactive shape (<see cref="IObservable{T}"/>) that composes cleanly with the rest of
/// the codebase's reactive pipelines. Implementations bridge their async leaf (e.g. Microsoft
/// Graph) via <c>Observable.FromAsync</c>.
///
/// <para>The abstraction lives in the framework so scripts and framework code can trigger mail
/// (e.g. notifications) without referencing the hosting app; the concrete sender is registered by
/// the host (see the portal's <c>GraphEmailSender</c> / <c>NoOpEmailSender</c>).</para>
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends an HTML email. The returned observable is cold — the send runs on Subscribe and
    /// emits a single <c>true</c> on success, or surfaces the failure via OnError.
    /// </summary>
    IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody);
}
